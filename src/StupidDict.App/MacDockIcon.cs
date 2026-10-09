using System;
using System.IO;
using System.Runtime.InteropServices;
using Avalonia.Platform;

namespace StupidDict.App;

/// <summary>
/// macOS Dock icon: a bare `dotnet run` process has no .app bundle, so LaunchServices
/// never reads Info.plist's CFBundleIconFile and the Dock shows the generic exec icon.
/// App posts this after the app has finished launching (regular policy, run loop up);
/// the packaged bundle ships the same artwork as an icns via CFBundleIconFile, so this
/// is an override, not an idempotent set.
/// The runtime path feeds the flattened PNG on purpose — the format Electron/SDL/Godot
/// push through this API. Note NSRunningApplication.icon does NOT reflect runtime sets
/// (it reads LaunchServices only), so the set cannot be verified programmatically; look
/// at the Dock. Rounded corners must be baked into the asset: runtime icons get no
/// system mask (macOS 26 masks bundle icons only; Windows .ico gets no treatment either).
/// Avalonia does not expose this API, so it is implemented by sending ObjC messages
/// via libobjc; the Dock icon is purely cosmetic, any failure stays silent.
/// </summary>
internal static class MacDockIcon
{
    private const string LibObjC = "/usr/lib/libobjc.dylib";

    public static void TrySetFromEmbeddedIcon()
    {
        if (!OperatingSystem.IsMacOS()) return;
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://StupidDict/Assets/app-icon.png"));
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            SetApplicationIcon(memory.ToArray());
        }
        catch
        {
            // Missing asset, no window server, anything else — never block startup
        }
    }

    private static unsafe void SetApplicationIcon(byte[] png)
    {
        fixed (byte* bytes = png)
        {
            var data = MsgSendBytes(ObjCGetClass("NSData"), Selector("dataWithBytes:length:"),
                (IntPtr)bytes, (UIntPtr)png.LongLength);
            if (data == IntPtr.Zero) return;

            var image = MsgSendObject(MsgSend(ObjCGetClass("NSImage"), Selector("alloc")),
                Selector("initWithData:"), data);
            if (image == IntPtr.Zero) return;

            var app = MsgSend(ObjCGetClass("NSApplication"), Selector("sharedApplication"));
            if (app != IntPtr.Zero)
                MsgSendObject(app, Selector("setApplicationIconImage:"), image);

            // B-015: the image came from alloc (create-side retainCount 1) and
            // setApplicationIconImage: retained what it needs, so the owned
            // reference must be balanced with a release or one NSImage and its
            // bitmap data leak per run. Unconditional — even a failed set has
            // to drop the reference. `data` is a dataWithBytes:... factory
            // product (autoreleased, drained by the run-loop pool): sending it
            // a release here would over-release, so it is left alone.
            MsgSend(image, Selector("release"));
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
    private static extern IntPtr MsgSendBytes(IntPtr receiver, IntPtr selector, IntPtr bytes, UIntPtr length);
}
