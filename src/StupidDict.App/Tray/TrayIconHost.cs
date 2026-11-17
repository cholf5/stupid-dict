using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using StupidDict.App.Localization;

namespace StupidDict.App.Tray;

/// <summary>
/// The real notification-area icon, present for the whole run (not only while
/// the window is hidden): left click toggles the quick-lookup card, right click
/// opens the menu "show window / settings / check for updates / quit".
/// <para>
/// Two backends. On macOS the icon is an <see cref="MacStatusItem"/> we own,
/// because Avalonia's <c>TrayIcon.Clicked</c> never fires there and the menu
/// would be the only reachable action. Everywhere else — and on macOS when the
/// interop cannot install — a plain Avalonia <see cref="TrayIcon"/> provides the
/// icon, the same menu, and (Windows/Linux) the left-click callback.
/// </para>
/// <para>
/// Menu instances are never replaced: AppKit's menu exporter caches the first
/// instance it mounted and replacing it at runtime throws. A language switch
/// rewrites the existing headers instead.
/// </para>
/// </summary>
internal sealed class TrayIconHost : ITrayHost
{
    // The ico is Windows-only artwork; the png is the 1024px master. The
    // template png is the flat menu-bar glyph AppKit recolours on its own.
    private const string AppIconUri = "avares://StupidDict/Assets/app-icon.png";
    private const string TemplateIconUri = "avares://StupidDict/Assets/tray-icon.png";

    private TrayActions? _actions;
    private TrayIcon? _icon;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _settingsItem;
    private NativeMenuItem? _updateItem;
    private NativeMenuItem? _exitItem;
    private MacStatusItem.Item[]? _macItems;

    /// <summary>
    /// Headless UI tests run on macOS too; creating a real status item there
    /// would put a genuine icon in the developer's menu bar for the duration of
    /// the test run. The tests never assert on the real backend's result (the
    /// return value is platform-dependent), so skipping the native path is safe.
    /// </summary>
    private static bool IsHeadlessHost =>
        AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Avalonia.Headless");

    public bool Show(TrayActions actions)
    {
        if (_actions is not null) return true;
        var installed = !IsHeadlessHost && OperatingSystem.IsMacOS() && TryInstallMacTray(actions)
            || TryInstallAvaloniaTray(actions);
        if (!installed) return false;
        SubscribeLanguage();
        _actions = actions;
        return true;
    }

    private bool TryInstallMacTray(TrayActions actions)
    {
        byte[] icon;
        try
        {
            using var stream = AssetLoader.Open(new Uri(TemplateIconUri));
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            icon = buffer.ToArray();
        }
        catch (Exception)
        {
            return false; // asset missing: fall back to the Avalonia tray
        }

        var items = BuildMacItems(actions);
        if (!MacStatusItem.TryInstall(icon, Translations.Instance.AppName, items, actions.QuickLookup))
            return false;
        _macItems = items;
        return true;
    }

    private static MacStatusItem.Item[] BuildMacItems(TrayActions actions) =>
    [
        new(() => Translations.Instance.TrayShowWindow, actions.ShowWindow),
        new(() => Translations.Instance.TraySettings, actions.OpenSettings),
        new(() => Translations.Instance.TrayCheckUpdate, actions.CheckUpdate),
        new(() => Translations.Instance.TrayExit, actions.Exit),
    ];

    private bool TryInstallAvaloniaTray(TrayActions actions)
    {
        var application = Application.Current;
        if (application is null) return false;

        try
        {
            var t = Translations.Instance;
            _showItem = new NativeMenuItem { Header = t.TrayShowWindow };
            _showItem.Click += (_, _) => actions.ShowWindow();
            _settingsItem = new NativeMenuItem { Header = t.TraySettings };
            _settingsItem.Click += (_, _) => actions.OpenSettings();
            _updateItem = new NativeMenuItem { Header = t.TrayCheckUpdate };
            _updateItem.Click += (_, _) => actions.CheckUpdate();
            _exitItem = new NativeMenuItem { Header = t.TrayExit };
            _exitItem.Click += (_, _) => actions.Exit();

            var menu = new NativeMenu();
            menu.Items.Add(_showItem);
            menu.Items.Add(_settingsItem);
            menu.Items.Add(_updateItem);
            menu.Items.Add(_exitItem);

            var icon = new TrayIcon
            {
                ToolTipText = t.AppName,
                Menu = menu,
                Icon = LoadIcon(AppIconUri),
                IsVisible = true,
            };
            // Win32 and some Linux DEs raise Clicked; macOS never does, which is
            // why the macOS path above installs its own status item instead.
            icon.Clicked += (_, _) => actions.QuickLookup();

            // The attached property owns attach/detach; Dispose detaches it again.
            TrayIcon.SetIcons(application, new TrayIcons { icon });
            _icon = icon;
            return true;
        }
        catch (Exception)
        {
            // No tray backend (headless / exotic desktop): report failure so the
            // close path falls back to a real exit instead of hiding.
            DisposePlatformBits();
            return false;
        }
    }

    private static WindowIcon? LoadIcon(string uri)
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(uri));
            return new WindowIcon(stream);
        }
        catch (Exception)
        {
            // Icon is cosmetic; a tray without artwork still works.
            return null;
        }
    }

    private void SubscribeLanguage() =>
        Translations.Instance.PropertyChanged += OnTranslationsChanged;

    /// <summary>A language switch must not leave stale menu text behind.</summary>
    private void OnTranslationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage)) return;
        if (_actions is not { } actions) return;
        var t = Translations.Instance;
        if (_macItems is not null)
        {
            MacStatusItem.SetTooltip(t.AppName);
            _macItems = BuildMacItems(actions);
            MacStatusItem.RefreshTitles(_macItems);
            return;
        }
        if (_icon is null) return;
        _icon.ToolTipText = t.AppName;
        if (_showItem is not null) _showItem.Header = t.TrayShowWindow;
        if (_settingsItem is not null) _settingsItem.Header = t.TraySettings;
        if (_updateItem is not null) _updateItem.Header = t.TrayCheckUpdate;
        if (_exitItem is not null) _exitItem.Header = t.TrayExit;
    }

    private void DisposePlatformBits()
    {
        if (_icon is { } icon)
        {
            icon.IsVisible = false;
            if (Application.Current is { } application)
                TrayIcon.SetIcons(application, null);
            icon.Dispose();
            _icon = null;
        }
        if (_macItems is not null)
        {
            MacStatusItem.Remove();
            _macItems = null;
        }
        _showItem = null;
        _settingsItem = null;
        _updateItem = null;
        _exitItem = null;
    }

    public void Dispose()
    {
        Translations.Instance.PropertyChanged -= OnTranslationsChanged;
        DisposePlatformBits();
        _actions = null;
    }
}
