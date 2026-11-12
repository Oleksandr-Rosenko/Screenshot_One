using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// macOS: власний значок у рядку меню (NSStatusItem) через Objective-C runtime.
    /// TrayIcon з Avalonia на Mac не повідомляє про клік (лише показує меню), а нам потрібна панель
    /// <see cref="TrayPanelWindow"/> як на Windows. Тому значок створюємо самі: кнопка NSStatusBarButton
    /// з target/action, де action — C#-метод (UnmanagedCallersOnly), зареєстрований як метод ObjC-класу.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static unsafe class MacStatusItem
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";

        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC)] private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, IntPtr extraBytes);
        [DllImport(ObjC)] private static extern void objc_registerClassPair(IntPtr cls);
        [DllImport(ObjC)] private static extern byte class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr Send(IntPtr receiver, IntPtr sel, IntPtr arg, nuint length);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendDouble(IntPtr receiver, IntPtr sel, double arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendSize(IntPtr receiver, IntPtr sel, CGSize size);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr SendNUInt(IntPtr receiver, IntPtr sel, nuint arg);

        [StructLayout(LayoutKind.Sequential)]
        private struct CGSize { public double Width, Height; }

        private static IntPtr _statusItem, _button, _target;

        /// <summary>Клік по значку (лівий або правий) — на головному потоці.</summary>
        public static event Action? Clicked;

        public static bool IsCreated => _button != IntPtr.Zero;

        private static IntPtr Sel(string name) => sel_registerName(name);

        private static IntPtr NSString(string value)
        {
            IntPtr utf8 = Marshal.StringToCoTaskMemUTF8(value);
            try { return Send(objc_getClass("NSString"), Sel("stringWithUTF8String:"), utf8); }
            finally { Marshal.FreeCoTaskMem(utf8); }
        }

        private static IntPtr NSImageFromPng(byte[] png)
        {
            fixed (byte* p = png)
            {
                IntPtr data = Send(objc_getClass("NSData"), Sel("dataWithBytes:length:"), (IntPtr)p, (nuint)png.Length);
                IntPtr image = Send(Send(objc_getClass("NSImage"), Sel("alloc")), Sel("initWithData:"), data);
                if (image == IntPtr.Zero) throw new InvalidOperationException("NSImage initWithData failed");
                SendSize(image, Sel("setSize:"), new CGSize { Width = 18, Height = 18 }); // розмір у рядку меню (точки)
                return image;
            }
        }

        [UnmanagedCallersOnly]
        private static void OnClick(IntPtr self, IntPtr sel, IntPtr sender)
        {
            try { Clicked?.Invoke(); }
            catch (Exception ex) { AppLog.Error(ex, "Mac: клік по значку в рядку меню"); }
        }

        /// <summary>Створює значок. Викликати на головному потоці (UI-потік Avalonia на Mac).</summary>
        public static void Create(byte[] iconPng, string toolTip)
        {
            if (IsCreated) return;

            // ObjC-клас із методом soClick:, реалізованим у C#
            IntPtr cls = objc_getClass("SOStatusItemTarget");
            if (cls == IntPtr.Zero)
            {
                cls = objc_allocateClassPair(objc_getClass("NSObject"), "SOStatusItemTarget", IntPtr.Zero);
                delegate* unmanaged<IntPtr, IntPtr, IntPtr, void> fn = &OnClick;
                class_addMethod(cls, Sel("soClick:"), (IntPtr)fn, "v@:@");
                objc_registerClassPair(cls);
            }
            _target = Send(Send(cls, Sel("alloc")), Sel("init"));

            IntPtr bar = Send(objc_getClass("NSStatusBar"), Sel("systemStatusBar"));
            _statusItem = SendDouble(bar, Sel("statusItemWithLength:"), -1.0); // NSVariableStatusItemLength
            Send(_statusItem, Sel("retain")); // рядок меню не тримає посилання сам
            _button = Send(_statusItem, Sel("button"));
            if (_button == IntPtr.Zero) throw new InvalidOperationException("NSStatusItem.button is null");

            Send(_button, Sel("setImage:"), NSImageFromPng(iconPng));
            Send(_button, Sel("setToolTip:"), NSString(toolTip));
            Send(_button, Sel("setTarget:"), _target);
            Send(_button, Sel("setAction:"), Sel("soClick:"));
            // Реагувати і на лівий, і на правий клік (NSEventMaskLeftMouseUp | NSEventMaskRightMouseUp)
            SendNUInt(_button, Sel("sendActionOn:"), (nuint)((1 << 2) | (1 << 4)));
            AppLog.Write("[MacStatusItem] значок у рядку меню створено");
        }

        public static void SetImage(byte[] iconPng)
        {
            if (!IsCreated) return;
            try { Send(_button, Sel("setImage:"), NSImageFromPng(iconPng)); }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        /// <summary>Для самоперевірки в CI: програмне «натискання» значка (той самий шлях target/action).</summary>
        public static void PerformClick()
        {
            if (IsCreated) Send(_button, Sel("performClick:"), IntPtr.Zero);
        }
    }
}
