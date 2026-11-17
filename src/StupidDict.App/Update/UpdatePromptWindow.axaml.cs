using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using StupidDict.App.Localization;

namespace StupidDict.App.Update;

/// <summary>
/// The one update UI: shows the new version, downloads it with progress,
/// offers the restart, and afterwards reports "已更新到 vX" or "上次更新未生效".
/// Owned by the main window and non-modal on purpose — a dictionary user can
/// keep looking words up while a few dozen megabytes download, and the window
/// closing (to tray or quit) cancels the transfer instead of stalling it.
/// <para>
/// All text is computed from <see cref="UpdateService"/>'s state machine in
/// <see cref="Sync"/>; the XAML only lays things out. Like every transient
/// dialog in this app the strings do not retro-refresh on a language switch.
/// </para>
/// </summary>
public partial class UpdatePromptWindow : Window
{
    private readonly UpdateService? _service;
    private readonly UpdateStartupNotice? _notice;
    private CancellationTokenSource? _downloadCancellation;
    private bool _noticeDismissed;
    // Whether the window has actually been shown: Sync runs from the
    // constructor too, and closing a window that was never opened would make
    // the Show() that follows impossible.
    private bool _opened;
    private double _progress;
    /// <summary>The dialog's own failure text (download/apply) — cleared when the user starts the next attempt.</summary>
    private string? _error;

    // XAML loader requirement (AVLN3001); production always passes a service.
    public UpdatePromptWindow() : this(null) { }

    internal UpdatePromptWindow(UpdateService? service)
    {
        InitializeComponent();
        // Its own HWND, so the dark-caption declaration is per window too.
        WindowsCaptionTheme.Attach(this);
        _service = service;
        _notice = service?.StartupNotice;
        if (_service is not null)
        {
            _service.Changed += OnServiceChanged;
        }

        Sync();
    }

    /// <summary>Testing seam: called instead of <see cref="UpdateService.RestartAndApply"/> so a headless test never swaps files.</summary>
    internal Action? RestartOverride { get; set; }

    private void OnServiceChanged()
    {
        // The service may fire from a background check; property updates must
        // land on the UI thread (Dispatcher.Post, same discipline as MainWindow).
        if (Dispatcher.UIThread.CheckAccess())
        {
            Sync();
        }
        else
        {
            Dispatcher.UIThread.Post(Sync);
        }
    }

    /// <summary>Re-renders the dialog from the current state: title/body/progress/buttons.</summary>
    internal void Sync()
    {
        var t = Translations.Instance;
        if (_service is null)
        {
            Show(t.UpdateDialogTitle, null, null, null);
            return;
        }

        // 1) The startup verdict comes first: it is about the previous update,
        // independent of whatever this process may have checked since.
        if (!_noticeDismissed && _notice is { Outcome: not UpdateStartupOutcome.None } notice)
        {
            Show(
                notice.Outcome == UpdateStartupOutcome.Upgraded
                    ? string.Format(t.UpdateUpgradedTitleFormat, notice.Version)
                    : t.UpdateNotAppliedTitle,
                notice.Outcome == UpdateStartupOutcome.Upgraded
                    ? t.UpdateUpgradedBody
                    : notice.RolledBack
                        ? t.UpdateRolledBackBody
                        : string.Format(t.UpdateBackupKeptBodyFormat, notice.BackupPath ?? ""),
                null,
                t.UpdateOk);
            return;
        }

        // 2) A failure that this dialog itself hit (download/apply), while the
        // service is back in a retryable state: the message stays until the
        // user acts again.
        if (_error is { } error)
        {
            var retryLabel = _service.State switch
            {
                UpdateState.Available => t.UpdateDownloadNow,
                UpdateState.ReadyToRestart => t.UpdateRestartNow,
                _ => null,
            };
            Show(error, null, null, t.UpdateLater, retryLabel);
            return;
        }

        var tag = _service.LastResult?.LatestVersion;
        switch (_service.State)
        {
            case UpdateState.Checking:
                Show(t.CheckingUpdate, null, null, null);
                return;

            case UpdateState.Downloading:
                Show(
                    string.Format(t.UpdateDownloadingFormat, tag),
                    null,
                    _progress,
                    t.Cancel,
                    primary: null);
                return;

            case UpdateState.ReadyToRestart:
                Show(
                    t.UpdateReadyTitle,
                    string.Format(t.UpdateReadyBodyFormat, tag),
                    null,
                    t.UpdateLater,
                    t.UpdateRestartNow);
                return;

            case UpdateState.Restarting:
                Show(t.UpdateRestarting, null, double.NaN, null);
                return;

            case UpdateState.Available:
                Show(
                    string.Format(t.UpdateAvailableTitleFormat, tag),
                    string.Format(t.UpdateAvailableBodyFormat, _service.CurrentVersion),
                    null,
                    t.UpdateLater,
                    t.UpdateDownloadNow);
                return;

            case UpdateState.Failed when _service.LastResult?.Error is { } checkError:
                Show(string.Format(t.UpdateCheckFailed, checkError), null, null, t.UpdateOk);
                return;

            default:
                // UpToDate/Idle (or a check this dialog did not start): nothing
                // to say, so the prompt closes instead of lingering empty.
                if (_opened)
                {
                    Close();
                }
                return;
        }
    }

    private void Show(string title, string? body, double? progress, string? secondary, string? primary = null)
    {
        TitleText.Text = title;
        BodyText.Text = body ?? string.Empty;
        BodyText.IsVisible = !string.IsNullOrEmpty(body);

        Progress.IsVisible = progress is not null;
        if (progress is { } value)
        {
            // NaN = no byte count to compute (the applying/restart stage):
            // an indeterminate bar instead of a frozen 0 %.
            Progress.IsIndeterminate = double.IsNaN(value);
            Progress.Value = double.IsNaN(value) ? 0 : value;
        }

        SecondaryButton.Content = secondary ?? string.Empty;
        SecondaryButton.IsVisible = !string.IsNullOrEmpty(secondary);
        PrimaryButton.Content = primary ?? string.Empty;
        PrimaryButton.IsVisible = !string.IsNullOrEmpty(primary);
    }

    private void OnPrimaryClick(object? sender, RoutedEventArgs e)
    {
        if (_service is null) return;
        switch (_service.State)
        {
            case UpdateState.Available:
                StartDownload();
                break;
            case UpdateState.ReadyToRestart:
                Restart();
                break;
        }
    }

    private void OnSecondaryClick(object? sender, RoutedEventArgs e)
    {
        // "取消" during a download and "稍后" afterwards are the same gesture:
        // stop what is running and get out of the way.
        _downloadCancellation?.Cancel();
        Close();
    }

    /// <summary>
    /// Starts the download. Deliberately not async void: the task is kept in a
    /// field, every exception lands in the dialog (never on the dispatcher),
    /// and closing the window cancels it.
    /// </summary>
    internal void StartDownload()
    {
        if (_service is null) return;
        _error = null;
        _progress = 0;
        _downloadCancellation?.Dispose();
        _downloadCancellation = new CancellationTokenSource();
        // Progress<T> is created on the UI thread, so its callbacks are posted
        // back here and the property updates need no extra marshalling. The bar
        // is updated directly: Sync() only runs on state changes, and a percent
        // tick is not a state change.
        var progress = new Progress<double>(percent =>
        {
            _progress = percent;
            if (Progress.IsVisible && !Progress.IsIndeterminate)
            {
                Progress.Value = percent;
            }
        });
        _ = RunDownloadAsync(progress, _downloadCancellation.Token);
    }

    private async Task RunDownloadAsync(IProgress<double> progress, CancellationToken ct)
    {
        if (_service is null) return;
        try
        {
            await _service.DownloadAsync(progress, ct);
        }
        catch (OperationCanceledException)
        {
            // The user's own cancel: the service already fell back to
            // Available, and Sync re-renders the "立即更新" state.
        }
        catch (UpdateStageUnavailableException ex)
        {
            // The package is downloaded but the install root is not writable:
            // the service already revealed it in the file manager.
            _error = string.Format(Translations.Instance.UpdateStageUnavailableFormat, ex.ArchivePath);
        }
        catch (Exception ex)
        {
            _error = string.Format(Translations.Instance.UpdateDownloadFailedFormat, ex.Message);
        }
        finally
        {
            Sync();
        }
    }

    private void Restart()
    {
        if (RestartOverride is { } restart)
        {
            restart();
            return;
        }

        if (_service is null) return;
        _error = null;
        try
        {
            // A successful swap raises RestartRequested; the host then exits
            // through the normal lifetime — nothing else to do here.
            _service.RestartAndApply();
        }
        catch (Exception ex)
        {
            _error = string.Format(Translations.Instance.UpdateApplyFailedFormat, ex.Message);
            Sync();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _opened = true;
        // A dialog does not take keyboard focus by itself (same as the other
        // dialogs in this app); without this Enter/Escape would go to the main
        // window underneath.
        (PrimaryButton.IsVisible ? PrimaryButton : SecondaryButton).Focus();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _downloadCancellation?.Cancel();
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_service is not null)
        {
            _service.Changed -= OnServiceChanged;
        }

        // Closing the prompt abandons the transfer: a hidden download that
        // finishes minutes later has no UI left to ask for the restart.
        _downloadCancellation?.Cancel();
        _noticeDismissed = true;
    }
}
