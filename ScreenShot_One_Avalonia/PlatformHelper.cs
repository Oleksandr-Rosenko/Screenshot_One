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
                // TODO для Mac: NSWindow.ignoresMouseEvents = YES
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
        }
    }
}