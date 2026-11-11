using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Розпізнавання тексту на зображенні системними засобами, без інтернету:
    /// Windows — Windows.Media.Ocr (мова з налаштувань Windows, інакше англійська);
    /// macOS — Vision через допоміжну утиліту ocr-helper усередині .app (див. MacHelpers/ocr.swift).
    /// </summary>
    public static class OcrService
    {
        /// <summary>Повертає розпізнаний текст (рядки через \n) або порожній рядок. Кидає OcrUnavailableException, якщо OCR недоступний.</summary>
        public static Task<string> RecognizeAsync(Bitmap bitmap)
        {
#if WINDOWS
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240))
                return RecognizeWindowsAsync(bitmap);
#endif
            if (OperatingSystem.IsMacOS())
                return RecognizeMacAsync(bitmap);

            throw new OcrUnavailableException("Розпізнавання тексту на цій системі не підтримується.");
        }

        /// <summary>Нормалізує результат: обрізає пробіли в кінці рядків і порожні рядки на краях.</summary>
        public static string CleanUp(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
            while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return string.Join(Environment.NewLine, lines);
        }

        private static byte[] ReadBgra(Bitmap bitmap, out int width, out int height)
        {
            width = bitmap.PixelSize.Width;
            height = bitmap.PixelSize.Height;

            // Гарантуємо формат BGRA: нестандартні формати спершу малюємо в RenderTargetBitmap
            Bitmap source = bitmap;
            RenderTargetBitmap? converted = null;
            if (bitmap.Format != Avalonia.Platform.PixelFormat.Bgra8888)
            {
                converted = new RenderTargetBitmap(bitmap.PixelSize, bitmap.Dpi);
                using (var dc = converted.CreateDrawingContext())
                    dc.DrawImage(bitmap, new Rect(bitmap.Size));
                source = converted;
            }

            var bytes = new byte[width * height * 4];
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                source.CopyPixels(new PixelRect(0, 0, width, height), handle.AddrOfPinnedObject(), bytes.Length, width * 4);
            }
            finally
            {
                handle.Free();
                converted?.Dispose();
            }
            return bytes;
        }

#if WINDOWS
        [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.10240")]
        private static async Task<string> RecognizeWindowsAsync(Bitmap bitmap)
        {
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
                         ?? Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language("en-US"));
            if (engine == null)
                throw new OcrUnavailableException(
                    "У Windows не встановлено жодної мови з підтримкою розпізнавання тексту.\n\n" +
                    "Додайте мову в «Параметри → Час і мова → Мова та регіон» (разом із компонентом «Оптичне розпізнавання символів»).");

            // Дрібний текст розпізнається краще, якщо зображення збільшити
            Bitmap input = bitmap;
            int max = (int)Windows.Media.Ocr.OcrEngine.MaxImageDimension;
            int w = bitmap.PixelSize.Width, h = bitmap.PixelSize.Height;
            bool scaled = false;
            if (w * 2 <= max && h * 2 <= max && (long)w * h < 1_500_000)
            {
                input = BitmapPixels.Scale(bitmap, new PixelSize(w * 2, h * 2), BitmapInterpolationMode.HighQuality);
                scaled = true;
            }
            else if (w > max || h > max)
            {
                double k = Math.Min(max / (double)w, max / (double)h);
                input = BitmapPixels.Scale(bitmap, new PixelSize(Math.Max(1, (int)(w * k)), Math.Max(1, (int)(h * k))), BitmapInterpolationMode.HighQuality);
                scaled = true;
            }

            try
            {
                byte[] pixels = ReadBgra(input, out int pw, out int ph);
                using var soft = new Windows.Graphics.Imaging.SoftwareBitmap(
                    Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, pw, ph, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
                soft.CopyFromBuffer(System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.AsBuffer(pixels));

                var result = await engine.RecognizeAsync(soft);
                return CleanUp(string.Join("\n", result.Lines.Select(l => l.Text)));
            }
            finally
            {
                if (scaled) input.Dispose();
            }
        }
#endif

        private static string? FindMacHelper()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "ocr-helper"),
                Path.Combine(AppContext.BaseDirectory, "..", "MacOS", "ocr-helper"),
            };
            return candidates.FirstOrDefault(File.Exists);
        }

        private static async Task<string> RecognizeMacAsync(Bitmap bitmap)
        {
            string? helper = FindMacHelper();
            if (helper == null)
                throw new OcrUnavailableException("Модуль розпізнавання тексту (ocr-helper) не знайдено в програмі. Встановіть свіжу версію Screenshot One.");

            string tempPath = Path.Combine(Path.GetTempPath(), $"ocr_{Guid.NewGuid():N}.png");
            try
            {
                using (var fs = File.Create(tempPath)) bitmap.Save(fs);

                var psi = new ProcessStartInfo
                {
                    FileName = helper,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };
                psi.ArgumentList.Add(tempPath);

                using var proc = Process.Start(psi) ?? throw new OcrUnavailableException("Не вдалося запустити ocr-helper.");
                var stdout = proc.StandardOutput.ReadToEndAsync();
                var stderr = proc.StandardError.ReadToEndAsync();
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
                await proc.WaitForExitAsync(cts.Token);

                if (proc.ExitCode != 0)
                    throw new InvalidOperationException($"ocr-helper завершився з кодом {proc.ExitCode}: {await stderr}");

                return CleanUp(await stdout);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception ex) { AppLog.Warn(ex); }
            }
        }
    }

    public sealed class OcrUnavailableException : Exception
    {
        public OcrUnavailableException(string message) : base(message) { }
    }
}
