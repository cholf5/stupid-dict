using System;
using System.Runtime.InteropServices;
using System.Text;

namespace StupidDict.App;

/// <summary>
/// macOS menu-bar app title for bare `dotnet run` runs: an unbundled process gets
/// its bold menu-bar title from the process name, and AvaloniaNative's
/// SetApplicationTitle (invoked once at platform init with Application.Current.Name)
/// sets it via NSProcessInfo.setProcessName. There is no managed API to re-push it,
/// so App replays that exact call here when the UI language changes, keeping the
/// menu name in step with the window title. Packaged bundles derive the menu title
/// from CFBundleName instead and ignore the process name — the set is then visually
/// a no-op. Same interop posture as MacDockIcon: libobjc message sends, silent on
/// any failure.
/// </summary>
internal static class MacAppTitle
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";

    public static void TrySet(string name)
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            SetProcessName(name);
        }
        catch
        {
            // No window server, unexpected class layout, anything else —
            // purely cosmetic, never block the caller
        }
    }

    private static unsafe void SetProcessName(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* p = bytes)
        {
            // NUL-terminated UTF-8, as stringWithUTF8String: expects; the message
            // send must stay inside fixed — the pinned pointer dies with the block
            var title = MsgSendCString(ObjCGetClass("NSString"), Selector("stringWithUTF8String:"),
                (IntPtr)p);
            if (title == IntPtr.Zero) return;

            var processInfo = MsgSend(ObjCGetClass("NSProcessInfo"), Selector("processInfo"));
            if (processInfo != IntPtr.Zero)
                MsgSendObject(processInfo, Selector("setProcessName:"), title);
        }
    }

    private static IntPtr ObjCGetClass(string name) => objc_getClass(name);

    private static IntPtr Selector(string name) => sel_registerName(name);

    [DllImport(LibObjC)]
    private static extern IntPtr objc_getClass(string name);

    [DllImport(LibObjC)]
    private static extern IntPtr sel_registerName(string name);

    // objc_msgSend takes parameters per the called method's signature, so each arity
    // needs its own declaration (same EntryPoint, distinct managed signatures)

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSend(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendObject(IntPtr receiver, IntPtr selector, IntPtr argument);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr MsgSendCString(IntPtr receiver, IntPtr selector, IntPtr cstring);
}
