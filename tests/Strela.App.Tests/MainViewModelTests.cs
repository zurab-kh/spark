using Strela.Engine.Integration;
using Strela.App.ViewModels;
using Strela.Engine.Manager;
using static Strela.App.Tests.FakeDownloadManager;

namespace Strela.App.Tests;

public class MainViewModelTests
{
    private const long MB = 1024 * 1024;

    [Fact]
    public void AddRequests_ForAlreadyDownloadedFile_RaisesAlreadyDownloaded()
    {
        var fake = new FakeDownloadManager();
        var done = new DownloadSnapshot(Guid.NewGuid(), "big.zip", @"C:\dl\big.zip", "https://h/big.zip",
            100, 100, 0, null, DownloadStatus.Completed, null, 0, 0);
        fake.Snapshots.Add(done);
        var vm = new MainViewModel(fake);
        vm.Refresh();
        DownloadItemViewModel? reported = null;
        vm.AlreadyDownloaded += item => reported = item;

        fake.AddReturns = done.Id;   // manager answers with the existing finished download
        vm.AddRequests(new[] { new IpcMessage("https://h/big.zip") });

        Assert.Equal(done.Id, reported?.Id);
    }

    private static (FakeDownloadManager Fake, MainViewModel Vm) ThreeDownloads()
    {
        var fake = new FakeDownloadManager();
        for (var i = 0; i < 3; i++)
            fake.Snapshots.Add(new DownloadSnapshot(Guid.NewGuid(), $"f{i}.zip", $@"C:\dl\f{i}.zip", $"https://h/f{i}.zip",
                100, 100, 0, null, DownloadStatus.Completed, null, 0, i));
        var vm = new MainViewModel(fake);
        vm.Refresh();
        return (fake, vm);
    }

    [Fact]
    public async Task RemoveSelectedWithFile_RemovesAllSelected_AfterOneConfirmation()
    {
        var (fake, vm) = ThreeDownloads();
        var prompts = new List<string>();
        var keptId = vm.Items[1].Id;
        vm.ConfirmDelete = m => { prompts.Add(m); return Task.FromResult(true); };

        await vm.RemoveSelectedWithFileCommand.ExecuteAsync(new List<object> { vm.Items[0], vm.Items[2] });

        Assert.Equal("Удалить 2 загрузки вместе с файлами на диске?", Assert.Single(prompts));
        Assert.Equal(2, fake.Removed.Count);
        Assert.All(fake.Removed, r => Assert.True(r.DeleteFile));
        Assert.DoesNotContain(fake.Removed, r => r.Id == keptId);
    }

    [Fact]
    public async Task RemoveSelectedWithFile_Declined_RemovesNothing()
    {
        var (fake, vm) = ThreeDownloads();
        vm.ConfirmDelete = _ => Task.FromResult(false);

        await vm.RemoveSelectedWithFileCommand.ExecuteAsync(vm.Items.Cast<object>().ToList());

        Assert.Empty(fake.Removed);
    }

    [Fact]
    public async Task RemoveSelected_WithoutFile_DoesNotAsk()
    {
        var (fake, vm) = ThreeDownloads();
        vm.ConfirmDelete = _ => throw new InvalidOperationException("must not ask");

        await vm.RemoveSelectedCommand.ExecuteAsync(vm.Items.Cast<object>().ToList());

        Assert.Equal(3, fake.Removed.Count);
        Assert.All(fake.Removed, r => Assert.False(r.DeleteFile));
    }

    [Theory]
    [InlineData(1, "Удалить «f2.zip» вместе с файлом на диске?")]   // newest first: f2 was added last
    [InlineData(3, "Удалить 3 загрузки вместе с файлами на диске?")]
    public async Task RemoveSelectedWithFile_PromptText(int count, string expected)
    {
        var (_, vm) = ThreeDownloads();
        string? prompt = null;
        vm.ConfirmDelete = m => { prompt = m; return Task.FromResult(false); };

        await vm.RemoveSelectedWithFileCommand.ExecuteAsync(vm.Items.Take(count).Cast<object>().ToList());

        Assert.Equal(expected, prompt);
    }

    [Fact]
    public void AddLinks_NewLink_DoesNotRaiseAlreadyDownloaded()
    {
        var fake = new FakeDownloadManager();
        var vm = new MainViewModel(fake);
        var raised = false;
        vm.AlreadyDownloaded += _ => raised = true;

        vm.AddLinks("https://h/new.zip");

        Assert.False(raised);
    }

    [Fact]
    public void AddLinks_SplitsLinesIgnoresJunk()
    {
        var fake = new FakeDownloadManager();
        var vm = new MainViewModel(fake);
        Assert.Equal(2, vm.AddLinks("https://a/x.zip\nmusor\n http://b/y.iso "));
        Assert.Equal(2, fake.Added.Count);
        Assert.Null(vm.InputHint);
    }

    [Fact]
    public void AddRequests_PassesRefererHeadersCookiesAndFileName()
    {
        var fake = new FakeDownloadManager();
        var vm = new MainViewModel(fake);
        var message = new Strela.Engine.Integration.IpcMessage("https://h/x.zip", "https://h/page",
            new Dictionary<string, string> { ["User-Agent"] = "UA" }, "a=1", "x.zip");

        Assert.Equal(1, vm.AddRequests(new[] { message, new Strela.Engine.Integration.IpcMessage("ftp://h/y") }));

        var request = Assert.Single(fake.Added);
        Assert.Equal("https://h/x.zip", request.Url.AbsoluteUri);
        Assert.Equal("https://h/page", request.Referer);
        Assert.Equal("a=1", request.Cookies);
        Assert.Equal("x.zip", request.FileName);
        Assert.Equal("UA", request.Headers!["User-Agent"]);
    }

    [Fact]
    public void AddLinks_None_SetsHint()
    {
        var vm = new MainViewModel(new FakeDownloadManager());
        Assert.Equal(0, vm.AddLinks("привет"));
        Assert.Equal("Не найдено ссылок", vm.InputHint);
    }

    [Fact]
    public void Refresh_UpdatesInPlace()
    {
        var fake = new FakeDownloadManager();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        fake.Snapshots.Add(Snap(a, 0, downloaded: 10));
        fake.Snapshots.Add(Snap(b, 1));
        var vm = new MainViewModel(fake);
        vm.Refresh();
        var first = vm.Items.Single(i => i.Id == a);
        Assert.Equal(2, vm.Items.Count);
        Assert.Equal(0.1, first.Progress, 3);

        fake.Snapshots.Clear();
        fake.Snapshots.Add(Snap(a, 0, downloaded: 68));
        vm.Refresh();

        Assert.Single(vm.Items);
        Assert.Same(first, vm.Items[0]);
        Assert.Equal(0.68, first.Progress, 3);
        Assert.Equal("68%", first.ProgressText);
    }

    [Fact]
    public void Refresh_SameAddedTime_LaterInQueueOnTop()
    {
        var fake = new FakeDownloadManager();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        fake.Snapshots.Add(Snap(a, 0));
        fake.Snapshots.Add(Snap(b, 1));
        var vm = new MainViewModel(fake);
        vm.Refresh();
        fake.Snapshots.Clear();
        fake.Snapshots.Add(Snap(a, 5));
        fake.Snapshots.Add(Snap(b, 2));
        vm.Refresh();
        Assert.Equal(new[] { a, b }, vm.Items.Select(i => i.Id));
    }

    [Fact]
    public void Refresh_RaisesOnlyChangedProps()
    {
        var fake = new FakeDownloadManager();
        fake.Snapshots.Add(Snap(Guid.NewGuid(), 0, downloaded: 30, bps: 5 * MB));
        var vm = new MainViewModel(fake);
        vm.Refresh();
        var raised = new List<string?>();
        vm.Items[0].PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.PropertyChanged += (_, e) => raised.Add("vm." + e.PropertyName);
        vm.Refresh();
        Assert.Empty(raised);

        fake.Snapshots[0] = fake.Snapshots[0] with { DownloadedBytes = 31 };
        vm.Refresh();
        Assert.Contains("Progress", raised);
        Assert.DoesNotContain("FileName", raised);
        Assert.DoesNotContain("SpeedText", raised);
    }

    [Fact]
    public async Task UpdateUrl_NoClipboardUrl_SetsHint()
    {
        var fake = new FakeDownloadManager();
        fake.Snapshots.Add(Snap(Guid.NewGuid(), 0, DownloadStatus.Error));
        var vm = new MainViewModel(fake) { GetClipboardText = () => Task.FromResult<string?>("") };
        vm.Refresh();
        await vm.UpdateUrlCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal("Скопируйте новую ссылку в буфер обмена", vm.InputHint);
        Assert.Empty(fake.UrlUpdates);
    }

    [Fact]
    public async Task UpdateUrl_UsesClipboardUrl()
    {
        var fake = new FakeDownloadManager();
        var id = Guid.NewGuid();
        fake.Snapshots.Add(Snap(id, 0, DownloadStatus.Error));
        var vm = new MainViewModel(fake) { GetClipboardText = () => Task.FromResult<string?>(" https://new/x.bin ") };
        vm.Refresh();
        await vm.UpdateUrlCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal((id, new Uri("https://new/x.bin")), Assert.Single(fake.UrlUpdates));
    }

    [Fact]
    public async Task RemoveWithFile_AsksConfirmation()
    {
        var fake = new FakeDownloadManager();
        var id = Guid.NewGuid();
        fake.Snapshots.Add(Snap(id, 0));
        var asked = 0;
        var vm = new MainViewModel(fake) { ConfirmDelete = _ => { asked++; return Task.FromResult(false); } };
        vm.Refresh();
        await vm.RemoveWithFileCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal(1, asked);
        Assert.Empty(fake.Removed);

        vm.ConfirmDelete = _ => Task.FromResult(true);
        await vm.RemoveWithFileCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal((id, true), Assert.Single(fake.Removed));
        Assert.Empty(vm.Items);
    }

    [Theory]
    [InlineData(DownloadStatus.Completed, "Готово")]
    [InlineData(DownloadStatus.Queued, "В очереди")]
    [InlineData(DownloadStatus.Paused, "Пауза")]
    [InlineData(DownloadStatus.Error, "Сервер вернул 403")]
    public void ProgressText_ByStatus(DownloadStatus st, string text)
    {
        var fake = new FakeDownloadManager();
        fake.Snapshots.Add(Snap(Guid.NewGuid(), 0, st, downloaded: 50, statusText: "Сервер вернул 403"));
        var vm = new MainViewModel(fake);
        vm.Refresh();
        Assert.Equal(text, vm.Items[0].ProgressText);
    }

    [Fact]
    public void Totals_Text()
    {
        var fake = new FakeDownloadManager();
        fake.Snapshots.Add(Snap(Guid.NewGuid(), 0, DownloadStatus.Downloading, bps: 42 * MB));
        fake.Snapshots.Add(Snap(Guid.NewGuid(), 1, DownloadStatus.Queued));
        var vm = new MainViewModel(fake);
        vm.Refresh();
        Assert.Equal("Всего: 42 МБ/с · Активно 1 · В очереди 1", vm.TotalsText);
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Items_NewestFirst_WhateverTheQueueOrder()
    {
        var fake = new FakeDownloadManager();
        Guid old = Guid.NewGuid(), mid = Guid.NewGuid(), last = Guid.NewGuid();
        // The queue was reordered: the newest is first in it, the oldest last.
        fake.Snapshots.Add(Snap(last, 0, name: "last.zip") with { AddedAt = T0.AddMinutes(2) });
        fake.Snapshots.Add(Snap(old, 1, name: "old.zip") with { AddedAt = T0 });
        fake.Snapshots.Add(Snap(mid, 2, name: "mid.zip") with { AddedAt = T0.AddMinutes(1) });
        var vm = new MainViewModel(fake);

        vm.Refresh();

        Assert.Equal(new[] { last, mid, old }, vm.Items.Select(i => i.Id));
    }

    [Fact]
    public void NewDownload_AppearsOnTop()
    {
        var (fake, vm) = ThreeDownloads();
        var added = Guid.NewGuid();
        fake.Snapshots.Add(Snap(added, 3, name: "new.zip") with { AddedAt = T0.AddDays(1) });

        vm.Refresh();

        Assert.Equal(added, vm.Items[0].Id);
        Assert.Equal(4, vm.Items.Count);
    }

    private static (FakeDownloadManager Fake, MainViewModel Vm) Named(params (string Name, string Url, DownloadStatus Status)[] items)
    {
        var fake = new FakeDownloadManager();
        for (var i = 0; i < items.Length; i++)
            fake.Snapshots.Add(Snap(Guid.NewGuid(), i, items[i].Status, name: items[i].Name) with
            {
                Url = items[i].Url,
                AddedAt = T0.AddMinutes(i),
            });
        var vm = new MainViewModel(fake);
        vm.Refresh();
        return (fake, vm);
    }

    [Fact]
    public void Search_ByNameOrLink_EveryWordMustMatch_IgnoringCase()
    {
        var (_, vm) = Named(
            ("A320 AUA Pack.zip", "https://cdn.flightsim.to/a320.zip", DownloadStatus.Completed),
            ("IniBuilds Global Jet Manual.pdf", "https://inibuilds.com/manual.pdf", DownloadStatus.Completed),
            ("A350 Livery.zip", "https://github.com/x/a350.zip", DownloadStatus.Completed));

        vm.SearchText = "a320";
        Assert.Equal(new[] { "A320 AUA Pack.zip" }, vm.Items.Select(i => i.FileName));

        vm.SearchText = "GITHUB";   // only in the link
        Assert.Equal(new[] { "A350 Livery.zip" }, vm.Items.Select(i => i.FileName));

        vm.SearchText = "  zip   livery ";
        Assert.Equal(new[] { "A350 Livery.zip" }, vm.Items.Select(i => i.FileName));

        vm.SearchText = "";
        Assert.Equal(3, vm.Items.Count);
        Assert.Equal("A350 Livery.zip", vm.Items[0].FileName);   // newest-first order is back
    }

    [Fact]
    public void Search_NothingFound_KeepsCountersForAllDownloads()
    {
        var (_, vm) = Named(
            ("a.zip", "https://h/a.zip", DownloadStatus.Downloading),
            ("b.zip", "https://h/b.zip", DownloadStatus.Queued));
        Assert.True(vm.HasDownloads);
        Assert.False(vm.NothingFound);

        vm.SearchText = "nope";

        Assert.Empty(vm.Items);
        Assert.True(vm.HasDownloads);
        Assert.True(vm.NothingFound);
        Assert.Equal("Активно 1", vm.ActiveText);
        Assert.Equal("В очереди 1", vm.QueuedText);
    }

    [Fact]
    public void NoDownloads_IsNotNothingFound()
    {
        var vm = new MainViewModel(new FakeDownloadManager()) { SearchText = "x" };

        Assert.False(vm.HasDownloads);
        Assert.False(vm.NothingFound);
    }

    [Fact]
    public void Search_DownloadRemovedWhileHidden_IsForgotten()
    {
        var (fake, vm) = Named(("a.zip", "https://h/a.zip", DownloadStatus.Completed), ("b.zip", "https://h/b.zip", DownloadStatus.Completed));
        vm.SearchText = "a.zip";
        fake.Snapshots.RemoveAll(s => s.FileName == "b.zip");

        vm.SearchText = "";

        Assert.Equal(new[] { "a.zip" }, vm.Items.Select(i => i.FileName));
        Assert.False(vm.Contains("https://h/b.zip"));
    }

    [Fact]
    public void StartEarlier_TakesThePlaceOfThePreviousWaitingDownload()
    {
        // Queue: 0 downloading, 1 waiting, 2 done, 3 waiting.
        var (fake, vm) = Named(
            ("run.zip", "https://h/0", DownloadStatus.Downloading),
            ("wait1.zip", "https://h/1", DownloadStatus.Queued),
            ("done.zip", "https://h/2", DownloadStatus.Completed),
            ("wait2.zip", "https://h/3", DownloadStatus.Queued));
        var wait2 = vm.Items.Single(i => i.FileName == "wait2.zip");

        vm.StartEarlierCommand.Execute(wait2);

        Assert.Equal((wait2.Id, 1), Assert.Single(fake.Moves));
    }

    [Fact]
    public void StartLater_TakesThePlaceOfTheNextWaitingDownload()
    {
        var (fake, vm) = Named(
            ("wait1.zip", "https://h/0", DownloadStatus.Queued),
            ("run.zip", "https://h/1", DownloadStatus.Downloading),
            ("wait2.zip", "https://h/2", DownloadStatus.Queued));
        var wait1 = vm.Items.Single(i => i.FileName == "wait1.zip");

        vm.StartLaterCommand.Execute(wait1);

        Assert.Equal((wait1.Id, 2), Assert.Single(fake.Moves));
    }

    [Fact]
    public void StartEarlier_NotWaitingOrAlreadyFirst_DoesNothing()
    {
        var (fake, vm) = Named(
            ("run.zip", "https://h/0", DownloadStatus.Downloading),
            ("wait.zip", "https://h/1", DownloadStatus.Queued));

        vm.StartEarlierCommand.Execute(vm.Items.Single(i => i.FileName == "run.zip"));
        vm.StartEarlierCommand.Execute(vm.Items.Single(i => i.FileName == "wait.zip"));   // no waiting one before it
        vm.StartLaterCommand.Execute(vm.Items.Single(i => i.FileName == "wait.zip"));

        Assert.Empty(fake.Moves);
    }

    [Fact]
    public void Waiting_ShowsItsNumberInTheQueue()
    {
        var (_, vm) = Named(
            ("wait1.zip", "https://h/0", DownloadStatus.Queued),
            ("run.zip", "https://h/1", DownloadStatus.Downloading),
            ("wait2.zip", "https://h/2", DownloadStatus.Queued));

        Assert.Equal("В очереди №1", vm.Items.Single(i => i.FileName == "wait1.zip").StatusLabel);
        Assert.Equal("В очереди №2", vm.Items.Single(i => i.FileName == "wait2.zip").StatusLabel);
        Assert.Equal("", vm.Items.Single(i => i.FileName == "run.zip").StatusLabel);
    }

    [Fact]
    public void AddLinks_ClearsSearch_SoTheNewDownloadIsVisible()
    {
        var (fake, vm) = Named(("a.zip", "https://h/a.zip", DownloadStatus.Completed));
        vm.SearchText = "a.zip";

        vm.AddLinks("https://h/b.zip");

        Assert.Equal("", vm.SearchText);
        Assert.Single(fake.Added);
    }

    [Fact]
    public void LastDownloadRemoved_ClearsSearch()
    {
        var (fake, vm) = Named(("a.zip", "https://h/a.zip", DownloadStatus.Completed));
        vm.SearchText = "a.zip";
        fake.Snapshots.Clear();

        vm.Refresh();

        Assert.Equal("", vm.SearchText);
        Assert.False(vm.HasDownloads);
        Assert.Empty(vm.Items);
    }
}
