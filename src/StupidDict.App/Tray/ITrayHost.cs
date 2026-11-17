using StupidDict.App.Localization;

namespace StupidDict.App.Tray;

/// <summary>
/// Everything the tray icon can trigger. The host stays free of UI logic: it
/// only knows "left click" and the four menu items, and calls back into the
/// window that owns the tray.
/// </summary>
internal sealed record TrayActions(
    Action QuickLookup,
    Action ShowWindow,
    Action OpenSettings,
    Action CheckUpdate,
    Action Exit);

/// <summary>
/// The notification-area icon. An interface so headless tests can record the
/// wiring without a real platform tray; the production implementation is
/// <see cref="TrayIconHost"/>.
/// </summary>
internal interface ITrayHost : IDisposable
{
    /// <summary>
    /// Creates the icon and wires its actions. Idempotent. Returns false when the
    /// platform has no tray — the caller must then treat a close as a real exit
    /// instead of hiding, otherwise the app would become unreachable.
    /// </summary>
    bool Show(TrayActions actions);
}
