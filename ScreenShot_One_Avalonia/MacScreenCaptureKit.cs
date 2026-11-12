using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Знімок екрана на macOS 14+ через ScreenCaptureKit — допоміжною утилітою capture-helper усередині .app
    /// (див. MacHelpers/capture.swift). CGDisplayCreateImage Apple позначила застарілою з macOS 14.
    ///
    /// Утиліта віддає сирі пікселі BGRA через stdout (без PNG і тимчасових файлів). Координати для неї — ТОЧКИ:
    /// прямокутник Avalonia (пікселі = точки × масштаб екрана) ділимо на масштаб, а через --scale просимо
    /// рівно стільки пікселів, скільки в прямокутнику, тож розмір і DPI знімка ті самі, що й у CoreGraphics.
    ///
    /// Методи нічого не кидають: при будь-якій проблемі повертають null і код, а MacScreenCaptureService
    /// переходить на CGDisplayCreateImage, а далі на screencapture.
    /// </summary>
    public static class MacScreenCaptureKit
    {
        public const string HelperName = "capture-helper";

        /// <summary>Коди виходу capture-helper (як у capture.swift) і кілька власних (від'ємні).</summary>
        public enum HelperExit
        {
            Ok = 0,
            BadArgs = 1,
            CaptureFailed = 2,
            WriteFailed = 3,
            NoPermission = 4,
            Timeout = 5,
            /// <summary>Утиліти немає (запуск через dotnet run, збірка без swiftc).</summary>
            Missing = -1,
            /// <summary>Процес не запустився, упав або віддав некоректні дані.</summary>
            Crashed = -2,
        }

        private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(10);
        private const int HeaderSize = 24; // "SOC1" + ширина + висота + stride (UInt32 LE) + масштаб (Float64 LE)
        private static ReadOnlySpan<byte> Magic => "SOC1"u8;

        private static string? _helperPath;

        /// <summary>ScreenCaptureKit-знімок можливий: macOS 14+ і утиліта лежить поруч із програмою.</summary>
        public static bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(14) && FindHelper() != null;

        private static string? FindHelper()
        {
            if (_helperPath != null) return _helperPath;
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, HelperName),
                Path.Combine(AppContext.BaseDirectory, "..", "MacOS", HelperName),
            };
            _helperPath = candidates.FirstOrDefault(File.Exists);
            return _helperPath;
        }

        /// <summary>Аргументи для знімка прямокутника Avalonia (пікселі) з екрана з масштабом scaling.</summary>
        public static string[] RectArguments(PixelRect rect, double scaling)
        {
            double s = double.IsNaN(scaling) || scaling <= 0 ? 1.0 : scaling;
            static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            return new[]
            {
                "--rect", $"{F(rect.X / s)},{F(rect.Y / s)},{F(rect.Width / s)},{F(rect.Height / s)}",
                "--scale", F(s),
                "--raw",
            };
        }

        /// <summary>Знімок прямокутника в пікселях Avalonia; DPI = 96 × scaling.</summary>
        public static (Bitmap? Bitmap, HelperExit Exit) CaptureRect(PixelRect rect, double scaling)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return (null, HelperExit.BadArgs);
            var result = Run(RectArguments(rect, scaling), scaling);
            if (result.Bitmap != null && result.Bitmap.PixelSize != rect.Size)
                AppLog.Write($"capture-helper: розмір {result.Bitmap.PixelSize} замість {rect.Size}");
            return result;
        }

        /// <summary>Усі дисплеї в одній картинці; DPI — за найбільшим масштабом серед дисплеїв.</summary>
        public static (Bitmap? Bitmap, HelperExit Exit) CaptureAllDisplays()
            => Run(new[] { "--all", "--raw" }, null);

        private static (Bitmap? Bitmap, HelperExit Exit) Run(string[] args, double? dpiScaling)
        {
            string? helper = FindHelper();
            if (helper == null) return (null, HelperExit.Missing);

            var psi = new ProcessStartInfo
            {
                FileName = helper,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc == null) return (null, HelperExit.Crashed);

            // stdout і stderr читаємо одночасно, інакше утиліта заблокується на повному каналі
            using var output = new MemoryStream();
            var copy = proc.StandardOutput.BaseStream.CopyToAsync(output);
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit((int)HelperTimeout.TotalMilliseconds))
            {
                try { proc.Kill(entireProcessTree: true); }
                catch (Exception ex) { AppLog.Warn(ex); }
                AppLog.Write($"capture-helper не відповів за {HelperTimeout.TotalSeconds} с — зупинено");
                return (null, HelperExit.Timeout);
            }
            if (!copy.Wait(TimeSpan.FromSeconds(5)))
            {
                AppLog.Write("capture-helper: не вдалося дочитати вивід");
                return (null, HelperExit.Crashed);
            }

            int code = proc.ExitCode;
            var exit = Enum.IsDefined(typeof(HelperExit), code) && code >= 0 ? (HelperExit)code : HelperExit.Crashed;
            if (exit != HelperExit.Ok)
            {
                string err = stderr.Wait(TimeSpan.FromSeconds(1)) ? stderr.Result.Trim() : "";
                AppLog.Write($"capture-helper завершився з кодом {code} ({exit}): {err}");
                return (null, exit);
            }

            var bitmap = ParseRaw(output.GetBuffer(), (int)output.Length, dpiScaling);
            if (bitmap == null)
            {
                AppLog.Write($"capture-helper: некоректні дані ({output.Length} байт)");
                return (null, HelperExit.Crashed);
            }
            return (bitmap, HelperExit.Ok);
        }

        /// <summary>
        /// Розбирає вивід --raw: заголовок і пікселі BGRA premultiplied. DPI = 96 × dpiScaling
        /// (якщо не задано — масштаб із заголовка). null — якщо дані пошкоджені або неповні.
        /// </summary>
        public static Bitmap? ParseRaw(byte[] data, int length, double? dpiScaling = null)
        {
            if (length < HeaderSize || length > data.Length) return null;
            var span = data.AsSpan(0, length);
            if (!span[..4].SequenceEqual(Magic)) return null;

            uint w = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
            uint h = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
            uint stride = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
            double scale = BinaryPrimitives.ReadDoubleLittleEndian(span[16..]);

            if (w == 0 || h == 0 || w > 32768 || h > 32768 || stride < w * 4) return null;
            if ((long)HeaderSize + (long)stride * h > length) return null;

            double dpi = dpiScaling ?? (double.IsFinite(scale) && scale > 0 ? scale : 1.0);
            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return BitmapPixels.FromBgra(handle.AddrOfPinnedObject() + HeaderSize, (int)w, (int)h, (int)stride, dpi);
            }
            finally
            {
                handle.Free();
            }
        }
    }
}
