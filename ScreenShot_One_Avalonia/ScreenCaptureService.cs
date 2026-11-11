using System;
using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ScreenShot_One_Avalonia
{
    public interface IScreenCaptureService
    {
        /// <param name="scaling">Масштаб екрана (Screen.Scaling) — визначає DPI знімка, щоб редактор і лупа показували його 1:1.</param>
        Bitmap? CaptureRect(PixelRect rect, double scaling = 1.0);
        Bitmap? CaptureScreen(Screen screen);
        Bitmap? CaptureAllScreens();
        Bitmap? CropBitmap(Bitmap source, PixelRect cropRect);
    }

    public static class ScreenCaptureService
    {
        private static IScreenCaptureService? _instance;

        public static IScreenCaptureService Instance
        {
            get
            {
                if (_instance == null)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        _instance = new WindowsScreenCaptureService();
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        _instance = new MacScreenCaptureService();
                    }
                    else
                    {
                        _instance = new LinuxScreenCaptureService();
                    }
                }
                return _instance;
            }
            set => _instance = value;
        }
    }

#if WINDOWS
    public class WindowsScreenCaptureService : IScreenCaptureService
    {
        public Bitmap? CaptureRect(PixelRect rect, double scaling = 1.0)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            try
            {
                using var bmp = new System.Drawing.Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(rect.X, rect.Y, 0, 0, bmp.Size);
                }

                // Пряме копіювання пікселів (Format32bppArgb у пам'яті — це BGRA) замість кодування PNG і декодування назад
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    return BitmapPixels.FromBgra(data.Scan0, bmp.Width, bmp.Height, data.Stride, scaling);
                }
                finally
                {
                    bmp.UnlockBits(data);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"[WindowsScreenCapture] Error: {ex.Message}");
                return null;
            }
        }

        public Bitmap? CaptureScreen(Screen screen)
        {
            return CaptureRect(screen.Bounds, screen.Scaling);
        }

        public Bitmap? CaptureAllScreens()
        {
            var virtualBounds = ScreenService.GetVirtualScreenBounds();
            return CaptureRect(virtualBounds);
        }

        public Bitmap? CropBitmap(Bitmap source, PixelRect cropRect)
        {
            if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;
            try
            {
                return BitmapPixels.Crop(source, cropRect);
            }
            catch (Exception ex)
            {
                AppLog.Write($"[WindowsScreenCapture] Crop error: {ex.Message}");
                return null;
            }
        }
    }
#else
    public class WindowsScreenCaptureService : IScreenCaptureService
    {
        public Bitmap? CaptureRect(PixelRect rect, double scaling = 1.0) => null;
        public Bitmap? CaptureScreen(Screen screen) => null;
        public Bitmap? CaptureAllScreens() => null;
        public Bitmap? CropBitmap(Bitmap source, PixelRect cropRect) => null;
    }
#endif

    /// <summary>
    /// macOS: спершу нативний CoreGraphics (швидко, у пам'яті, коректно для Retina і кількох моніторів),
    /// а якщо він не спрацював — старий перевірений шлях через /usr/sbin/screencapture.
    /// </summary>
    public class MacScreenCaptureService : IScreenCaptureService
    {
        private bool _nativeBroken;

        private Bitmap? TryNative(Func<Bitmap?> capture)
        {
            if (_nativeBroken) return null;
            try
            {
                return capture();
            }
            catch (Exception ex)
            {
                // DllNotFound / EntryPointNotFound тощо — більше не пробуємо в цій сесії
                _nativeBroken = true;
                AppLog.Error(ex, "Нативне захоплення CoreGraphics недоступне, використовуємо screencapture");
                return null;
            }
        }

        public Bitmap? CaptureRect(PixelRect rect, double scaling = 1.0)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            return TryNative(() => MacCoreGraphics.CaptureRect(rect)) ?? CaptureRectViaTool(rect, scaling);
        }

        public Bitmap? CaptureScreen(Screen screen)
        {
            return CaptureRect(screen.Bounds, screen.Scaling);
        }

        public Bitmap? CaptureAllScreens()
        {
            return TryNative(MacCoreGraphics.CaptureAllDisplays) ?? CaptureAllScreensViaTool();
        }

        public Bitmap? CropBitmap(Bitmap source, PixelRect cropRect)
        {
            if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;
            try
            {
                // Раніше: зберегти PNG → запустити sips → прочитати PNG. Тепер — копіювання в пам'яті.
                return BitmapPixels.Crop(source, cropRect);
            }
            catch (Exception ex)
            {
                AppLog.Write($"[MacScreenCapture] Crop error: {ex.Message}");
                return null;
            }
        }

        // ---- Запасний шлях через системну утиліту screencapture ----

        private static Bitmap? LoadWithScaling(string file, double scaling)
        {
            // PNG від screencapture на Retina має 144 dpi (а не 192), тож Size не збігався з екраном.
            // Перекладаємо пікселі в бітмап із DPI = 96 × масштаб екрана.
            using var fs = File.OpenRead(file);
            var decoded = new Bitmap(fs);
            var result = BitmapPixels.WithDpi(decoded, BitmapPixels.DpiForScaling(scaling));
            if (!ReferenceEquals(result, decoded)) decoded.Dispose();
            return result;
        }

        private static Bitmap? RunScreencapture(string arguments, string tempFile, double scaling)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/screencapture",
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);

                if (File.Exists(tempFile)) return LoadWithScaling(tempFile, scaling);
            }
            catch (Exception ex)
            {
                AppLog.Write($"[MacScreenCapture] Error: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch (Exception logEx) { AppLog.Warn(logEx); }
            }
            return null;
        }

        private static Bitmap? CaptureRectViaTool(PixelRect rect, double scaling)
        {
            // screencapture -R приймає ТОЧКИ, а rect — у пікселях Avalonia: ділимо на масштаб
            // (раніше пікселі передавались як є, і на Retina/другому моніторі область зсувалась)
            double s = scaling > 0 ? scaling : 1.0;
            int x = (int)Math.Round(rect.X / s), y = (int)Math.Round(rect.Y / s);
            int w = Math.Max(1, (int)Math.Round(rect.Width / s)), h = Math.Max(1, (int)Math.Round(rect.Height / s));
            string tempFile = Path.Combine(Path.GetTempPath(), $"shot_{Guid.NewGuid():N}.png");
            return RunScreencapture($"-x -R {x},{y},{w},{h} \"{tempFile}\"", tempFile, s);
        }

        private static Bitmap? CaptureAllScreensViaTool()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"shot_all_{Guid.NewGuid():N}.png");
            return RunScreencapture($"-x \"{tempFile}\"", tempFile, 1.0);
        }
    }

    public class LinuxScreenCaptureService : IScreenCaptureService
    {
        public Bitmap? CaptureRect(PixelRect rect, double scaling = 1.0) => null;
        public Bitmap? CaptureScreen(Screen screen) => null;
        public Bitmap? CaptureAllScreens() => null;
        public Bitmap? CropBitmap(Bitmap source, PixelRect cropRect) => null;
    }
}
