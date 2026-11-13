using Avalonia;
using Avalonia.Controls;
using System;
using System.Runtime.InteropServices;

namespace ScreenShot_One_Avalonia
{
    public static class PlatformHelper
    {
        // --- WINDOWS API ---
        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_LAYERED = 0x00080000;

        // Магічний прапорець Windows, який робить вікно невидимим для скріншотів і запису відео
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        /// <summary>
        /// Робить вікно повністю прозорим для кліків мишки на рівні ОС.
        /// </summary>
        public static void MakeWindowClickThrough(Window window)
        {
            if (OperatingSystem.IsWindows())
            {
                var hwnd = window.TryGetPlatformHandle()?.Handle;
                if (hwnd.HasValue && hwnd.Value != IntPtr.Zero)
                {
                    int exStyle = GetWindowLong(hwnd.Value, GWL_EXSTYLE);
                    SetWindowLong(hwnd.Value, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                try { MacWindow.SetClickThrough(window); }
                catch (Exception ex) { AppLog.Error(ex, "Mac: вікно прозоре для миші"); }
            }
            else if (OperatingSystem.IsLinux())
            {
                // TODO для Linux: XShape
            }
        }

        /// <summary>
        /// Робить вікно невидимим для програм запису екрану (OBS, FFmpeg і т.д.)
        /// </summary>
        public static void HideWindowFromScreenCapture(Window window)
        {
            if (OperatingSystem.IsWindows())
            {
                var hwnd = window.TryGetPlatformHandle()?.Handle;
                if (hwnd.HasValue && hwnd.Value != IntPtr.Zero)
                {
                    SetWindowDisplayAffinity(hwnd.Value, WDA_EXCLUDEFROMCAPTURE);
                }
            }
            else if (OperatingSystem.IsMacOS())
            {
                try { MacWindow.ExcludeFromCapture(window); }
                catch (Exception ex) { AppLog.Error(ex, "Mac: виключення вікна зі знімків"); }
            }
        }

        /// <summary>
        /// Оверлей на весь екран (виділення зони для знімка чи відео).
        /// Windows — FullScreen (вікно без рамки поверх панелі завдань). macOS — НЕ FullScreen: там це окремий робочий стіл
        /// з анімацією, і «заморожений» екран виглядав як картинка; натомість вікно без рамки на весь екран над рядком меню (<see cref="MacWindow"/>).
        /// </summary>
        public static void SetupScreenOverlay(Window window, PixelRect bounds, double scaling)
        {
            window.Position = new PixelPoint(bounds.X, bounds.Y);
            if (OperatingSystem.IsMacOS())
            {
                if (scaling <= 0) scaling = 1.0;
                window.WindowState = WindowState.Normal;
                window.CanResize = false;
                window.Width = bounds.Width / scaling;
                window.Height = bounds.Height / scaling;
                window.Opened += (_, _) =>
                {
                    if (!OperatingSystem.IsMacOS()) return;
                    try
                    {
                        if (!MacWindow.CoverScreen(window)) AppLog.Write("Mac: не вдалося розгорнути оверлей на весь екран (немає NSWindow)");
                    }
                    catch (Exception ex) { AppLog.Error(ex, "Mac: оверлей на весь екран"); }
                };
            }
            else
            {
                window.WindowState = WindowState.FullScreen;
            }
        }
    }
}
