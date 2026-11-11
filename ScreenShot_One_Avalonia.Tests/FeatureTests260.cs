using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ScreenShot_One_Avalonia;
using Xunit;
using Xunit.Abstractions;

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Тести функцій 2.6.0: OCR, збереження файлів, запис відео на Mac, міграція налаштувань.</summary>
    public class FeatureTests260
    {
        private readonly ITestOutputHelper _output;
        public FeatureTests260(ITestOutputHelper output) => _output = output;

        [Fact]
        public async Task OcrService_RecognizesRenderedText_OnWindows()
        {
            if (!OperatingSystem.IsWindows()) return;
            await HeadlessAvalonia.RunAsync(async () =>
            {

                using var rtb = new RenderTargetBitmap(new PixelSize(900, 200), BitmapPixels.Dpi96);
                using (var dc = rtb.CreateDrawingContext())
                {
                    dc.FillRectangle(Brushes.White, new Rect(0, 0, 900, 200));
                    var text = new FormattedText("Screenshot One 2026", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface("Arial", FontStyle.Normal, FontWeight.Bold), 56, Brushes.Black);
                    dc.DrawText(text, new Point(40, 60));
                }

                string recognized;
                try
                {
                    recognized = await OcrService.RecognizeAsync(rtb);
                }
                catch (OcrUnavailableException ex)
                {
                    // На машині без мовних пакетів OCR перевірити неможливо — це не помилка коду
                    _output.WriteLine("OCR недоступний: " + ex.Message);
                    return;
                }

                _output.WriteLine("Розпізнано: " + recognized);
                Assert.Contains("Screenshot", recognized, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("2026", recognized);
            });
        }

        [Fact]
        public void OcrService_CleanUp_TrimsEdgesAndTrailingSpaces()
        {
            string cleaned = OcrService.CleanUp("\n\n  Перший рядок   \nДругий\t\n\n");
            Assert.Equal("  Перший рядок" + Environment.NewLine + "Другий", cleaned);
            Assert.Equal("", OcrService.CleanUp("\n \n"));
        }

        [Fact]
        public void ImageExport_FileNames_AreUniqueAndFormatAware()
        {
            Assert.Equal("Screenshot_2026-11-11_14-05-32.png", ImageExport.BuildFileName(new DateTime(2026, 11, 11, 14, 5, 32), "png"));
            Assert.Equal("Screenshot_2026-11-11_14-05-32.jpg", ImageExport.BuildFileName(new DateTime(2026, 11, 11, 14, 5, 32), ".jpg"));

            string dir = Path.Combine(Path.GetTempPath(), "sso_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string first = ImageExport.MakeUniquePath(dir, "a.png");
                File.WriteAllText(first, "x");
                string second = ImageExport.MakeUniquePath(dir, "a.png");
                File.WriteAllText(second, "x");
                string third = ImageExport.MakeUniquePath(dir, "a.png");

                Assert.EndsWith("a.png", first);
                Assert.EndsWith("a (2).png", second);
                Assert.EndsWith("a (3).png", third);
            }
            finally { Directory.Delete(dir, true); }

            Assert.True(ImageExport.IsJpeg("x.JPG"));
            Assert.True(ImageExport.IsJpeg("x.jpeg"));
            Assert.False(ImageExport.IsJpeg("x.png"));
        }

        [Fact]
        public void ImageExport_SavesPngAndJpeg()
        {
            HeadlessAvalonia.Run(() =>
            {
                using var rtb = new RenderTargetBitmap(new PixelSize(64, 48), BitmapPixels.Dpi96);
                using (var dc = rtb.CreateDrawingContext())
                    dc.FillRectangle(Brushes.OrangeRed, new Rect(0, 0, 64, 48));

                string dir = Path.Combine(Path.GetTempPath(), "sso_test_" + Guid.NewGuid().ToString("N"));
                try
                {
                    string png = Path.Combine(dir, "t.png");
                    string jpg = Path.Combine(dir, "t.jpg");
                    ImageExport.Save(rtb, png);
                    ImageExport.Save(rtb, jpg);

                    byte[] pngBytes = File.ReadAllBytes(png);
                    byte[] jpgBytes = File.ReadAllBytes(jpg);
                    Assert.Equal(0x89, pngBytes[0]); Assert.Equal((byte)'P', pngBytes[1]); // сигнатура PNG
                    Assert.Equal(0xFF, jpgBytes[0]); Assert.Equal(0xD8, jpgBytes[1]);      // сигнатура JPEG

                    using var reloaded = new Bitmap(jpg);
                    Assert.Equal(new PixelSize(64, 48), reloaded.PixelSize);
                }
                finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
            });
        }

        [Fact]
        public void MacRecorder_BuildArguments_HandlesMicrophoneAndRegion()
        {
            string noMic = MacVideoRecorderService.BuildArguments(1, null, null, null, null, null);
            Assert.Contains("\"Capture screen 1:none\"", noMic);
            Assert.DoesNotContain("-c:a", noMic);
            Assert.Contains("{OUTPUT}", noMic);

            string withMic = MacVideoRecorderService.BuildArguments(0, "default", 10, 20, 211, 121);
            Assert.Contains("\"Capture screen 0:default\"", withMic);
            Assert.Contains("-c:a aac", withMic);
            // Розміри зони вирівнюються до парних (вимога libx264)
            Assert.Contains("crop=200:100:10:20", withMic);
        }

        [Fact]
        public void SettingsManager_MigrateV3_AddsOcrHotkey_WithoutResettingCustomHotkeys()
        {
            var settings = new AppSettings
            {
                SettingsVersion = 2,
                RegionKey = 0x41, RegionModifier = HotkeyModifiers.Ctrl // користувач змінив раніше
            };

            Assert.True(SettingsManager.Migrate(settings));
            Assert.Equal(3, settings.SettingsVersion);
            Assert.Equal(0x41, settings.RegionKey);              // не скинуто
            Assert.Equal(HotkeyModifiers.Ctrl, settings.RegionModifier);
            Assert.NotEqual(0, settings.OcrKey);

            Assert.Equal("Editor", new AppSettings().AfterCapture); // поведінка за замовчуванням не змінилась
            Assert.False(new AppSettings().AutoSaveToFolder);
        }
    }
}
