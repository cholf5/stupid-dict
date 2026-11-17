using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;

namespace StupidDict.App.Tray;

/// <summary>
/// macOS notification-area icon owned by us instead of by Avalonia (ported from
/// tokenmetry's MacTrayInterop, trimmed to this app's needs).
/// <para>
/// <b>Why</b>: <c>TrayIcon.Clicked</c> never fires on macOS (trayicon.mm wires no
/// click callback), so an Avalonia tray can only ever show its menu — left-click
/// quick lookup would have no entry point. We therefore build an NSStatusItem
/// ourselves: derive a subclass of the button's own class, install it with
/// <c>object_setClass</c>, override <c>rightMouseDown:</c> to pop our own NSMenu,
/// and route the left click through target/action (an NSButton consumes mouse-up
/// inside its own tracking loop, so overriding <c>mouseUp:</c> would never run).
/// </para>
/// <para>
/// <b>Constraints</b>: main thread only (all callers are on the Avalonia UI
/// thread). ABI verified on Apple Silicon only — the Intel <c>objc_msgSend_stret</c>
/// path for struct returns is not implemented, so <see cref="TryInstall"/> failing
/// there is expected and the caller falls back to the Avalonia tray. Objective-C
/// exceptions and managed exceptions must never cross the IMP boundary (that
/// crashes the process), so every IMP swallows its own faults.
/// </para>
/// </summary>
internal static unsafe class MacStatusItem
{
    private const string LibObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>One menu entry: title is a live getter so a language switch can
    /// rewrite the native item in place (replacing the whole menu at runtime is
    /// not something AppKit tolerates).</summary>
    internal sealed record Item(Func<string> Title, Action Action);

    [DllImport(LibObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(LibObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);
    [DllImport(LibObjC)] private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);
    [DllImport(LibObjC)] private static extern void objc_registerClassPair(IntPtr cls);
    [DllImport(LibObjC)] private static extern IntPtr object_setClass(IntPtr obj, IntPtr cls);
    [DllImport(LibObjC)] private static extern IntPtr object_getClass(IntPtr obj);
    [DllImport(LibObjC)] private static extern IntPtr sel_registerName(string name);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2, IntPtr arg3, IntPtr arg4);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, double arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, NSSize arg1);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, NSPoint arg2, IntPtr arg3);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector, IntPtr arg1, nuint arg2);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern double MsgSendDouble(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern long MsgSendLong(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern NSPoint MsgSendPoint(IntPtr receiver, IntPtr selector);
    [DllImport(LibObjC, EntryPoint = "objc_msgSend")] private static extern NSRect MsgSendRect(IntPtr receiver, IntPtr selector);

    [StructLayout(LayoutKind.Sequential)]
    private struct NSPoint { public double X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSSize { public double Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NSRect { public double X, Y, Width, Height; }

    // Cached selectors. They must be resolved before the first msgSend: a null
    // selector hits the empty method cache bucket (sel address 0 == the zeroed
    // bucket) and jumps to imp 0 — a segfault, not an unrecognized-selector
    // exception (tokenmetry's instrumentation, same trap).
    private static IntPtr SelButton, SelStatusItemWithLength, SelStatusBar, SelRemoveStatusItem,
        SelFrame, SelWindow, SelScreens, SelObjectAtIndex, SelSetImage, SelSetTemplate, SelSetToolTip,
        SelSetTarget, SelSetAction, SelSetTag, SelSetEnabled, SelSetTitle, SelAddItem, SelSetAutoenables,
        SelAlloc, SelInit, SelInitWithUtf8, SelRelease, SelRightMouseDown, SelMenuAction, SelButtonAction,
        SelTag, SelMouseLocation, SelDataWithBytesLength, SelInitWithData, SelMenuFontOfSize, SelPointSize,
        SelSetSize, SelInitWithTitle, SelInitWith3, SelPopUpMenu;

    private static IntPtr _button, _menu, _target, _statusItem;
    private static readonly List<IntPtr> NativeItems = new();
    private static IReadOnlyList<Item>? _spec;
    private static Action? _leftClick;

    public static bool IsInstalled { get; private set; }

    /// <summary>Installs the status item. Any failure (unsupported ABI, AppKit
    /// change) returns false so the caller can fall back to the Avalonia tray —
    /// the icon and its menu still exist there, only left-click is lost.</summary>
    public static bool TryInstall(byte[] templatePng, string tooltip, IReadOnlyList<Item> items, Action leftClick)
    {
        if (IsInstalled) return true;
        try
        {
            Install(templatePng, tooltip, items, leftClick);
            IsInstalled = true;
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StupidDict] macOS tray interop failed, falling back to the Avalonia tray: {ex.Message}");
            return false;
        }
    }

    private static void Install(byte[] templatePng, string tooltip, IReadOnlyList<Item> items, Action leftClick)
    {
        _leftClick = leftClick;
        _spec = items;
        CacheSelectors();

        // Square cell, same choice as Avalonia's trayicon.mm (NSSquareStatusItemLength).
        var statusBar = MsgSend(objc_getClass("NSStatusBar"), SelStatusBar);
        var statusItem = MsgSend(statusBar, SelStatusItemWithLength, -2.0);
        _statusItem = statusItem;
        _button = MsgSend(statusItem, SelButton);

        // Derive from the button's actual class instead of assuming NSStatusBarButton.
        var buttonClass = objc_allocateClassPair(object_getClass(_button), "StupidDictTrayStatusButton", 0);
        if (buttonClass == IntPtr.Zero)
            throw new InvalidOperationException("objc_allocateClassPair failed");

        var impRightDown = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&ButtonRightMouseDownImp;
        var impMenuAction = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&MenuItemActionImp;
        var impButtonAction = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, IntPtr, IntPtr, void>)&TrayButtonActionImp;
        if (!class_addMethod(buttonClass, SelRightMouseDown, impRightDown, "v@:@")
            || !class_addMethod(buttonClass, SelMenuAction, impMenuAction, "v@:@")
            || !class_addMethod(buttonClass, SelButtonAction, impButtonAction, "v@:@"))
        {
            throw new InvalidOperationException("class_addMethod failed");
        }

        objc_registerClassPair(buttonClass);

        // Template image: AppKit renders the alpha channel in the menu-bar
        // foreground colour, so the flat glyph follows light/dark automatically.
        var image = MakeImage(templatePng);
        var menuFont = MsgSend(objc_getClass("NSFont"), SelMenuFontOfSize, 0.0);
        var side = Math.Floor(MsgSendDouble(menuFont, SelPointSize) * 1.333333);
        MsgSend(image, SelSetSize, new NSSize { Width = side, Height = side });
        MsgSend(image, SelSetTemplate, (IntPtr)1);
        MsgSend(_button, SelSetImage, image);
        MsgSend(image, SelRelease);
        SetTooltip(tooltip);

        // Left click: target/action (see the class summary for why not mouseUp:).
        _target = MsgSend(MsgSend(buttonClass, SelAlloc), SelInit);
        _ = object_setClass(_button, buttonClass);
        MsgSend(_button, SelSetTarget, _target);
        MsgSend(_button, SelSetAction, SelButtonAction);

        // Right click menu: autoenables off — the target is not on the responder
        // chain, so automatic enabling would grey everything out.
        _menu = MsgSend(MsgSend(objc_getClass("NSMenu"), SelAlloc), SelInitWithTitle, CreateNSString("StupidDict"));
        MsgSend(_menu, SelSetAutoenables, (IntPtr)0);
        for (var index = 0; index < items.Count; index++)
        {
            var item = MsgSend(MsgSend(objc_getClass("NSMenuItem"), SelAlloc), SelInitWith3,
                CreateNSString(items[index].Title()), SelMenuAction, CreateNSString(string.Empty));
            MsgSend(item, SelSetTag, (IntPtr)index);
            MsgSend(item, SelSetEnabled, (IntPtr)1);
            MsgSend(item, SelSetTarget, _target);
            NativeItems.Add(item);
            MsgSend(_menu, SelAddItem, item);
            MsgSend(item, SelRelease); // the menu retains it
        }
    }

    private static void CacheSelectors()
    {
        if (SelButton != IntPtr.Zero) return;

        SelButton = Sel("button");
        SelStatusBar = Sel("systemStatusBar");
        SelStatusItemWithLength = Sel("statusItemWithLength:");
        SelRemoveStatusItem = Sel("removeStatusItem:");
        SelFrame = Sel("frame");
        SelWindow = Sel("window");
        SelScreens = Sel("screens");
        SelObjectAtIndex = Sel("objectAtIndex:");
        SelSetImage = Sel("setImage:");
        SelSetTemplate = Sel("setTemplate:");
        SelSetToolTip = Sel("setToolTip:");
        SelSetTarget = Sel("setTarget:");
        SelSetAction = Sel("setAction:");
        SelSetTag = Sel("setTag:");
        SelSetEnabled = Sel("setEnabled:");
        SelSetTitle = Sel("setTitle:");
        SelAddItem = Sel("addItem:");
        SelSetAutoenables = Sel("setAutoenablesItems:");
        SelAlloc = Sel("alloc");
        SelInit = Sel("init");
        SelInitWithUtf8 = Sel("initWithUTF8String:");
        SelRelease = Sel("release");
        SelRightMouseDown = Sel("rightMouseDown:");
        SelMenuAction = Sel("stupidDictMenuItemAction:");
        SelButtonAction = Sel("stupidDictTrayButtonAction:");
        SelTag = Sel("tag");
        SelMouseLocation = Sel("mouseLocation");
        SelDataWithBytesLength = Sel("dataWithBytes:length:");
        SelInitWithData = Sel("initWithData:");
        SelMenuFontOfSize = Sel("menuFontOfSize:");
        SelPointSize = Sel("pointSize");
        SelSetSize = Sel("setSize:");
        SelInitWithTitle = Sel("initWithTitle:");
        SelInitWith3 = Sel("initWithTitle:action:keyEquivalent:");
        SelPopUpMenu = Sel("popUpMenuPositioningItem:atLocation:inView:");
    }

    private static IntPtr Sel(string name) => sel_registerName(name);

    // ── IMPs: main thread only, every fault swallowed (crossing the ObjC
    //    boundary with an exception kills the process) ──

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void TrayButtonActionImp(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            _leftClick?.Invoke();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StupidDict] tray left click failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ButtonRightMouseDownImp(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            PopUpMenu();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StupidDict] tray right click failed: {ex.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void MenuItemActionImp(IntPtr self, IntPtr sel, IntPtr sender)
    {
        try
        {
            var tag = MsgSendLong(sender, SelTag);
            if (_spec is { } spec && tag >= 0 && tag < spec.Count)
                spec[(int)tag].Action();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[StupidDict] tray menu item failed: {ex.Message}");
        }
    }

    // ── Runtime operations (main thread) ──

    /// <summary>Pops the right-click menu at the cursor (blocks until dismissed).</summary>
    public static void PopUpMenu()
    {
        if (_menu == IntPtr.Zero) return;
        var location = MsgSendPoint(objc_getClass("NSEvent"), SelMouseLocation);
        MsgSend(_menu, SelPopUpMenu, IntPtr.Zero, location, IntPtr.Zero);
    }

    public static void SetTooltip(string tooltip)
    {
        if (_button != IntPtr.Zero)
            SetNSString(_button, SelSetToolTip, tooltip);
    }

    /// <summary>Rewrites the menu titles from <see cref="Item.Title"/> after a
    /// language switch. Same item count as at install time — AppKit menus are
    /// not rebuilt at runtime.</summary>
    public static void RefreshTitles(IReadOnlyList<Item> items)
    {
        _spec = items;
        for (var index = 0; index < NativeItems.Count && index < items.Count; index++)
            SetNSString(NativeItems[index], SelSetTitle, items[index].Title());
    }

    /// <summary>Removes the status item (app exit / fallback). Safe to call twice.</summary>
    public static void Remove()
    {
        if (_statusItem != IntPtr.Zero)
        {
            var statusBar = MsgSend(objc_getClass("NSStatusBar"), SelStatusBar);
            MsgSend(statusBar, SelRemoveStatusItem, _statusItem);
            _statusItem = IntPtr.Zero;
            _button = IntPtr.Zero;
            _menu = IntPtr.Zero;
            _target = IntPtr.Zero;
            NativeItems.Clear();
            _leftClick = null;
            _spec = null;
        }
        IsInstalled = false;
    }

    /// <summary>Standard visible cell (Apple HIG: 24×24 visual area in the menu
    /// bar). The status window's frame includes a hit-test extension that hangs
    /// below the bar — anchoring on it would float the popover, so the rect is
    /// clamped to the visible cell.</summary>
    private const double VisibleCellSide = 24;

    /// <summary>The button's rect in Avalonia screen coordinates, or false when
    /// the status item is not ours (fallback path / not yet laid out).</summary>
    public static bool TryGetStatusItemFrame(out PixelRect frame)
    {
        frame = default;
        if (!IsInstalled || _button == IntPtr.Zero) return false;
        var window = MsgSend(_button, SelWindow);
        if (window == IntPtr.Zero) return false;

        var winFrame = MsgSendRect(window, SelFrame); // screen coords, y up
        var rect = ConvertCocoaFrameToAvaloniaCoords(
            winFrame.X, winFrame.Y, winFrame.Width, winFrame.Height, GetPrimaryScreenCocoaMaxY());
        frame = new PixelRect(rect.X, rect.Y, rect.Width, (int)Math.Min(rect.Height, VisibleCellSide));
        return true;
    }

    /// <summary>Cocoa (y up) → Avalonia (y down) frame, matching Avalonia.Native's
    /// ConvertPointY: <c>avaloniaY = primaryScreenCocoaMaxY − cocoaY</c>. Pure so
    /// a headless test can pin the formula (NSRect itself is a private layout type).</summary>
    internal static PixelRect ConvertCocoaFrameToAvaloniaCoords(
        double x, double y, double width, double height, double primaryScreenCocoaMaxY)
    {
        var top = primaryScreenCocoaMaxY - (y + height);
        return new PixelRect(
            (int)Math.Round(x),
            (int)Math.Round(top),
            (int)Math.Round(width),
            (int)Math.Round(height));
    }

    private static double GetPrimaryScreenCocoaMaxY()
    {
        var screens = MsgSend(objc_getClass("NSScreen"), SelScreens);
        var first = MsgSend(screens, SelObjectAtIndex, IntPtr.Zero);
        var frame = MsgSendRect(first, SelFrame);
        return frame.Y + frame.Height; // NSMaxY
    }

    // ── Memory / NSString helpers ──

    private static IntPtr MakeImage(byte[] png)
    {
        var nsData = MsgSend(objc_getClass("NSData"), SelDataWithBytesLength, AllocBytes(png), (nuint)png.Length);
        return MsgSend(MsgSend(objc_getClass("NSImage"), SelAlloc), SelInitWithData, nsData);
    }

    private static IntPtr AllocBytes(byte[] bytes)
    {
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    private static IntPtr CreateNSString(string value)
    {
        var utf8 = Encoding.UTF8.GetBytes(value);
        var buffer = Marshal.AllocHGlobal(utf8.Length + 1); // NUL-terminated
        try
        {
            Marshal.Copy(utf8, 0, buffer, utf8.Length);
            Marshal.WriteByte(buffer, utf8.Length, 0);
            return MsgSend(MsgSend(objc_getClass("NSString"), SelAlloc), SelInitWithUtf8, buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void SetNSString(IntPtr receiver, IntPtr selector, string value)
    {
        var nsstring = CreateNSString(value);
        try
        {
            MsgSend(receiver, selector, nsstring);
        }
        finally
        {
            MsgSend(nsstring, SelRelease);
        }
    }
}
