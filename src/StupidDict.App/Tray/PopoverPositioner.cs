using Avalonia;

namespace StupidDict.App.Tray;

/// <summary>
/// Where the quick-lookup card lands, in physical pixels (the unit of
/// <see cref="Avalonia.Platform.Screen.Bounds"/>, <see cref="Screen.WorkingArea"/>
/// and <see cref="Window.Position"/>). Pure functions: the platform-specific
/// probing lives in <see cref="TrayAnchor"/>, so headless tests pin the maths
/// without a tray, a menu bar or a taskbar.
/// </summary>
internal static class PopoverPositioner
{
    /// <summary>Windows/Linux flyout: the cursor sits on the tray icon when the
    /// click lands, so the card's bottom edge goes <paramref name="gap"/> above
    /// it. A taskbar on the right edge leaves no horizontal room, hence the
    /// caller passes <paramref name="rightOverlap"/> to tuck the card inside.</summary>
    public static PixelPoint AboveCursor(PixelRect workingArea, PixelSize card, PixelPoint cursor, int gap, int rightOverlap)
    {
        var x = cursor.X + rightOverlap - card.Width;
        var y = cursor.Y - card.Height - gap;
        return Clamp(new PixelPoint(x, y), workingArea, card);
    }

    /// <summary>macOS: the card hangs below the menu-bar status item, left edges
    /// aligned (the system popover convention), clamped back into the work area.</summary>
    public static PixelPoint BelowAnchor(PixelRect workingArea, PixelSize card, PixelRect anchor, int gap)
    {
        var y = anchor.Y + anchor.Height + gap;
        return Clamp(new PixelPoint(anchor.X, y), workingArea, card);
    }

    /// <summary>
    /// No cursor or icon geometry (Linux panels, probe failure): park the card
    /// in the working-area corner that faces the panel — the gap between
    /// <paramref name="screenBounds"/> and <paramref name="workingArea"/> tells
    /// which edge the panel occupies, so a bottom panel yields a bottom corner.
    /// </summary>
    public static PixelPoint InCorner(PixelRect screenBounds, PixelRect workingArea, PixelSize card, int gap)
    {
        var panelAtBottom = workingArea.Y == screenBounds.Y;
        var x = workingArea.X + workingArea.Width - card.Width - gap;
        var y = panelAtBottom
            ? workingArea.Y + workingArea.Height - card.Height - gap
            : workingArea.Y + gap;
        return Clamp(new PixelPoint(x, y), workingArea, card);
    }

    private static PixelPoint Clamp(PixelPoint point, PixelRect area, PixelSize card)
    {
        var maxX = Math.Max(area.X, area.X + area.Width - card.Width);
        var maxY = Math.Max(area.Y, area.Y + area.Height - card.Height);
        return new PixelPoint(
            Math.Clamp(point.X, area.X, maxX),
            Math.Clamp(point.Y, area.Y, maxY));
    }
}
