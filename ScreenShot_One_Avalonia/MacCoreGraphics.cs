using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Нативне захоплення екрана на macOS через CoreGraphics — без запуску /usr/sbin/screencapture
    /// і без тимчасових PNG-файлів (раніше кожен знімок коштував сотні мілісекунд).
    ///
    /// Система координат: CGDisplayBounds повертає ТОЧКИ (points) з початком у лівому верхньому куті
    /// головного екрана. Avalonia Screen.Bounds на Mac — це ті самі точки, помножені на масштаб екрана
    /// (пікселі). Тому піксельний прямокутник дисплея = bounds * (ширина знімка / ширина в точках).
    ///
    /// Якщо будь-що тут не спрацює (API недоступне, немає дозволу), методи повертають null,
    /// і MacScreenCaptureService відкочується на старий шлях через screencapture.
    /// </summary>
    public static class MacCoreGraphics
    {
        private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        // kCGImageAlphaPremultipliedFirst | kCGBitmapByteOrder32Little → у пам'яті BGRA premultiplied
        private const uint BitmapInfoBgraPremul = 2u | (2u << 12);

        [StructLayout(LayoutKind.Sequential)]
        public struct CGRect
        {
            public double X, Y, Width, Height;
            public CGRect(double x, double y, double w, double h) { X = x; Y = y; Width = w; Height = h; }
        }

        [DllImport(CG)] private static extern int CGGetActiveDisplayList(uint maxDisplays, [Out] uint[]? activeDisplays, out uint displayCount);
        [DllImport(CG)] private static extern CGRect CGDisplayBounds(uint display);
        [DllImport(CG)] private static extern IntPtr CGDisplayCreateImage(uint display);
        [DllImport(CG)] private static extern nuint CGImageGetWidth(IntPtr image);
        [DllImport(CG)] private static extern nuint CGImageGetHeight(IntPtr image);
        [DllImport(CG)] private static extern IntPtr CGColorSpaceCreateDeviceRGB();
        [DllImport(CG)] private static extern IntPtr CGBitmapContextCreate(IntPtr data, nuint width, nuint height, nuint bitsPerComponent, nuint bytesPerRow, IntPtr colorSpace, uint bitmapInfo);
        [DllImport(CG)] private static extern void CGContextDrawImage(IntPtr context, CGRect rect, IntPtr image);
        [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
        [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestScreenCaptureAccess();
        [DllImport(CF)] private static extern void CFRelease(IntPtr cf);

        /// <summary>Дисплей із його знімком і піксельним прямокутником у координатах Avalonia.</summary>
        private sealed class DisplayShot : IDisposable
        {
            public IntPtr Image;
            public PixelRect PixelBounds;
            public double Scale = 1.0; // пікселі знімка / точки (Retina = 2.0)
            public void Dispose() { if (Image != IntPtr.Zero) { CFRelease(Image); Image = IntPtr.Zero; } }
        }

        /// <summary>true — дозвіл «Запис екрана» вже надано (macOS 10.15+). При помилці вважаємо, що дозвіл є.</summary>
        public static bool HasScreenCaptureAccess()
        {
            try { return CGPreflightScreenCaptureAccess(); }
            catch (Exception ex) { AppLog.Warn(ex); return true; }
        }

        /// <summary>Просить систему додати програму до списку «Запис екрана» (показує системний запит один раз).</summary>
        public static void RequestScreenCaptureAccess()
        {
            try { CGRequestScreenCaptureAccess(); }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        /// <summary>Відкриває «Системні налаштування → Конфіденційність → Запис екрана».</summary>
        public static void OpenScreenCaptureSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = "\"x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture\"",
                    UseShellExecute = false
                });
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        private static uint[] GetDisplays()
        {
            if (CGGetActiveDisplayList(0, null, out uint count) != 0 || count == 0) return Array.Empty<uint>();
            var ids = new uint[count];
            if (CGGetActiveDisplayList(count, ids, out count) != 0) return Array.Empty<uint>();
            if (count < ids.Length) Array.Resize(ref ids, (int)count);
            return ids;
        }

        private static List<DisplayShot> ShootDisplays(Func<PixelRect, bool>? filter)
        {
            var shots = new List<DisplayShot>();
            foreach (var id in GetDisplays())
            {
                var pts = CGDisplayBounds(id);
                if (pts.Width <= 0 || pts.Height <= 0) continue;

                IntPtr image = CGDisplayCreateImage(id);
                if (image == IntPtr.Zero) continue;

                int w = (int)CGImageGetWidth(image);
                int h = (int)CGImageGetHeight(image);
                double scale = w / pts.Width;
                var pixelBounds = new PixelRect(
                    (int)Math.Round(pts.X * scale), (int)Math.Round(pts.Y * scale), w, h);

                if (filter != null && !filter(pixelBounds)) { CFRelease(image); continue; }
                shots.Add(new DisplayShot { Image = image, PixelBounds = pixelBounds, Scale = scale });
            }
            return shots;
        }

        /// <summary>
        /// Малює знімки дисплеїв в один BGRA-буфер розміром target і повертає Avalonia-бітмап.
        /// CoreGraphics має початок координат у ЛІВОМУ НИЖНЬОМУ куті — тому Y перевертаємо.
        /// </summary>
        private static Bitmap? Compose(List<DisplayShot> shots, PixelRect target)
        {
            if (target.Width <= 0 || target.Height <= 0) return null;
            int stride = target.Width * 4;
            // Керований масив уже обнулений (прозорі проміжки між моніторами різного розміру)
            var pixels = new byte[stride * target.Height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            IntPtr buffer = handle.AddrOfPinnedObject();
            IntPtr colorSpace = IntPtr.Zero, ctx = IntPtr.Zero;
            try
            {
                colorSpace = CGColorSpaceCreateDeviceRGB();
                ctx = CGBitmapContextCreate(buffer, (nuint)target.Width, (nuint)target.Height, 8, (nuint)stride, colorSpace, BitmapInfoBgraPremul);
                if (ctx == IntPtr.Zero) return null;

                foreach (var s in shots)
                {
                    double dx = s.PixelBounds.X - target.X;
                    double dyTop = s.PixelBounds.Y - target.Y;
                    double dy = target.Height - dyTop - s.PixelBounds.Height;
                    CGContextDrawImage(ctx, new CGRect(dx, dy, s.PixelBounds.Width, s.PixelBounds.Height), s.Image);
                }
                double scaling = 1.0;
                foreach (var s in shots) scaling = Math.Max(scaling, s.Scale);
                return BitmapPixels.FromBgra(buffer, target.Width, target.Height, stride, scaling);
            }
            finally
            {
                if (ctx != IntPtr.Zero) CFRelease(ctx);
                if (colorSpace != IntPtr.Zero) CFRelease(colorSpace);
                handle.Free();
            }
        }

        /// <summary>Знімок довільного піксельного прямокутника (у координатах Avalonia Screen.Bounds).</summary>
        public static Bitmap? CaptureRect(PixelRect rect)
        {
            if (rect.Width <= 0 || rect.Height <= 0) return null;
            var shots = ShootDisplays(b => b.Intersects(rect));
            try
            {
                if (shots.Count == 0) return null;

                // Найчастіший випадок — рівно один дисплей цілком: віддаємо без зайвої обрізки
                if (shots.Count == 1 && shots[0].PixelBounds == rect)
                    return Compose(shots, rect);

                var union = shots[0].PixelBounds;
                foreach (var s in shots) union = union.Union(s.PixelBounds);
                using var full = Compose(shots, union);
                if (full == null) return null;
                return BitmapPixels.Crop(full, new PixelRect(rect.X - union.X, rect.Y - union.Y, rect.Width, rect.Height));
            }
            finally
            {
                foreach (var s in shots) s.Dispose();
            }
        }

        /// <summary>Знімок усіх дисплеїв в одній картинці (раніше screencapture з одним файлом знімав лише головний екран).</summary>
        public static Bitmap? CaptureAllDisplays()
        {
            var shots = ShootDisplays(null);
            try
            {
                if (shots.Count == 0) return null;
                var union = shots[0].PixelBounds;
                foreach (var s in shots) union = union.Union(s.PixelBounds);
                return Compose(shots, union);
            }
            finally
            {
                foreach (var s in shots) s.Dispose();
            }
        }
    }
}
