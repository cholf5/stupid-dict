using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using StupidDict.App.Localization;
using StupidDict.App.Settings;
using StupidDict.App.Translation;
using StupidDict.Core.Dictionary;

namespace StupidDict.App;

/// <summary>
/// Translation session state machine (design decision C): a new query or a
/// window close cancels the in-flight request; back/forward navigation never
/// cancels it. One session at a time — the same query re-Enter reuses it
/// (single-flight), a different query replaces it. The dictionary always
/// answers first: a word miss stays manual, only a sentence miss (and only
/// with translation configured and auto enabled) sends a request.
/// </summary>
public partial class MainWindow
{
    private ITranslationService? _translation;
    private bool _ownsTranslation;
    private TranslationSession? _translationSession;
    private TranslationUiState _translationUi = new(TranslationUiKind.None, "");
    private StackPanel? _translationPanel;
    private bool _translationAutoStart;

    private sealed class TranslationSession
    {
        public required string Query;
        public required CancellationTokenSource Cancellation;
        public Task<TranslationResult>? Task;
    }

    /// <summary>
    /// Translation config straight from the shared settings — the service's
    /// GetConfig projects the same fields, so the prepare phase needs no
    /// service instance (and none is created until a translation actually
    /// starts; an unconfigured app never touches the cache file).
    /// </summary>
    private TranslationConfig TranslationConfiguration => new(
        _settings.TranslationApiUrl.Trim(),
        _settings.TranslationApiKey,
        _settings.TranslationModel.Trim(),
        _settings.TranslationAutoOnMiss);

    private ITranslationService EnsureTranslationService()
    {
        if (_translation is null)
        {
            _translation = new TranslationService(
                _settings,
                new TranslationCache(_locations.TranslationCacheDatabasePath));
            _ownsTranslation = true;
        }
        return _translation;
    }

    private void InitializeTranslation(ITranslationService? service)
    {
        if (service is not null)
            _translation = service;
        _settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.TranslationApiUrl)
                or nameof(AppSettings.TranslationApiKey)
                or nameof(AppSettings.TranslationModel)
                or nameof(AppSettings.TranslationAutoOnMiss))
                _translation?.ResetCircuit();
        };
        Closed += (_, _) =>
        {
            CancelTranslation();
            if (_ownsTranslation) _translation?.Dispose();
        };
    }

    private void BeginTranslationQuery(string query)
    {
        if (_translationSession?.Query != query)
            CancelTranslation();
        _translationUi = TranslationUiState.None(query);
        _translationAutoStart = false;
    }

    private void PrepareTranslationForResult(LookupResult result, bool allowAuto)
    {
        var query = result.Query;
        if (result.Primary is not null || (result.IsChineseQuery && result.Tier == MatchTier.Chinese))
        {
            // 词条命中（或中文精确命中）绝不发网络请求。
            _translationUi = TranslationUiState.None(query);
            return;
        }

        var sentence = TranslationText.LooksLikeSentence(query);
        var config = TranslationConfiguration;
        if (!sentence)
        {
            _translationUi = new TranslationUiState(
                config.IsConfigured ? TranslationUiKind.Manual : TranslationUiKind.NotConfigured, query);
            return;
        }

        if (_translationSession?.Query == query)
        {
            _translationUi = new TranslationUiState(TranslationUiKind.Pending, query);
            return;
        }

        if (!config.IsConfigured)
        {
            _translationUi = new TranslationUiState(TranslationUiKind.NotConfigured, query);
            return;
        }

        if (!allowAuto || !config.AutoOnMiss)
        {
            _translationUi = new TranslationUiState(TranslationUiKind.Manual, query);
            return;
        }

        var cached = EnsureTranslationService().TryGetCached(query);
        if (cached is { IsSuccess: true })
        {
            _translationUi = new TranslationUiState(
                TranslationUiKind.Success, query, cached.Text, cached.Model,
                FromCache: true, IsLocal: cached.IsLocal);
            return;
        }

        _translationUi = new TranslationUiState(TranslationUiKind.Pending, query);
        _translationAutoStart = true;
    }

    private void StartPreparedTranslation()
    {
        if (!_translationAutoStart || _translationUi.Kind != TranslationUiKind.Pending) return;
        _translationAutoStart = false;
        if (_translation is { AutoPaused: true })
        {
            // 熔断中（首次 401/403/404 后，设计「错误与熔断」）：自动翻译暂停，
            // 回到手动卡——「翻译这句话」= 用户显式重试，StartTranslation 里
            // ResetCircuit 恢复。避免坏 key 时每句都白发一次注定失败的请求。
            _translationUi = new TranslationUiState(TranslationUiKind.Manual, _translationUi.Query);
            UpdateTranslationPanel();
            return;
        }
        StartTranslation(_translationUi.Query, bypassCache: false);
    }

    private void PrepareTranslationForNavigation(LookupResult result)
    {
        var query = result.Query;
        if (result.Primary is not null || (result.IsChineseQuery && result.Tier == MatchTier.Chinese))
        {
            _translationUi = TranslationUiState.None(query);
            return;
        }

        if (_translationSession?.Query == query)
        {
            // 后退回到还在翻译的页面：会话继续跑（导航不取消），显示进行中。
            _translationUi = new TranslationUiState(TranslationUiKind.Pending, query);
            return;
        }

        var sentence = TranslationText.LooksLikeSentence(query);
        var config = TranslationConfiguration;
        if (!sentence)
        {
            _translationUi = new TranslationUiState(
                config.IsConfigured ? TranslationUiKind.Manual : TranslationUiKind.NotConfigured, query);
            return;
        }

        // 导航从不发请求；缓存恢复只经已存在的服务实例（没有就当未缓存）。
        var cached = _translation?.TryGetCached(query);
        _translationUi = cached is { IsSuccess: true }
            ? new TranslationUiState(TranslationUiKind.Success, query, cached.Text, cached.Model,
                FromCache: true, IsLocal: cached.IsLocal)
            : new TranslationUiState(
                config.IsConfigured ? TranslationUiKind.Manual : TranslationUiKind.NotConfigured, query);
    }

    private void StartTranslation(string query, bool bypassCache)
    {
        if (!TranslationConfiguration.IsConfigured) return;
        var service = EnsureTranslationService();
        CancelTranslation();

        service.ResetCircuit();
        var cts = new CancellationTokenSource();
        var session = new TranslationSession { Query = query, Cancellation = cts };
        _translationSession = session;
        _translationUi = new TranslationUiState(TranslationUiKind.Pending, query);
        UpdateTranslationPanel();

        session.Task = TranslateAsync(session, bypassCache);
    }

    private async Task<TranslationResult> TranslateAsync(TranslationSession session, bool bypassCache)
    {
        try
        {
            var result = await _translation!.TranslateAsync(
                session.Query, bypassCache, session.Cancellation.Token);
            // 只有当前页仍是该 query、且没有更新的翻译请求时才渲染；否则
            // 结果已写进缓存（服务内部），页面保持导航目标的状态。
            if (_translationSession == session && IsCurrentQuery(session.Query))
                ApplyTranslationResult(session.Query, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            // 取消不写缓存（服务内部已保证）；被取消的自动翻译回到手动卡。
            if (_translationSession == session && IsCurrentQuery(session.Query))
                _translationUi = new TranslationUiState(TranslationUiKind.Manual, session.Query);
            return TranslationResult.Fail(TranslationFailureKind.InvalidResponse, canRetry: false);
        }
        catch (Exception ex)
        {
            if (_translationSession == session && IsCurrentQuery(session.Query))
                _translationUi = new TranslationUiState(
                    TranslationUiKind.Failure, session.Query,
                    Failure: TranslationFailureKind.InvalidResponse, CanRetry: true);
            System.Diagnostics.Debug.WriteLine(ex);
            return TranslationResult.Fail(TranslationFailureKind.InvalidResponse);
        }
        finally
        {
            if (_translationSession == session)
                _translationSession = null;
            UpdateTranslationPanel();
        }
    }

    private void ApplyTranslationResult(string query, TranslationResult result)
    {
        _translationUi = result.IsSuccess
            ? new TranslationUiState(TranslationUiKind.Success, query, result.Text, result.Model,
                result.FromCache, result.IsLocal)
            : new TranslationUiState(TranslationUiKind.Failure, query,
                Failure: result.Failure, CanRetry: result.CanRetry);
    }

    private bool IsCurrentQuery(string query) =>
        string.Equals(SearchBox.Text?.Trim(), query, StringComparison.Ordinal);

    private void CancelTranslation()
    {
        var session = _translationSession;
        _translationSession = null;
        if (session is not null)
        {
            session.Cancellation.Cancel();
            session.Cancellation.Dispose();
        }
        _translationAutoStart = false;
    }

    private void OnTranslateClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_translationUi.Query.Length == 0) return;
        // 失败重试跳过缓存读（重新翻译）；手动翻译正常走缓存。
        StartTranslation(_translationUi.Query, bypassCache: _translationUi.Kind == TranslationUiKind.Failure);
    }

    private void OnCancelTranslationClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        CancelTranslation();
        _translationUi = new TranslationUiState(TranslationUiKind.Manual, _translationUi.Query);
        UpdateTranslationPanel();
    }

    private void OnTranslationSettingsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        OpenSettings(SettingsWindow.TranslationTabIndex);

    private void RenderTranslationSection(LookupResult result)
    {
        if (_translationUi.Kind == TranslationUiKind.None) return;
        _translationPanel = new StackPanel { Spacing = 6, Margin = new Thickness(2, 8, 0, 8) };
        ResultsPanel.Children.Add(_translationPanel);
        UpdateTranslationPanel();
    }

    private void UpdateTranslationPanel()
    {
        if (_translationPanel is null) return;
        _translationPanel.Children.Clear();
        var t = Translations.Instance;
        var ui = _translationUi;

        switch (ui.Kind)
        {
            case TranslationUiKind.Pending:
                _translationPanel.Children.Add(Text(t.TranslationInProgress,
                    fontSize: 13, brushKey: Palette.TextMuted));
                _translationPanel.Children.Add(BuildChipButton(t.Cancel, OnCancelTranslationClick));
                break;
            case TranslationUiKind.Success:
                var label = string.Format(
                    ui.IsLocal ? t.TranslationLocalFormat : t.TranslationCloudFormat, ui.Model ?? "");
                _translationPanel.Children.Add(Text(label, fontSize: 12, brushKey: Palette.TextMuted));
                _translationPanel.Children.Add(Text(ui.Text ?? "", fontSize: 16,
                    brushKey: Palette.TextBody, lineHeight: 24));
                break;
            case TranslationUiKind.Failure:
                _translationPanel.Children.Add(Text(string.Format(
                    t.TranslationFailedFormat, FailureText(ui.Failure)), fontSize: 13,
                    brushKey: Palette.TextMuted));
                _translationPanel.Children.Add(BuildChipButton(t.Retry, OnTranslateClick));
                _translationPanel.Children.Add(BuildChipButton(t.TranslationOpenSettings, OnTranslationSettingsClick));
                break;
            case TranslationUiKind.Manual:
                _translationPanel.Children.Add(BuildChipButton(
                    TranslationText.LooksLikeSentence(ui.Query) ? t.TranslateSentence : t.TranslateWord,
                    OnTranslateClick));
                break;
            case TranslationUiKind.NotConfigured:
                _translationPanel.Children.Add(Text(t.TranslationNotConfigured, fontSize: 13,
                    brushKey: Palette.TextMuted));
                _translationPanel.Children.Add(BuildChipButton(t.TranslationOpenSettings, OnTranslationSettingsClick));
                break;
        }
    }

    /// <summary>失败类别 → 短文案；绝不把 key、原始响应体带进界面。</summary>
    private static string FailureText(TranslationFailureKind? kind) => kind switch
    {
        TranslationFailureKind.Authentication => Translations.Instance.TranslationAuthFailed,
        TranslationFailureKind.EndpointOrModel or TranslationFailureKind.InvalidConfig =>
            Translations.Instance.TranslationEndpointFailed,
        TranslationFailureKind.RateLimited => Translations.Instance.TranslationRateLimited,
        TranslationFailureKind.Server => Translations.Instance.TranslationServerFailed,
        TranslationFailureKind.Timeout => Translations.Instance.TranslationTimeout,
        TranslationFailureKind.Network => Translations.Instance.TranslationNetworkFailed,
        TranslationFailureKind.Refused => Translations.Instance.TranslationRefused,
        TranslationFailureKind.Incomplete => Translations.Instance.TranslationIncomplete,
        _ => Translations.Instance.TranslationInvalidResponse,
    };

    private Button BuildChipButton(string label, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var button = new Button { Content = label, Classes = { "chip" } };
        button.Click += click;
        return button;
    }
}
