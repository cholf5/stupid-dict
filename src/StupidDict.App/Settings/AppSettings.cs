using System.ComponentModel;

namespace StupidDict.App.Settings;

/// <summary>The appearance theme: follow the OS, or force one mode.</summary>
public enum AppTheme
{
    System = 0,
    Light = 1,
    Dark = 2,
}

/// <summary>The UI language: follow the OS, or force one. (0/1/2 mirror the settings combo order.)</summary>
public enum AppLanguage
{
    System = 0,
    SimplifiedChinese = 1,
    English = 2,
}

/// <summary>
/// Everything persisted across runs: the theme and the UI language are user
/// choices surfaced in the settings window; the main window's size and
/// maximized state are recorded automatically at close with no settings UI.
/// Lives as one shared instance: App wires it at startup, the settings window
/// mutates it, MainWindow records bounds into it, and every change applies
/// live and is persisted by the wiring.
/// </summary>
public sealed class AppSettings : INotifyPropertyChanged
{
    private AppTheme _theme = AppTheme.System;
    private AppLanguage _language = AppLanguage.System;
    private double? _windowWidth;
    private double? _windowHeight;
    private bool _windowMaximized;
    private string _translationApiUrl = "";
    private string _translationApiKey = "";
    private string _translationModel = "";
    private bool _translationAutoOnMiss = true;
    private bool _launchAtLogin;

    public AppTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Theme)));
        }
    }

    public AppLanguage Language
    {
        get => _language;
        set
        {
            if (_language == value) return;
            _language = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
        }
    }

    /// <summary>Main window width in DIPs as of the last close; null until the first close.</summary>
    public double? WindowWidth
    {
        get => _windowWidth;
        set
        {
            if (_windowWidth == value) return;
            _windowWidth = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowWidth)));
        }
    }

    /// <summary>Main window height in DIPs as of the last close; null until the first close.</summary>
    public double? WindowHeight
    {
        get => _windowHeight;
        set
        {
            if (_windowHeight == value) return;
            _windowHeight = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowHeight)));
        }
    }

    /// <summary>Whether the main window was maximized at last close.</summary>
    public bool WindowMaximized
    {
        get => _windowMaximized;
        set
        {
            if (_windowMaximized == value) return;
            _windowMaximized = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowMaximized)));
        }
    }

    /// <summary>OpenAI-compatible base URL for optional sentence translation; empty = disabled.</summary>
    public string TranslationApiUrl
    {
        get => _translationApiUrl;
        set
        {
            if (_translationApiUrl == value) return;
            _translationApiUrl = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationApiUrl)));
        }
    }

    /// <summary>Bearer key for the translation API; may be empty for local endpoints. Stored in plain text.</summary>
    public string TranslationApiKey
    {
        get => _translationApiKey;
        set
        {
            if (_translationApiKey == value) return;
            _translationApiKey = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationApiKey)));
        }
    }

    /// <summary>Model name sent with each translation request; required for translation to be active.</summary>
    public string TranslationModel
    {
        get => _translationModel;
        set
        {
            if (_translationModel == value) return;
            _translationModel = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationModel)));
        }
    }

    /// <summary>Whether a sentence that misses the dictionary is translated automatically.</summary>
    public bool TranslationAutoOnMiss
    {
        get => _translationAutoOnMiss;
        set
        {
            if (_translationAutoOnMiss == value) return;
            _translationAutoOnMiss = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranslationAutoOnMiss)));
        }
    }

    /// <summary>Whether the app registers a per-user OS entry to launch at login.</summary>
    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set
        {
            if (_launchAtLogin == value) return;
            _launchAtLogin = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(LaunchAtLogin)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
