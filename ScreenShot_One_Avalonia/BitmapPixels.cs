using System;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Робота з пікселями в пам'яті — без перекодування через PNG.
    /// Раніше обрізка зони на Windows проганяла картинку через PNG чотири рази
    /// (Avalonia → PNG → System.Drawing → PNG → Avalonia), а на Mac запускала зовнішню утиліту sips
    /// з двома тимчасовими файлами. Тепер це пряме копіювання пам'яті.
    ///
    /// DPI знімка = 96 × масштаб екрана. Тоді Bitmap.Size (DIP) збігається з розміром екрана в DIP:
    /// лупа й редактор показують картинку 1:1, а PixelSize лишається повною фізичною роздільністю.
    /// </summary>
    public static class BitmapPixels
    {
        public static readonly Vector Dpi96 = new Vector(96, 96);

        /// <summary>DPI для бітмапа, знятого з екрана з масштабом scaling (1.0 = 100%, 2.0 = Retina).</summary>
        public static Vector DpiForScaling(double scaling)
        {
            if (double.IsNaN(scaling) || scaling <= 0) scaling = 1.0;
            return new Vector(96 * scaling, 96 * scaling);
        }

        /// <summary>Створює бітмап із сирого буфера BGRA (дані копіюються, буфер можна звільняти одразу).</summary>
        public static Bitmap FromBgra(IntPtr data, int width, int height, int stride, double scaling = 1.0, AlphaFormat alpha = AlphaFormat.Premul)
            => new Bitmap(PixelFormat.Bgra8888, alpha, data, new PixelSize(width, height), DpiForScaling(scaling), stride);

        /// <summary>
        /// Масштабує бітмап будь-якого типу. Вбудований Bitmap.CreateScaledBitmap у Avalonia працює лише
        /// з бітмапами, прочитаними з файлу (для RenderTargetBitmap і WriteableBitmap кидає
        /// «Invalid source bitmap type»), тому малюємо через рендер і повертаємо копію у звичайній пам'яті.
        /// </summary>
        public static Bitmap Scale(Bitmap source, PixelSize size, BitmapInterpolationMode mode = BitmapInterpolationMode.HighQuality)
        {
            var w = Math.Max(1, size.Width);
            var h = Math.Max(1, size.Height);
            using var rtb = new RenderTargetBitmap(new PixelSize(w, h), Dpi96);
            using (var ctx = rtb.CreateDrawingContext())
            using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
            {
                ctx.DrawImage(source, new Rect(source.Size), new Rect(0, 0, w, h));
            }
            return Crop(rtb, new PixelRect(0, 0, w, h)) ?? throw new InvalidOperationException("Не вдалося масштабувати зображення");
        }

        /// <summary>
        /// Ті самі пікселі з іншим DPI (копія в пам'яті). Якщо DPI вже такий — повертає джерело без копіювання.
        /// Потрібно, бо PNG, збережений Avalonia, не містить DPI і при читанні завжди має 96:
        /// редактор працює з 96 DPI, щоб щойно знятий скріншот і відкритий з історії виглядали однаково.
        /// </summary>
        public static Bitmap WithDpi(Bitmap source, Vector dpi)
        {
            if (source.Dpi == dpi) return source;
            var size = source.PixelSize;
            var format = source.Format ?? PixelFormat.Bgra8888;
            var alpha = source.AlphaFormat ?? AlphaFormat.Premul;
            var result = new WriteableBitmap(size, dpi, format, alpha);
            using (var fb = result.Lock())
            {
                source.CopyPixels(new PixelRect(0, 0, size.Width, size.Height), fb.Address, fb.RowBytes * size.Height, fb.RowBytes);
            }
            return result;
        }

        /// <summary>
        /// Обрізає бітмап у пам'яті (прямокутник у ПІКСЕЛЯХ джерела, обмежується його межами).
        /// DPI джерела зберігається. Повертає null, якщо площа нульова.
        /// </summary>
        public static Bitmap? Crop(Bitmap source, PixelRect rect)
        {
            var size = source.PixelSize;
            int x = Math.Clamp(rect.X, 0, Math.Max(0, size.Width - 1));
            int y = Math.Clamp(rect.Y, 0, Math.Max(0, size.Height - 1));
            int w = Math.Min(rect.Width, size.Width - x);
            int h = Math.Min(rect.Height, size.Height - y);
            if (w <= 0 || h <= 0) return null;

            var clamped = new PixelRect(x, y, w, h);
            try
            {
                // Швидкий шлях: копіюємо рядки пікселів у тому самому форматі, що й джерело
                var format = source.Format ?? PixelFormat.Bgra8888;
                var alpha = source.AlphaFormat ?? AlphaFormat.Premul;
                var result = new WriteableBitmap(new PixelSize(w, h), source.Dpi, format, alpha);
                using (var fb = result.Lock())
                {
                    source.CopyPixels(clamped, fb.Address, fb.RowBytes * h, fb.RowBytes);
                }
                return result;
            }
            catch (Exception ex)
            {
                // Запасний шлях для нестандартних форматів: малюємо фрагмент через рендер
                AppLog.Warn(ex);
                var rtb = new RenderTargetBitmap(new PixelSize(w, h), source.Dpi);
                using (var ctx = rtb.CreateDrawingContext())
                {
                    // Обидва прямокутники задаються в DIP (Size), а не в пікселях — перераховуємо
                    double sx = source.Size.Width / size.Width;
                    double sy = source.Size.Height / size.Height;
                    ctx.DrawImage(source, new Rect(x * sx, y * sy, w * sx, h * sy), new Rect(0, 0, w * sx, h * sy));
                }
                return rtb;
            }
        }
    }
}
