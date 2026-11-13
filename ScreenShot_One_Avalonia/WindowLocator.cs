using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Знімок окремого вікна (2.10.0): межі видимих вікон у пікселях екрана, від верхнього до нижнього.
    /// Оверлей виділення підсвічує вікно під курсором; клік без перетягування знімає саме його.
    /// Windows — EnumWindows + DWM (точні межі без невидимої рамки); macOS — CGWindowListCopyWindowInfo.
    /// Власні вікна програми не враховуються.
    /// </summary>
    public static class WindowLocator
    {
        public static List<PixelRect> GetWindows()
        {
            try
            {
                if (OperatingSystem.IsWindows()) return WindowsImpl.Get();
                if (OperatingSystem.IsMacOS()) return MacImpl.Get();
            }
            catch (Exception ex) { AppLog.Warn(ex); }
            return new List<PixelRect>();
        }

        /// <summary>Найвище вікно, що містить точку (публічний для тестів).</summary>
        public static PixelRect? HitTest(IReadOnlyList<PixelRect> windows, PixelPoint point)
        {
            foreach (var r in windows)
                if (point.X >= r.X && point.X < r.Right && point.Y >= r.Y && point.Y < r.Bottom) return r;
            return null;
        }

        [SupportedOSPlatform("windows")]
        private static class WindowsImpl
        {
            private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
            [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
            [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
            [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
            [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT value, int size);
            [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] private static extern int DwmGetWindowAttributeInt(IntPtr hWnd, int attr, out int value, int size);

            [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
            private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;

            public static List<PixelRect> Get()
            {
                var list = new List<PixelRect>();
                uint own = (uint)Environment.ProcessId;
                EnumWindows((h, _) =>
                {
                    if (!IsWindowVisible(h) || IsIconic(h) || GetWindowTextLength(h) == 0) return true;
                    GetWindowThreadProcessId(h, out uint pid);
                    if (pid == own) return true;
                    if (DwmGetWindowAttributeInt(h, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return true; // приховані UWP-вікна
                    if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(h, out r)) return true;
                    int w = r.Right - r.Left, ht = r.Bottom - r.Top;
                    if (w >= 40 && ht >= 40) list.Add(new PixelRect(r.Left, r.Top, w, ht));
                    return true;
                }, IntPtr.Zero);
                return list;
            }
        }

        [SupportedOSPlatform("macos")]
        private static class MacImpl
        {
            private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
            private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
            [DllImport(CG)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
            [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dict, out CGRect rect);
            [DllImport(CF)] private static extern long CFArrayGetCount(IntPtr array);
            [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
            [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
            [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out int value);
            [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
            [DllImport(CF)] private static extern void CFRelease(IntPtr obj);

            [StructLayout(LayoutKind.Sequential)] private struct CGRect { public double X, Y, W, H; }
            private const uint OnScreenOnly = 1, ExcludeDesktop = 16, Utf8 = 0x08000100;
            private const int kCFNumberIntType = 9;

            public static List<PixelRect> Get()
            {
                var list = new List<PixelRect>();
                double scale = ScreenService.GetPrimaryScreen()?.Scaling ?? 1.0;
                IntPtr kLayer = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowLayer", Utf8);
                IntPtr kBounds = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowBounds", Utf8);
                IntPtr kPid = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowOwnerPID", Utf8);
                IntPtr arr = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktop, 0);
                try
                {
                    if (arr == IntPtr.Zero) return list;
                    int own = Environment.ProcessId;
                    long n = CFArrayGetCount(arr);
                    for (long i = 0; i < n; i++)
                    {
                        IntPtr d = CFArrayGetValueAtIndex(arr, i);
                        if (!CFNumberGetValue(CFDictionaryGetValue(d, kLayer), kCFNumberIntType, out int layer) || layer != 0) continue;
                        if (CFNumberGetValue(CFDictionaryGetValue(d, kPid), kCFNumberIntType, out int pid) && pid == own) continue;
                        IntPtr b = CFDictionaryGetValue(d, kBounds);
                        if (b == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(b, out CGRect r)) continue;
                        if (r.W < 40 || r.H < 40) continue;
                        list.Add(new PixelRect((int)(r.X * scale), (int)(r.Y * scale), (int)(r.W * scale), (int)(r.H * scale)));
                    }
                }
                finally
                {
                    if (arr != IntPtr.Zero) CFRelease(arr);
                    CFRelease(kLayer); CFRelease(kBounds); CFRelease(kPid);
                }
                return list;
            }
        }
    }
}
