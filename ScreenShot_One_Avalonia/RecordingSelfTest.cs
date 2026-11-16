using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Самоперевірка запису (CI, SO_SELFTEST_RECORD=1): відкриває панель запису, пише кілька секунд усього екрана,
    /// витягує кадр і перевіряє, чи не потрапила в нього сама панель (на час тесту вона пурпурна).
    /// </summary>
    internal static class RecordingSelfTest
    {
        public static async Task RunAsync(string? shotsDir)
        {
            await RecordOnceAsync(shotsDir, "звичайний", hide: true);
        }

        /// <summary>
        /// Виключення вікон зі скріншотів (SO_SELFTEST_EXCLUDE=1): два пурпурні вікна, одне «приховане» (як мініатюра),
        /// друге звичайне. На знімку екрана першого не має бути, а друге — має (перевірка, що сам тест бачить вікна).
        /// </summary>
        public static async Task RunExclusionAsync()
        {
            try
            {
                Window? hidden = null, visible = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Window Make(int x) => new Window
                    {
                        Width = 220, Height = 140, CanResize = false, ShowInTaskbar = false, Topmost = true,
                        WindowDecorations = WindowDecorations.None, WindowStartupLocation = WindowStartupLocation.Manual,
                        Position = new PixelPoint(x, 140),
                        Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 0, 255)),
                    };
                    hidden = Make(120); visible = Make(460);
                    hidden.Show(); visible.Show();
                });
                await Task.Delay(800);
                await Dispatcher.UIThread.InvokeAsync(() => PlatformHelper.HideWindowFromScreenCapture(hidden!));
                await Task.Delay(800);

                var (rectHidden, rectVisible, screen) = await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PixelRect R(Window w)
                    {
                        var a = w.PointToScreen(new Point(0, 0));
                        var b = w.PointToScreen(new Point(w.Bounds.Width, w.Bounds.Height));
                        return new PixelRect(a.X, a.Y, b.X - a.X, b.Y - a.Y);
                    }
                    return (R(hidden!), R(visible!), hidden!.Screens.Primary);
                });
                // Контрольне вікно на Mac іноді ще домальовується (анімація появи) — кілька спроб, поки його не видно повністю
                double h = -1, v = -1;
                for (int attempt = 0; attempt < 5 && !(v > 0.5); attempt++)
                {
                    if (attempt > 0) await Task.Delay(700);
                    var shot = screen != null ? ScreenCaptureService.Instance.CaptureScreen(screen) : null;
                    if (shot == null) continue;
                    var bounds = screen!.Bounds;
                    h = DarkShare(shot, rectHidden, bounds);
                    v = DarkShare(shot, rectVisible, bounds);
                    shot.Dispose();
                }
                bool ok = h >= 0 && h < 0.05 && v > 0.5;
                AppLog.Write($"[SelfTest] виключення вікон зі знімка: приховане {(h < 0 ? "?" : h.ToString("P0"))}, звичайне {(v < 0 ? "?" : v.ToString("P0"))} — {ok}");
                await Dispatcher.UIThread.InvokeAsync(() => { hidden!.Close(); visible!.Close(); });
            }
            catch (Exception ex) { AppLog.Error(ex, "Самоперевірка виключення вікон"); }
        }

        /// <summary>
        /// Пошук в історії (SO_SELFTEST_HISTORY=1): картинка з текстом → історія → фонове розпізнавання → пошук
        /// за словом із картинки, за програмою й за тегом.
        /// </summary>
        public static async Task RunHistorySearchAsync()
        {
            string? path = null;
            try
            {
                Bitmap? bmp = null;
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    var rtb = new RenderTargetBitmap(new PixelSize(900, 240));
                    using (var dc = rtb.CreateDrawingContext())
                    {
                        dc.FillRectangle(Avalonia.Media.Brushes.White, new Rect(0, 0, 900, 240));
                        var ft = new Avalonia.Media.FormattedText("Quarterly invoice Pineapple 2026", System.Globalization.CultureInfo.InvariantCulture,
                            Avalonia.Media.FlowDirection.LeftToRight, new Avalonia.Media.Typeface("Inter"), 56, Avalonia.Media.Brushes.Black);
                        dc.DrawText(ft, new Point(30, 80));
                    }
                    bmp = BitmapPixels.Crop(rtb, new PixelRect(0, 0, 900, 240));
                    rtb.Dispose();
                });
                string id = HistoryManager.ReserveId(null);
                path = HistoryManager.FlattenedPath(id);
                HistoryIndex.SetSource(path, new CaptureSource("SelfTestApp", "Звіт за квартал"));
                await HistoryManager.SaveScreenshotInBackground(id, true, bmp!, bmp!, new List<ShapeData>(), bmp!);

                for (int i = 0; i < 60 && HistoryIndex.Get(path)?.TextDone != true; i++) await Task.Delay(500);
                var info = HistoryIndex.Get(path);
                HistoryIndex.SetTags(path, new[] { "selftest", "звіт" });
                info = HistoryIndex.Get(path);
                bool byText = HistoryIndex.Matches("pineapple", info);
                bool byApp = HistoryIndex.Matches("selftestapp", info);
                bool byTag = HistoryIndex.Matches("#звіт", info);
                bool notOther = !HistoryIndex.Matches("banana", info);
                bool ok = byText && byApp && byTag && notOther;
                string text = (info?.Text ?? "").Replace('\n', ' ');
                AppLog.Write($"[SelfTest] пошук в історії: розпізнано «{text}»; за текстом {byText}, за програмою {byApp}, за тегом {byTag}, чуже не знаходить {notOther} — {ok}");
            }
            catch (Exception ex) { AppLog.Error(ex, "Самоперевірка пошуку в історії"); }
            finally
            {
                if (path != null) HistoryManager.DeleteScreenshot(path);
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
                    var p = panel!;
                    var root = p.FindControl<Border>("RootBorder")!;
                    // Позначка: на час тесту панель яскраво-пурпурна — у звичайному кадрі такого кольору немає
                    root.Background = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(255, 0, 255));
                    var tl = p.PointToScreen(root.TranslatePoint(new Point(0, 0), p) ?? default);
                    var br = p.PointToScreen(root.TranslatePoint(new Point(root.Bounds.Width, root.Bounds.Height), p) ?? default);
                    var screen = p.Screens.Primary;
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
                // Невідомо (кадр не прочитався) — теж провал перевірки
                string panelVisible = share < 0 ? "?" : (share > 0.2).ToString();
                string backend = OperatingSystem.IsMacOS() ? MacVideoRecorderService.LastBackend : "ScreenRecorderLib";
                AppLog.Write($"[SelfTest] запис ({label}, {backend}): файл {new FileInfo(path).Length / 1024} КБ, кадр {size}, панель {panelRect}, " +
                             $"пурпурних пікселів у місці панелі {(share < 0 ? "?" : share.ToString("P0"))} — панель у відео: {panelVisible}");
                await CloseExtraWindowsAsync();
            }
            catch (Exception ex) { AppLog.Error(ex, "Самоперевірка запису"); }
        }

        /// <summary>Частка пурпурних пікселів (позначка панелі) у місці панелі на кадрі відео.</summary>
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
                    if (buf[i] > 190 && buf[i + 1] < 70 && buf[i + 2] > 190) dark++; // пурпурний (BGRA)
                }
            return total == 0 ? -1 : dark / (double)total;
        }

        private static async Task CloseExtraWindowsAsync()
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime d) return;
                foreach (var w in d.Windows.ToList())
                {
                    if (w is VideoEditorWindow ve) ve.CloseDiscarding();
                    else if (w is VideoControlWindow) w.Close();
                }
            });
            await Task.Delay(1500);
        }
    }
}
