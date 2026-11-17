using Avalonia;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Звідки знято скріншот (3.1.0): програма й заголовок вікна, що були на передньому плані в момент знімка.
    /// Потрібно для пошуку в історії («chrome», «jira», «звіт»). Запам'ятовується на початку знімка
    /// (до появи оверлея), а редактор/мініатюра забирають це значення разом із бітмапом.
    /// 3.2.2: для виділеної зони чи вікна — вікно ПІД виділенням (NarrowTo), а не активне: раніше знімок браузера,
    /// зроблений, поки активним був інший застосунок, підписувався його назвою.
    /// </summary>
    public sealed record CaptureSource(string? App, string? Title)
    {
        private static CaptureSource? _pending;

        /// <summary>Запам'ятати передній план — викликати на початку знімка.</summary>
        public static void Remember()
        {
            try { _pending = Detect(); }
            catch (Exception ex) { AppLog.Warn(ex); _pending = null; }
        }

        /// <summary>
        /// Уточнити джерело за виділеною зоною (пікселі екрана): найвище вікно, що містить центр зони.
        /// Якщо такого немає (зона на робочому столі) — лишається активне вікно.
        /// </summary>
        public static void NarrowTo(IReadOnlyList<WindowLocator.Entry> windows, PixelRect area)
        {
            try
            {
                var hit = Pick(windows, area);
                if (hit == null) return;
                string? app = hit.Owner ?? (OperatingSystem.IsWindows() ? WindowsImpl.AppName((uint)hit.Pid) : null);
                if (app != null) _pending = new CaptureSource(app, hit.Title);
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        /// <summary>Найвище вікно, що містить центр зони (публічний для тестів).</summary>
        public static WindowLocator.Entry? Pick(IReadOnlyList<WindowLocator.Entry> windows, PixelRect area)
        {
            var c = new PixelPoint(area.X + area.Width / 2, area.Y + area.Height / 2);
            return windows.FirstOrDefault(w => c.X >= w.Bounds.X && c.X < w.Bounds.Right && c.Y >= w.Bounds.Y && c.Y < w.Bounds.Bottom);
        }

        /// <summary>Забрати запам'ятоване (лише один раз — для нового знімка).</summary>
        public static CaptureSource? TakePending()
        {
            var s = _pending;
            _pending = null;
            return s;
        }

        public static CaptureSource? Detect()
        {
            if (OperatingSystem.IsWindows()) return WindowsImpl.Detect();
            if (OperatingSystem.IsMacOS()) return MacImpl.Detect();
            return null;
        }

        [SupportedOSPlatform("windows")]
        private static class WindowsImpl
        {
            private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
            [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
            [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
            [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
            [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
            [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

            public static CaptureSource? Detect()
            {
                uint own = (uint)Environment.ProcessId;
                IntPtr target = GetForegroundWindow();
                GetWindowThreadProcessId(target, out uint pid);
                if (target == IntPtr.Zero || pid == own || GetWindowTextLength(target) == 0)
                {
                    // Знімок з панелі трею: передній план — наша панель; беремо найвище чуже вікно (EnumWindows — порядок Z)
                    target = IntPtr.Zero;
                    EnumWindows((h, _) =>
                    {
                        if (!IsWindowVisible(h) || IsIconic(h) || GetWindowTextLength(h) == 0) return true;
                        GetWindowThreadProcessId(h, out uint p);
                        if (p == own) return true;
                        target = h; pid = p;
                        return false;
                    }, IntPtr.Zero);
                    if (target == IntPtr.Zero) return null;
                }
                var sb = new StringBuilder(512);
                GetWindowText(target, sb, sb.Capacity);
                return new CaptureSource(AppName(pid), sb.Length > 0 ? sb.ToString() : null);
            }

            public static string? AppName(uint pid)
            {
                try
                {
                    using var p = Process.GetProcessById((int)pid);
                    // «Google Chrome» замість «chrome» — якщо опис доступний (для чужих елевованих процесів — ні)
                    try
                    {
                        string? desc = p.MainModule?.FileVersionInfo.FileDescription;
                        if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
                    }
                    catch (Exception ex) { AppLog.Warn(ex); }
                    return p.ProcessName;
                }
                catch (Exception ex) { AppLog.Warn(ex); return null; }
            }
        }

        [SupportedOSPlatform("macos")]
        private static class MacImpl
        {
            private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
            private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
            [DllImport(CG)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);
            [DllImport(CF)] private static extern long CFArrayGetCount(IntPtr array);
            [DllImport(CF)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
            [DllImport(CF)] private static extern IntPtr CFDictionaryGetValue(IntPtr dict, IntPtr key);
            [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, int type, out int value);
            [DllImport(CF)] private static extern IntPtr CFStringCreateWithCString(IntPtr alloc, string s, uint encoding);
            [DllImport(CF)] private static extern long CFStringGetLength(IntPtr s);
            [DllImport(CF)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(IntPtr s, byte[] buffer, long size, uint encoding);
            [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
            private const uint OnScreenOnly = 1, ExcludeDesktop = 16, Utf8 = 0x08000100;

            private static string? Str(IntPtr cf)
            {
                if (cf == IntPtr.Zero) return null;
                long len = CFStringGetLength(cf);
                var buf = new byte[len * 4 + 1];
                if (!CFStringGetCString(cf, buf, buf.Length, Utf8)) return null;
                int n = Array.IndexOf(buf, (byte)0);
                return Encoding.UTF8.GetString(buf, 0, n < 0 ? buf.Length : n);
            }

            /// <summary>Перше (найвище) звичайне вікно чужої програми: назва програми й заголовок.</summary>
            public static CaptureSource? Detect()
            {
                IntPtr kLayer = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowLayer", Utf8);
                IntPtr kPid = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowOwnerPID", Utf8);
                IntPtr kOwner = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowOwnerName", Utf8);
                IntPtr kName = CFStringCreateWithCString(IntPtr.Zero, "kCGWindowName", Utf8);
                IntPtr arr = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktop, 0);
                try
                {
                    if (arr == IntPtr.Zero) return null;
                    int own = Environment.ProcessId;
                    long n = CFArrayGetCount(arr);
                    for (long i = 0; i < n; i++)
                    {
                        IntPtr d = CFArrayGetValueAtIndex(arr, i);
                        if (!CFNumberGetValue(CFDictionaryGetValue(d, kLayer), 9, out int layer) || layer != 0) continue;
                        if (CFNumberGetValue(CFDictionaryGetValue(d, kPid), 9, out int pid) && pid == own) continue;
                        string? app = Str(CFDictionaryGetValue(d, kOwner));
                        string? title = Str(CFDictionaryGetValue(d, kName));
                        if (string.IsNullOrEmpty(app)) continue;
                        return new CaptureSource(app, string.IsNullOrWhiteSpace(title) ? null : title);
                    }
                    return null;
                }
                finally
                {
                    if (arr != IntPtr.Zero) CFRelease(arr);
                    CFRelease(kLayer); CFRelease(kPid); CFRelease(kOwner); CFRelease(kName);
                }
            }
        }
    }
}
