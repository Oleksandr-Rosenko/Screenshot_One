using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// «Красивий фон і рамка» (2.10.0): фон-градієнт навколо скріншота, відступ, заокруглені кути, тінь.
    /// У редакторі — попередній перегляд (EditorWindow.ApplyFramePreview), при експорті — <see cref="Apply"/>.
    /// Кольори фонів — частина картинки, а не інтерфейсу, тому тут дозволені власні hex.
    /// </summary>
    public static class FrameStyle
    {
        public sealed record Preset(string From, string To);

        /// <summary>Фони: хвоя, м'ята, пісок, сутінки, корал, графіт, світлий.</summary>
        public static readonly Preset[] Presets =
        {
            new("#2A6B5A", "#0F2D25"),
            new("#7FD8B3", "#2E8F6F"),
            new("#EFE3CB", "#C9B48F"),
            new("#3A4A73", "#6B5A8E"),
            new("#F4A08F", "#D9586A"),
            new("#3A4044", "#16191B"),
            new("#F7F6F2", "#E4E6E0"),
        };

        public static IBrush BrushFor(int index)
        {
            var p = Presets[Math.Clamp(index, 0, Presets.Length - 1)];
            return new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Color.Parse(p.From), 0), new GradientStop(Color.Parse(p.To), 1) }
            };
        }

        public static BoxShadows Shadow => BoxShadows.Parse("0 14 36 0 #59000000");

        /// <summary>Розмір результату: картинка + відступи з усіх боків (публічний для тестів).</summary>
        public static PixelSize OutputSize(PixelSize image, int padding) =>
            new(image.Width + 2 * Math.Max(0, padding), image.Height + 2 * Math.Max(0, padding));

        /// <summary>Накладає фон/рамку з налаштувань. Вимкнено — повертає null (викликач лишає оригінал).</summary>
        public static RenderTargetBitmap? Apply(Bitmap source)
        {
            var s = SettingsManager.Current;
            if (!s.FrameEnabled) return null;

            double w = source.Size.Width, h = source.Size.Height;
            int pad = Math.Clamp(s.FramePadding, 0, 400);
            var image = new Image { Source = source, Width = w, Height = h, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
            var clip = new Border { CornerRadius = new CornerRadius(s.FrameRadius), ClipToBounds = true, Child = image };
            var card = new Border
            {
                CornerRadius = new CornerRadius(s.FrameRadius),
                BoxShadow = s.FrameShadow ? Shadow : default,
                Child = clip
            };
            var outer = new Border { Background = BrushFor(s.FramePreset), Padding = new Thickness(pad), Child = card };

            var size = OutputSize(source.PixelSize, pad);
            var logical = new Size(w + 2 * pad, h + 2 * pad);
            outer.Measure(logical);
            outer.Arrange(new Rect(logical));

            var rtb = new RenderTargetBitmap(size, source.Dpi);
            rtb.Render(outer);
            return rtb;
        }
    }
}
