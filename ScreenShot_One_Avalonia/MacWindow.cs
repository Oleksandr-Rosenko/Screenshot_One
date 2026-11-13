using Avalonia.Controls;
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// macOS: керування NSWindow вікон Avalonia через Objective-C runtime.
    /// Головне — оверлеї виділення (знімок, зона відео): <c>WindowState.FullScreen</c> на Mac — це «рідний» повноекранний режим
    /// з окремим робочим столом (Space) і анімацією переходу, тому «заморожений» екран виглядав як картинка, що виїжджає.
    /// Замість цього оверлей — звичайне вікно без рамки на весь екран, на рівні над рядком меню й Dock, на поточному робочому столі.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static class MacWindow
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNInt(IntPtr receiver, IntPtr sel, nint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNUInt(IntPtr receiver, IntPtr sel, nuint arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendBool(IntPtr receiver, IntPtr sel, byte arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendRetNInt(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nuint SendRetNUInt(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSetFrame(IntPtr receiver, IntPtr sel, CGRect frame, byte display);

        // NSRect (32 байти): на arm64 повертається в регістрах (objc_msgSend), на x64 — через пам'ять (objc_msgSend_stret)
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern CGRect SendRetRectArm64(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend_stret")] private static extern CGRect SendRetRectX64(IntPtr receiver, IntPtr sel);

        [StructLayout(LayoutKind.Sequential)]
        public struct CGRect
        {
            public double X, Y, Width, Height;
            public override string ToString() => $"{X:0},{Y:0} {Width:0}×{Height:0}";
        }

        // Рівень вікна — над рядком меню (24), Dock (20) і спливаючими меню (101)
        private const nint OverlayLevel = 1000;              // NSScreenSaverWindowLevel
        private const nuint NSWindowStyleMaskFullScreen = 1 << 14;
        // canJoinAllSpaces | stationary | ignoresCycle | fullScreenAuxiliary — видно й поверх програм у повноекранному режимі
        private const nuint OverlayCollectionBehavior = (1 << 0) | (1 << 4) | (1 << 6) | (1 << 8);

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static CGRect GetRect(IntPtr obj, string selector) =>
            RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? SendRetRectX64(obj, Sel(selector))
                : SendRetRectArm64(obj, Sel(selector));

        /// <summary>NSWindow вікна Avalonia (IntPtr.Zero, якщо вікно ще не створене).</summary>
        public static IntPtr GetNSWindow(Window window)
        {
            var handle = window.TryGetPlatformHandle();
            if (handle == null) return IntPtr.Zero;
            // Avalonia.Native: IMacOSTopLevelPlatformHandle.NSWindow (через рефлексію — без залежності від збірки Avalonia.Native)
            if (handle.GetType().GetProperty("NSWindow")?.GetValue(handle) is IntPtr nsWindow && nsWindow != IntPtr.Zero)
                return nsWindow;
            if (handle.HandleDescriptor == "NSWindow") return handle.Handle;
            if (handle.HandleDescriptor == "NSView" && handle.Handle != IntPtr.Zero) return Send(handle.Handle, Sel("window"));
            return IntPtr.Zero;
        }

        /// <summary>Розтягує вікно-оверлей на весь екран, на якому воно стоїть (разом із рядком меню), поверх усього.</summary>
        public static bool CoverScreen(Window window)
        {
            IntPtr ns = GetNSWindow(window);
            if (ns == IntPtr.Zero) return false;

            SendNInt(ns, Sel("setLevel:"), OverlayLevel);
            SendNUInt(ns, Sel("setCollectionBehavior:"), OverlayCollectionBehavior);
            SendBool(ns, Sel("setHidesOnDeactivate:"), 0);

            IntPtr screen = Send(ns, Sel("screen"));
            if (screen == IntPtr.Zero) screen = Send(objc_getClass("NSScreen"), Sel("mainScreen"));
            if (screen == IntPtr.Zero) return false;

            var frame = GetRect(screen, "frame");
            SendSetFrame(ns, Sel("setFrame:display:"), frame, 1);

            // Програма живе в рядку меню й може бути неактивною — без цього Esc не дійде до оверлея
            IntPtr app = Send(objc_getClass("NSApplication"), Sel("sharedApplication"));
            if (app != IntPtr.Zero) SendBool(app, Sel("activateIgnoringOtherApps:"), 1);
            Send(ns, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
            return true;
        }

        /// <summary>Вікно не перехоплює мишу (рамка зони запису відео).</summary>
        public static void SetClickThrough(Window window, bool clickThrough = true)
        {
            IntPtr ns = GetNSWindow(window);
            if (ns != IntPtr.Zero) SendBool(ns, Sel("setIgnoresMouseEvents:"), clickThrough ? (byte)1 : (byte)0);
        }

        /// <summary>
        /// Не показувати вікно в знімках і записі екрана (NSWindowSharingNone). Працює для CoreGraphics і ffmpeg;
        /// ScreenCaptureKit на нових macOS цей прапорець може ігнорувати.
        /// </summary>
        public static void ExcludeFromCapture(Window window)
        {
            IntPtr ns = GetNSWindow(window);
            if (ns != IntPtr.Zero) SendNUInt(ns, Sel("setSharingType:"), 0);
        }

        /// <summary>Стан вікна для самоперевірки: рамка, рамка екрана, рівень, чи «рідний» повноекранний режим.</summary>
        public static (CGRect Frame, CGRect Screen, nint Level, bool NativeFullScreen)? Describe(Window window)
        {
            IntPtr ns = GetNSWindow(window);
            if (ns == IntPtr.Zero) return null;
            IntPtr screen = Send(ns, Sel("screen"));
            var screenFrame = screen != IntPtr.Zero ? GetRect(screen, "frame") : default;
            bool fullScreen = (SendRetNUInt(ns, Sel("styleMask")) & NSWindowStyleMaskFullScreen) != 0;
            return (GetRect(ns, "frame"), screenFrame, SendRetNInt(ns, Sel("level")), fullScreen);
        }
    }
}
