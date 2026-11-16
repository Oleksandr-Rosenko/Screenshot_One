using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Самоперевірка запису (CI, SO_SELFTEST_RECORD=1): відкриває панель запису, пише кілька секунд усього екрана,
    /// витягує кадр і перевіряє, чи не потрапила в нього сама панель (її темний фон #111816).
    /// </summary>
    internal static class RecordingSelfTest
    {
        public static async Task RunAsync(string? shotsDir)
        {
            if (OperatingSystem.IsMacOS())
            {
                // Mac: двічі — без прихування й з прихуванням (NSWindowSharingNone), щоб бачити, чи це діє на запис
                await RecordOnceAsync(shotsDir, "без прихування", hide: false);
                await RecordOnceAsync(shotsDir, "з прихуванням", hide: true);
            }
            else
            {
                await RecordOnceAsync(shotsDir, "звичайний", hide: true);
            }
        }

        private static async Task RecordOnceAsync(string? shotsDir, string label, bool hide)
        {
            try
            {
                VideoControlWindow? panel = null;
                await Dispatcher.UIThread.InvokeAsync(() => { panel = new VideoControlWindow(); panel.Show(); });
                await Task.Delay(1500);

                var (panelRect, screenRect) = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (OperatingSystem.IsMacOS() && !hide) MacWindow.SetSharingType(panel!, 1);
                    var root = panel!.FindControl<Border>("RootBorder")!;
                    var tl = panel.PointToScreen(root.TranslatePoint(new Point(0, 0), panel) ?? default);
                    var br = panel.PointToScreen(root.TranslatePoint(new Point(root.Bounds.Width, root.Bounds.Height), panel) ?? default);
                    var screen = panel.Screens.Primary;
                    return (new PixelRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y), screen?.Bounds ?? new PixelRect(0, 0, 1024, 768));
                });

                var before = VideoControlWindow.LastRecordedPath;
                await Dispatcher.UIThread.InvokeAsync(() =>
                    panel!.FindControl<Button>("BtnRecordToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
                await Task.Delay(4000);
                bool recording = VideoRecorderManager.IsRecording;
                await Dispatcher.UIThread.InvokeAsync(() =>
                    panel!.FindControl<Button>("BtnRecordToggle")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));

                for (int i = 0; i < 60 && VideoControlWindow.LastRecordedPath == before; i++) await Task.Delay(500);
                string? path = VideoControlWindow.LastRecordedPath;
                if (path == null || path == before || !File.Exists(path))
                {
                    AppLog.Write($"[SelfTest] запис ({label}): файл не створено (запис ішов: {recording}) — False");
                    await CloseExtraWindowsAsync();
                    return;
                }

                string? ffmpeg = GifExport.FindFfmpeg();
                string frame = Path.Combine(shotsDir ?? Path.GetTempPath(), $"record_{(hide ? "hidden" : "visible")}.png");
                if (shotsDir != null) Directory.CreateDirectory(shotsDir);
                var psi = new ProcessStartInfo { FileName = ffmpeg ?? "ffmpeg", UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
                foreach (var a in new[] { "-y", "-hide_banner", "-loglevel", "error", "-ss", "2", "-i", path, "-frames:v", "1", frame }) psi.ArgumentList.Add(a);
                using (var p = Process.Start(psi)) { if (p != null) { await p.StandardError.ReadToEndAsync(); await p.WaitForExitAsync(); } }

                double share = -1;
                string size = "?";
                if (File.Exists(frame))
                {
                    using var bmp = new Bitmap(frame);
                    size = $"{bmp.PixelSize.Width}×{bmp.PixelSize.Height}";
                    share = DarkShare(bmp, panelRect, screenRect);
                }
                bool panelVisible = share > 0.3;
                AppLog.Write($"[SelfTest] запис ({label}): файл {new FileInfo(path).Length / 1024} КБ, кадр {size}, панель {panelRect}, " +
                             $"темних пікселів у місці панелі {(share < 0 ? "?" : share.ToString("P0"))} — панель у відео: {panelVisible}");
                await CloseExtraWindowsAsync();
            }
            catch (Exception ex) { AppLog.Error(ex, "Самоперевірка запису"); }
        }

        /// <summary>Частка пікселів кольору панелі (#111816 ± допуск) у місці панелі на кадрі відео.</summary>
        private static double DarkShare(Bitmap frame, PixelRect panel, PixelRect screen)
        {
            var size = frame.PixelSize;
            double kx = size.Width / (double)Math.Max(1, screen.Width), ky = size.Height / (double)Math.Max(1, screen.Height);
            var r = new PixelRect((int)((panel.X - screen.X) * kx), (int)((panel.Y - screen.Y) * ky), (int)(panel.Width * kx), (int)(panel.Height * ky))
                .Intersect(new PixelRect(size));
            if (r.Width <= 0 || r.Height <= 0) return -1;
            int stride = size.Width * 4;
            var buf = new byte[stride * size.Height];
            var h = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { frame.CopyPixels(new PixelRect(size), h.AddrOfPinnedObject(), buf.Length, stride); }
            finally { h.Free(); }
            int dark = 0, total = 0;
            for (int y = r.Y; y < r.Bottom; y++)
                for (int x = r.X; x < r.Right; x++)
                {
                    int i = y * stride + x * 4; // BGRA
                    total++;
                    if (Math.Abs(buf[i] - 0x16) < 14 && Math.Abs(buf[i + 1] - 0x18) < 14 && Math.Abs(buf[i + 2] - 0x11) < 14) dark++;
                }
            return total == 0 ? -1 : dark / (double)total;
        }

        private static async Task CloseExtraWindowsAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d) return;
                foreach (var w in d.Windows.ToList())
                    if (w is VideoEditorWindow or VideoControlWindow) w.Close();
            });
            await Task.Delay(1500);
        }
    }
}
