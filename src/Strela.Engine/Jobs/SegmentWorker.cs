using System.Buffers;
using System.Net;
using Strela.Engine.Http;
using Strela.Engine.Manager;
using Strela.Engine.Segments;

namespace Strela.Engine.Jobs;

/// <summary>
/// One connection of a <see cref="DownloadJob"/>. Owns one segment at a time: GET Range from its Position,
/// read into a pooled buffer, Reserve → write → Commit; when the segment is done it asks the job for more work
/// (an unowned segment or a stolen tail) and continues over a new request. Never throws out of <see cref="RunAsync"/>:
/// a fatal problem is handed to <see cref="DownloadJob.Fail"/>, cancellation just ends the worker.
/// </summary>
internal sealed class SegmentWorker
{
    private readonly DownloadJob _job;
    private readonly CancellationToken _token;
    private readonly RetryStreak _streak = new();
    private long _written;

    public SegmentWorker(DownloadJob job, int id, CancellationToken token)
    {
        _job = job;
        Id = id;
        _token = token;
    }

    public int Id { get; }

    public async Task RunAsync()
    {
        var buffer = ArrayPool<byte>.Shared.Rent(_job.Options.BufferBytes);
        try
        {
            if (_job.SingleStream)
                await RunSingleStreamAsync(buffer);
            else
                await RunRangedAsync(buffer);
        }
        catch (JobFailedException ex)
        {
            _job.Fail(ex.Outcome, ex.InnerException ?? ex);
        }
        catch (Exception) when (_token.IsCancellationRequested)
        {
            // Pause, or another worker failed the job. A cancelled request may surface as IOException too.
        }
        catch (Exception ex)
        {
            _job.Fail(new JobOutcome(DownloadStatus.Error, $"Внутренняя ошибка: {ex.Message}"), ex);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task RunRangedAsync(byte[] buffer)
    {
        var segment = _job.TakeWork(null);
        while (segment is not null)
        {
            while (!segment.IsDone)
            {
                var before = _written;
                try
                {
                    // false = the segment is complete (possibly cut short by a steal); true = the response ended.
                    if (await FetchRangeAsync(segment, buffer) && !segment.IsDone)
                        throw new IOException("Сервер закрыл соединение раньше времени");
                    _streak.Reset();
                }
                catch (Exception ex) when (!_token.IsCancellationRequested && ex is not JobFailedException)
                {
                    if (_written > before)
                        _streak.Reset();
                    await _job.BackOffAsync(_streak, ex, _token);
                }
            }

            segment = _job.TakeWork(segment);
        }
    }

    /// <summary>No ranges or unknown size: one request without Range from byte 0; any failure restarts from scratch.</summary>
    private async Task RunSingleStreamAsync(byte[] buffer)
    {
        while (true)
        {
            var segment = _job.RestartSingleStream();
            var before = _written;
            try
            {
                if (await FetchWholeAsync(segment, buffer))
                    return;
                throw new IOException("Сервер закрыл соединение раньше времени");
            }
            catch (Exception ex) when (!_token.IsCancellationRequested && ex is not JobFailedException)
            {
                if (_written > before)
                    _streak.Reset();
                await _job.BackOffAsync(_streak, ex, _token);
            }
        }
    }

    /// <summary>One ranged request for [Position, End). Returns true when the response body ended before the segment did.</summary>
    private async Task<bool> FetchRangeAsync(Segment segment, byte[] buffer)
    {
        var from = segment.Position;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(_token);
        stall.CancelAfter(_job.Options.StallTimeout);
        _job.ConnectionOpened();
        try
        {
            using var request = StrelaHttp.NewRequest(_job.Request, _job.Uri, from, segment.End - 1);
            using var response = await _job.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                if ((int)response.StatusCode >= 400)
                    throw HttpStatusException.From(response);
                // Task 10: 200 on a range request → fall back to single-stream from scratch.
                throw new JobFailedException(new JobOutcome(DownloadStatus.Error, "Сервер перестал поддерживать докачку"));
            }

            if (response.Content.Headers.ContentRange?.From != from)
                throw new JobFailedException(new JobOutcome(DownloadStatus.Error, "Сервер прислал не тот кусок файла"));

            await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
            return await CopyAsync(body, segment, buffer, stall);
        }
        catch (OperationCanceledException ex) when (stall.IsCancellationRequested && !_token.IsCancellationRequested)
        {
            throw new TimeoutException("Сервер не присылает данные", ex);
        }
        finally
        {
            segment.Abandon();   // reserved-but-uncommitted bytes go back; a no-op after a clean finish
            _job.ConnectionClosed();
        }
    }

    /// <summary>One request without Range. Returns true when the whole file has been written.</summary>
    private async Task<bool> FetchWholeAsync(Segment segment, byte[] buffer)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(_token);
        stall.CancelAfter(_job.Options.StallTimeout);
        _job.ConnectionOpened();
        try
        {
            using var request = StrelaHttp.NewRequest(_job.Request, _job.Uri);
            using var response = await _job.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);
            if (!response.IsSuccessStatusCode)
                throw HttpStatusException.From(response);

            await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
            if (!await CopyAsync(body, segment, buffer, stall))
                return true;                     // known length reached; anything beyond it is ignored
            if (!_job.KnownLength)
            {
                _job.FinishUnknownLength(segment.Position);
                return true;
            }

            return segment.IsDone;
        }
        catch (OperationCanceledException ex) when (stall.IsCancellationRequested && !_token.IsCancellationRequested)
        {
            throw new TimeoutException("Сервер не присылает данные", ex);
        }
        finally
        {
            segment.Abandon();
            _job.ConnectionClosed();
        }
    }

    /// <summary>
    /// Streams the body into the segment in blocks of up to BufferBytes. Returns true on end of the body,
    /// false when the segment has no room left (complete, or its End was moved back by a steal).
    /// </summary>
    private async Task<bool> CopyAsync(Stream body, Segment segment, byte[] buffer, CancellationTokenSource stall)
    {
        var stallTimeout = _job.Options.StallTimeout;
        var size = _job.Options.BufferBytes;
        while (true)
        {
            // Only this worker reserves, so Free is exactly what is left; it can only shrink meanwhile.
            var want = (int)Math.Min(size, segment.Free);
            if (want == 0)
                return false;

            var filled = 0;
            var eof = false;
            while (filled < want)
            {
                stall.CancelAfter(stallTimeout);   // re-arm: no bytes for StallTimeout = dead connection
                var read = await body.ReadAsync(buffer.AsMemory(filled, want - filled), stall.Token);
                if (read == 0)
                {
                    eof = true;                   // end of this response, not necessarily of the segment
                    break;
                }
                filled += read;
            }

            if (filled > 0)                       // never Reserve(0): Count 0 is the "segment end" sentinel
            {
                var (offset, count) = segment.Reserve(filled);
                if (count > 0)
                {
                    _job.Write(offset, buffer.AsSpan(0, count));
                    segment.Commit(count);
                    _job.AddDownloaded(count);
                    _written += count;
                }

                if (count < filled)
                    return false;                 // the rest belongs to the segment that was split off
            }

            if (eof)
                return true;
        }
    }
}
