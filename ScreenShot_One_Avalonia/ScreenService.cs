using System;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform;

namespace ScreenShot_One_Avalonia
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct Win32Point
    {
        public int X;
        public int Y;
    }

    internal static class NativeWin32
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetCursorPos(out Win32Point lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szDevice;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);

        /// <summary>
        /// Повертає системну назву монітора (на кшталт \\.\DISPLAY1) — ту саму, що дає ScreenRecorderLib.
        /// </summary>
        public static bool TryGetMonitorDeviceName(IntPtr hMonitor, out string deviceName)
        {
            deviceName = string.Empty;
            try
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = string.Empty };
                if (!GetMonitorInfo(hMonitor, ref info)) return false;
                deviceName = info.szDevice ?? string.Empty;
                return deviceName.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static class ScreenService
    {
        // Службове вікно, яке ніколи не показується. Потрібне лише для доступу до списку екранів,
        // коли в програмі не відкрито жодного вікна (вона працює з трею).
        private static Window? _screensHelperWindow;

        public static Screens? GetScreens()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var window in desktop.Windows)
                {
                    if (window.Screens != null) return window.Screens;
                }
                if (desktop.MainWindow?.Screens != null)
                {
                    return desktop.MainWindow.Screens;
                }

                // Жодного відкритого вікна: раніше тут повертався null, і «Скріншот активного екрана»
                // з трею або гарячою клавішею нічого не робив, а «усі екрани» знімали фіксовані 1920×1080.
                // Screens доступні в Window одразу після конструктора, показувати його не треба.
                try
                {
                    _screensHelperWindow ??= new Window
                    {
                        ShowInTaskbar = false,
                        ShowActivated = false,
                        Width = 1,
                        Height = 1
                    };
                    return _screensHelperWindow.Screens;
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[ScreenService] Не вдалося отримати список екранів: {ex.Message}");
                }
            }
            return null;
        }

        /// <summary>
        /// Знаходить екран Avalonia за DeviceName з MonitorInfo (список моніторів у панелі запису).
        /// Windows: DeviceName — системна назва монітора (\\.\DISPLAY1), зіставляємо через HMONITOR.
        /// macOS: DeviceName — порядковий номер екрана.
        /// Раніше тут порівнювали Screen.ToString() з DeviceName, а це ніколи не збігалося.
        /// </summary>
        public static Screen? FindScreenByDeviceName(Screens? screens, string? deviceName)
        {
            if (screens == null || string.IsNullOrWhiteSpace(deviceName)) return null;

            if (OperatingSystem.IsWindows())
            {
                foreach (var screen in screens.All)
                {
                    var handle = screen.TryGetPlatformHandle();
                    if (handle != null
                        && NativeWin32.TryGetMonitorDeviceName(handle.Handle, out var name)
                        && string.Equals(name.Trim(), deviceName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        return screen;
                    }
                }
                return null;
            }

            if (int.TryParse(deviceName, out int index) && index >= 0 && index < screens.All.Count)
            {
                return screens.All[index];
            }
            return null;
        }

        public static Screen? GetPrimaryScreen()
        {
            var screens = GetScreens();
            return screens?.Primary ?? screens?.All.FirstOrDefault();
        }

        public static Screen? GetScreenFromPoint(PixelPoint point)
        {
            var screens = GetScreens();
            if (screens == null) return null;
            return screens.ScreenFromPoint(point) ?? screens.Primary ?? screens.All.FirstOrDefault();
        }

        public static PixelRect GetVirtualScreenBounds()
        {
            var screens = GetScreens();
            if (screens == null || screens.All.Count == 0)
            {
                return new PixelRect(0, 0, 1920, 1080);
            }

            int minX = screens.All.Min(s => s.Bounds.X);
            int minY = screens.All.Min(s => s.Bounds.Y);
            int maxX = screens.All.Max(s => s.Bounds.X + s.Bounds.Width);
            int maxY = screens.All.Max(s => s.Bounds.Y + s.Bounds.Height);

            return new PixelRect(minX, minY, maxX - minX, maxY - minY);
        }

        public static PixelPoint GetCursorPosition()
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    if (NativeWin32.GetCursorPos(out var pt))
                    {
                        return new PixelPoint(pt.X, pt.Y);
                    }
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            }

            // macOS: позиція курсора з CoreGraphics (точки, початок — лівий верхній кут головного екрана)
            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    IntPtr ev = MacCursor.CGEventCreate(IntPtr.Zero);
                    if (ev != IntPtr.Zero)
                    {
                        var p = MacCursor.CGEventGetLocation(ev);
                        MacCursor.CFRelease(ev);
                        double scale = GetPrimaryScreen()?.Scaling ?? 1.0;
                        return new PixelPoint((int)(p.X * scale), (int)(p.Y * scale));
                    }
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            }

            // Cross-platform fallback: center of primary screen
            var primary = GetPrimaryScreen();
            if (primary != null)
            {
                return new PixelPoint(primary.Bounds.X + primary.Bounds.Width / 2, primary.Bounds.Y + primary.Bounds.Height / 2);
            }
            return new PixelPoint(0, 0);
        }
    }

    internal static class MacCursor
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        public struct CGPoint { public double X, Y; }
        [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern IntPtr CGEventCreate(IntPtr source);
        [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
        public static extern CGPoint CGEventGetLocation(IntPtr ev);
        [System.Runtime.InteropServices.DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
        public static extern void CFRelease(IntPtr obj);
    }
}
