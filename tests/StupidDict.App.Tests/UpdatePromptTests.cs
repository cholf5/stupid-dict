using System.Net;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.App.Update;
using Xunit;

namespace StupidDict.App.Tests;

/// <summary>
/// The update dialog's content by state (headless): a new version offers
/// "立即更新", the previous update's verdict offers only an acknowledgement.
/// The service is built over a fake handler, so nothing touches the network.
/// </summary>
public class UpdatePromptTests
{
    [AvaloniaFact]
    public async Task AvailableState_ShowsUpdateNowAndLater()
    {
        var service = await AvailableServiceAsync();
        var prompt = new UpdatePromptWindow(service);
        prompt.Show();
        Dispatcher.UIThread.RunJobs();

        var primary = prompt.FindControl<Button>("PrimaryButton")!;
        var secondary = prompt.FindControl<Button>("SecondaryButton")!;
        Assert.True(primary.IsVisible);
        Assert.Equal(Translations.Instance.UpdateDownloadNow, primary.Content);
        Assert.True(secondary.IsVisible);
        Assert.Equal(Translations.Instance.UpdateLater, secondary.Content);
        Assert.False(prompt.FindControl<ProgressBar>("Progress")!.IsVisible);

        prompt.Close();
    }

    [AvaloniaFact]
    public void StartupUpgradedNotice_ReportsTheVersionAndOnlyAcknowledges()
    {
        var root = Path.Combine(Path.GetTempPath(), "stupiddict-prompttests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var journalPath = Path.Combine(root, "update-state.json");
        new UpdateJournal(journalPath).Save(new UpdateJournalRecord
        {
            PendingVersion = "v1.0.0",
            BackupPath = Path.Combine(root, "backup"),
            TargetPath = root,
        });

        var service = new UpdateService(
            new UpdateChecker(currentVersion: "v1.0.0"),
            new AppSettings(),
            saveSettings: () => { },
            downloader: new UpdateDownloadService(downloadDirectory: root),
            openUrl: _ => { },
            revealFile: _ => { },
            windowsInstalledCopy: false,
            journal: new UpdateJournal(journalPath),
            processPath: Path.Combine(root, "StupidDict"));

        var prompt = new UpdatePromptWindow(service);
        prompt.Show();
        Dispatcher.UIThread.RunJobs();

        var title = prompt.FindControl<TextBlock>("TitleText")!;
        Assert.Equal(string.Format(Translations.Instance.UpdateUpgradedTitleFormat, "v1.0.0"), title.Text);
        Assert.False(prompt.FindControl<Button>("PrimaryButton")!.IsVisible);
        Assert.Equal(Translations.Instance.UpdateOk, prompt.FindControl<Button>("SecondaryButton")!.Content);

        prompt.Close();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    [AvaloniaFact]
    public async Task DownloadFailure_StaysRetryableInsteadOfClosing()
    {
        var service = await AvailableServiceAsync();
        var prompt = new UpdatePromptWindow(service);
        prompt.Show();
        Dispatcher.UIThread.RunJobs();

        // No checksum sidecar is served, so the download cannot be verified and
        // must fail without ever replacing anything. WaitUntil (pumps the
        // dispatcher) rather than await: the download's continuation is posted
        // to this very dispatcher, so an awaited delay here would never let it
        // run.
        prompt.StartDownload();
        WaitUntil(() => service.State != UpdateState.Downloading);
        Dispatcher.UIThread.RunJobs();

        var primary = prompt.FindControl<Button>("PrimaryButton")!;
        // A failure keeps the dialog (and the retry) on screen.
        Assert.True(primary.IsVisible && Equals(primary.Content, Translations.Instance.UpdateDownloadNow),
            $"state={service.State} lastError={service.LastError} content='{primary.Content}' " +
            $"title='{prompt.FindControl<TextBlock>("TitleText")!.Text}'");
        prompt.Close();
    }

    /// <summary>Pumps the headless dispatcher until the condition holds (the repo's test idiom; Thread.Sleep is
    /// only the clock, the pump is RunJobs).</summary>
    private static void WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }

    private static async Task<UpdateService> AvailableServiceAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "stupiddict-prompttests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // One handler for check and download: a real network call here would
        // make the dialog test either slow or flaky.
        var handler = new FakeHandler();
        var service = new UpdateService(
            new UpdateChecker(handler, currentVersion: "v1.0.0"),
            new AppSettings(),
            saveSettings: () => { },
            downloader: new UpdateDownloadService(handler, root),
            openUrl: _ => { },
            revealFile: _ => { },
            windowsInstalledCopy: false,
            stager: new UpdateStager(os: "Linux", processId: 1, relaunchOverride: _ => true),
            journal: new UpdateJournal(Path.Combine(root, "update-state.json")),
            processPath: Path.Combine(root, "StupidDict"));
        await service.CheckNowAsync(TestContext.Current.CancellationToken);
        return service;
    }

    /// <summary>Releases/latest answers 302 → v9.9.9; every asset request is a 404.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.Contains("/releases/latest"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers = { Location = new Uri("https://github.com/cholf5/stupid-dict/releases/tag/v9.9.9") },
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
