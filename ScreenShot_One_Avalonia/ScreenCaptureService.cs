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
        Bitmap? CaptureRect(PixelRect rect);
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
        public Bitmap? CaptureRect(PixelRect rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            try
            {
                using var bmp = new System.Drawing.Bitmap(rect.Width, rect.Height);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(rect.X, rect.Y, 0, 0, bmp.Size);
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                ms.Position = 0;
                return new Bitmap(ms);
            }
            catch (Exception ex)
            {
                AppLog.Write($"[WindowsScreenCapture] Error: {ex.Message}");
                return null;
            }
        }

        public Bitmap? CaptureScreen(Screen screen)
        {
            return CaptureRect(screen.Bounds);
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
                using var ms = new MemoryStream();
                source.Save(ms);
                ms.Position = 0;
                using var sysBmp = new System.Drawing.Bitmap(ms);

                int x = Math.Clamp(cropRect.X, 0, sysBmp.Width - 1);
                int y = Math.Clamp(cropRect.Y, 0, sysBmp.Height - 1);
                int w = Math.Min(cropRect.Width, sysBmp.Width - x);
                int h = Math.Min(cropRect.Height, sysBmp.Height - y);

                if (w <= 0 || h <= 0) return null;

                using var cropped = sysBmp.Clone(new System.Drawing.Rectangle(x, y, w, h), sysBmp.PixelFormat);
                using var outMs = new MemoryStream();
                cropped.Save(outMs, System.Drawing.Imaging.ImageFormat.Png);
                outMs.Position = 0;
                return new Bitmap(outMs);
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
        public Bitmap? CaptureRect(PixelRect rect) => null;
        public Bitmap? CaptureScreen(Screen screen) => null;
        public Bitmap? CaptureAllScreens() => null;
        public Bitmap? CropBitmap(Bitmap source, PixelRect cropRect) => null;
    }
#endif

    public class MacScreenCaptureService : IScreenCaptureService
    {
        public Bitmap? CaptureRect(PixelRect rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            string tempFile = Path.Combine(Path.GetTempPath(), $"shot_{Guid.NewGuid():N}.png");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/screencapture",
                    Arguments = $"-x -R {rect.X},{rect.Y},{rect.Width},{rect.Height} \"{tempFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);

                if (File.Exists(tempFile))
                {
                    using var fs = File.OpenRead(tempFile);
                    return new Bitmap(fs);
                }
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

        public Bitmap? CaptureScreen(Screen screen)
        {
            return CaptureRect(screen.Bounds);
        }

        public Bitmap? CaptureAllScreens()
        {
            string tempFile = Path.Combine(Path.GetTempPath(), $"shot_all_{Guid.NewGuid():N}.png");
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/screencapture",
                    Arguments = $"-x \"{tempFile}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);

                if (File.Exists(tempFile))
                {
                    using var fs = File.OpenRead(tempFile);
                    return new Bitmap(fs);
                }
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

        public Bitmap? CropBitmap(Bitmap source, PixelRect cropRect)
        {
            if (cropRect.Width <= 0 || cropRect.Height <= 0) return null;
            string tempIn = Path.Combine(Path.GetTempPath(), $"crop_in_{Guid.NewGuid():N}.png");
            string tempOut = Path.Combine(Path.GetTempPath(), $"crop_out_{Guid.NewGuid():N}.png");
            try
            {
                using (var fs = File.Create(tempIn))
                {
                    source.Save(fs);
                }

                // Apple sips: -c <height> <width> --cropOffset <offsetY> <offsetX>
                var psi = new ProcessStartInfo
                {
                    FileName = "/usr/bin/sips",
                    Arguments = $"-c {cropRect.Height} {cropRect.Width} --cropOffset {cropRect.Y} {cropRect.X} \"{tempIn}\" --out \"{tempOut}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(3000);

                if (File.Exists(tempOut))
                {
                    using var outFs = File.OpenRead(tempOut);
                    return new Bitmap(outFs);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"[MacScreenCapture] Crop error: {ex.Message}");
            }
            finally
            {
                try { if (File.Exists(tempIn)) File.Delete(tempIn); } catch (Exception logEx) { AppLog.Warn(logEx); }
                try { if (File.Exists(tempOut)) File.Delete(tempOut); } catch (Exception logEx) { AppLog.Warn(logEx); }
            }
            return null;
        }
    }

    public class LinuxScreenCaptureService : IScreenCaptureService
    {
        public Bitmap? CaptureRect(PixelRect rect) => null;
        public Bitmap? CaptureScreen(Screen screen) => null;
        public Bitmap? CaptureAllScreens() => null;
        public Bitmap? CropBitmap(Bitmap source, PixelRect cropRect) => null;
    }
}
