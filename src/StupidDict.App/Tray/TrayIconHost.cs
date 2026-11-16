using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using StupidDict.App.Localization;

namespace StupidDict.App.Tray;

/// <summary>
/// The notification-area icon used while the window is hidden. An interface so
/// headless tests can record show/hide/exit without a real platform tray; the
/// production implementation is <see cref="TrayIconHost"/>.
/// </summary>
internal interface ITrayHost : IDisposable
{
    /// <summary>
    /// Shows the tray icon and wires its "show window" / "quit" actions.
    /// Returns false when no tray can be shown — the caller must then quit
    /// instead of hiding, otherwise the app would become unreachable.
    /// </summary>
    bool Show(Action restoreWindow, Action exitApplication);

    /// <summary>Removes the icon (the window is visible again, or the app is exiting).</summary>
    void Hide();
}

/// <summary>
/// A real Avalonia <see cref="TrayIcon"/> with a two-item menu. The icon is
/// created on demand and disposed on hide, so the app only occupies the
/// notification area while its window is hidden.
/// </summary>
internal sealed class TrayIconHost : ITrayHost
{
    // Tray images are tiny; the ico is Windows-only artwork, the png is the
    // 1024px master every platform scales down. Use the png everywhere.
    private const string IconUri = "avares://StupidDict/Assets/app-icon.png";

    private TrayIcon? _icon;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _exitItem;

    public bool Show(Action restoreWindow, Action exitApplication)
    {
        if (_icon is not null) return true;
        var application = Application.Current;
        if (application is null) return false;

        try
        {
            var t = Translations.Instance;
            var show = new NativeMenuItem { Header = t.TrayShowWindow };
            show.Click += (_, _) => restoreWindow();
            var exit = new NativeMenuItem { Header = t.TrayExit };
            exit.Click += (_, _) => exitApplication();
            var menu = new NativeMenu();
            menu.Items.Add(show);
            menu.Items.Add(exit);

            var icon = new TrayIcon
            {
                ToolTipText = t.AppName,
                Menu = menu,
                Icon = LoadIcon(),
                IsVisible = true,
            };
            // Win32 and some Linux DEs raise Clicked; macOS never does, which is
            // why the menu above carries the same "show window" action.
            icon.Clicked += (_, _) => restoreWindow();

            // The attached property owns attach/detach; Hide detaches it again.
            TrayIcon.SetIcons(application, new TrayIcons { icon });

            _icon = icon;
            _showItem = show;
            _exitItem = exit;
            Translations.Instance.PropertyChanged += OnTranslationsChanged;
            return true;
        }
        catch (Exception)
        {
            // No tray backend (headless / exotic desktop): report failure so
            // the close path falls back to a real exit instead of hiding.
            Hide();
            return false;
        }
    }

    public void Hide()
    {
        if (_icon is null) return;
        Translations.Instance.PropertyChanged -= OnTranslationsChanged;
        _icon.IsVisible = false;
        if (Application.Current is { } application)
            TrayIcon.SetIcons(application, null);
        _icon.Dispose();
        _icon = null;
        _showItem = null;
        _exitItem = null;
    }

    /// <summary>A language switch while hidden must not leave stale menu text behind.</summary>
    private void OnTranslationsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(Translations.CurrentLanguage) || _icon is null)
            return;
        var t = Translations.Instance;
        _icon.ToolTipText = t.AppName;
        if (_showItem is not null) _showItem.Header = t.TrayShowWindow;
        if (_exitItem is not null) _exitItem.Header = t.TrayExit;
    }

    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(IconUri));
            return new WindowIcon(stream);
        }
        catch (Exception)
        {
            // Icon is cosmetic; a tray without artwork still works.
            return null;
        }
    }

    public void Dispose() => Hide();
}
