using Strela.App.ViewModels;
using Strela.Engine.Manager;
using static Strela.App.Tests.FakeDownloadManager;

namespace Strela.App.Tests;

public class MainViewModelTests
{
    private const long MB = 1024 * 1024;

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
        var first = vm.Items[0];
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
    public void Refresh_OrdersByQueueOrder()
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
        Assert.Equal(new[] { b, a }, vm.Items.Select(i => i.Id));
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
    public void MoveUp_CallsManagerMove()
    {
        var fake = new FakeDownloadManager();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        fake.Snapshots.Add(Snap(a, 0));
        fake.Snapshots.Add(Snap(b, 1));
        var vm = new MainViewModel(fake);
        vm.Refresh();
        vm.MoveUpCommand.Execute(vm.Items[1]);
        Assert.Equal((b, 0), Assert.Single(fake.Moves));
    }

    [Fact]
    public void MoveDown_CallsManagerMove()
    {
        var fake = new FakeDownloadManager();
        var a = Guid.NewGuid();
        fake.Snapshots.Add(Snap(a, 0));
        fake.Snapshots.Add(Snap(Guid.NewGuid(), 1));
        var vm = new MainViewModel(fake);
        vm.Refresh();
        vm.MoveDownCommand.Execute(vm.Items[0]);
        Assert.Equal((a, 1), Assert.Single(fake.Moves));
    }

    [Fact]
    public async Task UpdateUrl_NoClipboardUrl_SetsHint()
    {
        var fake = new FakeDownloadManager();
        fake.Snapshots.Add(Snap(Guid.NewGuid(), 0, DownloadStatus.Error));
        var vm = new MainViewModel(fake) { GetClipboardText = () => "" };
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
        var vm = new MainViewModel(fake) { GetClipboardText = () => " https://new/x.bin " };
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
        var vm = new MainViewModel(fake) { ConfirmDelete = _ => { asked++; return false; } };
        vm.Refresh();
        await vm.RemoveWithFileCommand.ExecuteAsync(vm.Items[0]);
        Assert.Equal(1, asked);
        Assert.Empty(fake.Removed);

        vm.ConfirmDelete = _ => true;
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
}
