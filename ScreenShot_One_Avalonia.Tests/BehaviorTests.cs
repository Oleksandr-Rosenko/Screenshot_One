using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using ScreenShot_One_Avalonia;
using Xunit;

// HeadlessAvalonia змінює глобальний стан Avalonia — тести виконуються послідовно
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Один раз ініціалізує Avalonia з headless-платформою і Skia (потрібно для створення бітмапів).</summary>
    /// <summary>
    /// Avalonia в тестах — на ОДНОМУ виділеному UI-потоці (HeadlessUnitTestSession).
    /// Раніше платформа ініціалізувалась у потоці першого тесту, а наступні тести працювали з бітмапами
    /// в інших потоках; залежно від порядку запуску тест-сесія зависала (так зупинився перший деплой 2.6.0).
    /// </summary>
    internal static class HeadlessAvalonia
    {
        public sealed class TestAppBuilder
        {
            public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
        }

        private static readonly Lazy<HeadlessUnitTestSession> _session =
            new Lazy<HeadlessUnitTestSession>(() => HeadlessUnitTestSession.StartNew(typeof(TestAppBuilder)));

        /// <summary>Виконує тіло тесту в UI-потоці Avalonia й чекає завершення (з тайм-аутом, щоб не висіти вічно).</summary>
        public static void Run(Action body)
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
            _session.Value.Dispatch(body, cts.Token).GetAwaiter().GetResult();
        }

        public static Task RunAsync(Func<Task> body)
        {
            Func<Task<bool>> wrapped = async () => { await body(); return true; };
            return _session.Value.Dispatch<bool>(wrapped, System.Threading.CancellationToken.None).WaitAsync(TimeSpan.FromMinutes(2));
        }
    }

    /// <summary>Тести поведінки (а не наявності рядків у коді).</summary>
    public class BehaviorTests
    {
        // Бітмап W×H, де пікселі кодують свої координати: B = x, G = y
        private static Bitmap MakeGradient(int w, int h, double scaling = 1.0)
        {
            int stride = w * 4;
            var buf = new byte[stride * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * stride + x * 4;
                    buf[i] = (byte)x; buf[i + 1] = (byte)y; buf[i + 2] = 7; buf[i + 3] = 255;
                }
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { return BitmapPixels.FromBgra(handle.AddrOfPinnedObject(), w, h, stride, scaling); }
            finally { handle.Free(); }
        }

        private static byte[] ReadPixels(Bitmap bmp)
        {
            int w = bmp.PixelSize.Width, h = bmp.PixelSize.Height;
            var buf = new byte[w * 4 * h];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(0, 0, w, h), handle.AddrOfPinnedObject(), buf.Length, w * 4); }
            finally { handle.Free(); }
            return buf;
        }

        [Fact]
        public void BitmapPixels_Crop_CopiesCorrectRegion_AndKeepsDpi()
        {
            HeadlessAvalonia.Run(() =>
            {
                using var src = MakeGradient(40, 30, scaling: 1.5);
                Assert.Equal(144, src.Dpi.X, 3);

                using var crop = BitmapPixels.Crop(src, new PixelRect(10, 5, 12, 8));
                Assert.NotNull(crop);
                Assert.Equal(new PixelSize(12, 8), crop!.PixelSize);
                Assert.Equal(src.Dpi, crop.Dpi);

                var px = ReadPixels(crop);
                Assert.Equal(10, px[0]);               // перший піксель = (10, 5)
                Assert.Equal(5, px[1]);
                Assert.Equal(21, px[px.Length - 4]);   // останній піксель = (21, 12)
                Assert.Equal(12, px[px.Length - 3]);
            });
        }

        [Fact]
        public void BitmapPixels_Crop_ClampsToBounds_AndRejectsEmpty()
        {
            HeadlessAvalonia.Run(() =>
            {
                using var src = MakeGradient(40, 30);

                using var clamped = BitmapPixels.Crop(src, new PixelRect(35, 25, 20, 20));
                Assert.NotNull(clamped);
                Assert.Equal(new PixelSize(5, 5), clamped!.PixelSize);

                Assert.Null(BitmapPixels.Crop(src, new PixelRect(5, 5, 0, 3)));
            });
        }

        [Fact]
        public void BitmapPixels_WithDpi_ChangesDpiButNotPixels()
        {
            HeadlessAvalonia.Run(() =>
            {
                using var src = MakeGradient(16, 9, scaling: 2.0);
                using var normalized = BitmapPixels.WithDpi(src, BitmapPixels.Dpi96);

                Assert.Equal(BitmapPixels.Dpi96, normalized.Dpi);
                Assert.Equal(src.PixelSize, normalized.PixelSize);
                Assert.Equal(ReadPixels(src), ReadPixels(normalized));
                // Той самий DPI — без зайвої копії
                Assert.Same(normalized, BitmapPixels.WithDpi(normalized, BitmapPixels.Dpi96));
            });
        }

        [Fact]
        public void BitmapPixels_Scale_WorksForRenderTargetAndWriteableBitmaps()
        {
            // Регрес 2.6.0: вбудований CreateScaledBitmap кидав «Invalid source bitmap type» для цих типів
            HeadlessAvalonia.Run(() =>
            {
                using var rtb = new RenderTargetBitmap(new PixelSize(40, 20), BitmapPixels.Dpi96);
                using (var dc = rtb.CreateDrawingContext())
                {
                    dc.FillRectangle(Avalonia.Media.Brushes.Red, new Rect(0, 0, 20, 20));
                    dc.FillRectangle(Avalonia.Media.Brushes.Blue, new Rect(20, 0, 20, 20));
                }
                using var writeable = BitmapPixels.Crop(rtb, new PixelRect(0, 0, 40, 20))!;

                foreach (Bitmap source in new Bitmap[] { rtb, writeable })
                {
                    using var up = BitmapPixels.Scale(source, new PixelSize(80, 40));
                    Assert.Equal(new PixelSize(80, 40), up.PixelSize);

                    using var down = BitmapPixels.Scale(source, new PixelSize(4, 2), Avalonia.Media.Imaging.BitmapInterpolationMode.None);
                    var px = ReadPixels(down);
                    Assert.Equal(255, px[2]);              // лівий піксель червоний
                    Assert.Equal(255, px[3 * 4]);          // правий піксель синій (B)
                }
            });
        }

        [Fact]
        public void HistoryItem_FriendlyDateAndSize_AreHumanReadable()
        {
            Loc.SetForTests("uk");
            var now = new DateTime(2026, 11, 11, 15, 0, 0);
            Assert.Equal("Сьогодні, 13:04", HistoryItem.FriendlyDate(new DateTime(2026, 11, 11, 13, 4, 0), now));
            Assert.Equal("Вчора, 18:20", HistoryItem.FriendlyDate(new DateTime(2026, 11, 10, 18, 20, 0), now));
            Assert.Equal("3 березня, 09:15", HistoryItem.FriendlyDate(new DateTime(2026, 3, 3, 9, 15, 0), now));
            Assert.Equal("31 грудня 2025, 23:59", HistoryItem.FriendlyDate(new DateTime(2025, 12, 31, 23, 59, 0), now));

            Assert.Equal("512 Б", HistoryItem.FriendlySize(512));
            Assert.Equal("245 КБ", HistoryItem.FriendlySize(245 * 1024));
            Assert.Equal("2,4 МБ", HistoryItem.FriendlySize((long)(2.4 * 1024 * 1024)));
        }

        [Fact]
        public void HistoryItem_ReadPngSize_ReadsHeaderWithoutDecoding()
        {
            HeadlessAvalonia.Run(() =>
            {
                string path = Path.Combine(Path.GetTempPath(), $"sso_png_{Guid.NewGuid():N}.png");
                try
                {
                    using (var bmp = MakeGradient(321, 123)) bmp.Save(path);
                    Assert.Equal((321, 123), HistoryItem.ReadPngSize(path));
                    File.WriteAllText(path, "not a png");
                    Assert.Null(HistoryItem.ReadPngSize(path));
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            });
        }

        [Fact]
        public void MacUpdateChecker_ParseRelease_PicksArchiveForArchitecture()
        {
            string json = @"{
              ""tag_name"": ""2.5.5"",
              ""html_url"": ""https://github.com/x/y/releases/tag/2.5.5"",
              ""assets"": [
                { ""name"": ""ScreenShotOne-win-Setup.exe"", ""browser_download_url"": ""https://d/win.exe"" },
                { ""name"": ""Screenshot_One-v2.5.5-macOS-arm64.zip"", ""browser_download_url"": ""https://d/arm.zip"" },
                { ""name"": ""Screenshot_One-v2.5.5-macOS-x64.zip"", ""browser_download_url"": ""https://d/x64.zip"" },
                { ""name"": ""Screenshot_One-v2.5.5-macOS.pkg"", ""browser_download_url"": ""https://d/mac.pkg"" }
              ]}";

            var arm = MacUpdateChecker.ParseRelease(json, arm64: true);
            var x64 = MacUpdateChecker.ParseRelease(json, arm64: false);

            Assert.NotNull(arm);
            Assert.Equal(new Version(2, 5, 5), arm!.Version);
            Assert.Equal("https://d/arm.zip", arm.DownloadUrl);
            Assert.Equal("https://d/x64.zip", x64!.DownloadUrl);
            Assert.Equal("https://d/mac.pkg", arm.PkgUrl);

            // Тег з префіксом v і реліз без Mac-архівів — посилання на сторінку релізу
            var noMac = MacUpdateChecker.ParseRelease(@"{""tag_name"":""v3.0.0"",""html_url"":""https://p"",""assets"":[]}", true);
            Assert.Equal(new Version(3, 0, 0), noMac!.Version);
            Assert.Null(noMac.DownloadUrl);
            Assert.Equal("https://p", noMac.PageUrl);

            Assert.Null(MacUpdateChecker.ParseRelease(@"{""tag_name"":""latest""}", true));
        }

        [Fact]
        public void MacSelfUpdate_InstallScript_SwapsContentsSafely()
        {
            string script = MacUpdateChecker.BuildInstallScript(4242, "/Applications/Screenshot One's.app", "/tmp/w/new/Screenshot_One.app", "/tmp/w");
            Assert.StartsWith("#!/bin/bash\n", script);
            Assert.Contains("PID=4242;", script);
            Assert.Contains("APP='/Applications/Screenshot One'\\''s.app'", script); // лапки в шляху екрановано
            Assert.Contains("mv \"$APP/Contents\" \"$APP/Contents.old\"", script);
            Assert.Contains("/usr/bin/ditto \"$NEW/Contents\" \"$APP/Contents\"", script);
            Assert.Contains("mv \"$APP/Contents.old\" \"$APP/Contents\"", script); // відкат при помилці
            Assert.Contains("/usr/bin/open \"$APP\"", script);
            Assert.DoesNotContain("\r", script);

            // Синтаксис bash (де він є: Linux/macOS, Git Bash на Windows)
            string? bash = new[] { "/bin/bash", @"C:\Program Files\Git\bin\bash.exe" }.FirstOrDefault(File.Exists);
            if (bash != null)
            {
                string tmp = Path.Combine(Path.GetTempPath(), "so-install-" + Guid.NewGuid().ToString("N") + ".sh");
                File.WriteAllText(tmp, script);
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(bash) { ArgumentList = { "-n", tmp }, RedirectStandardError = true, UseShellExecute = false };
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    string err = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    Assert.True(p.ExitCode == 0, "bash -n: " + err);
                }
                finally { File.Delete(tmp); }
            }
        }

        [Fact]
        public void MacLaunchAgent_UsesOpenForAppBundle_AndProducesValidPlist()
        {
            var bundled = SettingsManager.GetMacLaunchArguments("/Applications/Screenshot_One.app/Contents/MacOS/Screenshot_One");
            Assert.Equal(new[] { "/usr/bin/open", "-a", "/Applications/Screenshot_One.app" }, bundled);

            var plain = SettingsManager.GetMacLaunchArguments("/Users/me/bin/Screenshot_One");
            Assert.Equal(new[] { "/Users/me/bin/Screenshot_One" }, plain);

            string plist = SettingsManager.BuildMacLaunchAgentPlist(new[] { "/usr/bin/open", "-a", "/Applications/A & B.app" });
            var doc = XDocument.Parse(plist); // має бути валідний XML (амперсанд екранується)
            var strings = doc.Descendants("string").Select(e => e.Value).ToList();
            Assert.Contains("com.screenshotone.app", strings);
            Assert.Contains("/Applications/A & B.app", strings);
            Assert.Contains(doc.Descendants("key"), k => k.Value == "RunAtLoad");
        }

        // Вивід capture-helper --raw: "SOC1", ширина, висота, stride (UInt32 LE), масштаб (Float64 LE), пікселі BGRA
        private static byte[] MakeRawCapture(int w, int h, int stride, double scale)
        {
            var data = new byte[24 + stride * h];
            "SOC1"u8.CopyTo(data);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), (uint)w);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), (uint)h);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), (uint)stride);
            System.Buffers.Binary.BinaryPrimitives.WriteDoubleLittleEndian(data.AsSpan(16), scale);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = 24 + y * stride + x * 4;
                    data[i] = (byte)x; data[i + 1] = (byte)y; data[i + 2] = 9; data[i + 3] = 255;
                }
            return data;
        }

        [Fact]
        public void MacScreenCaptureKit_ParseRaw_ReadsPixelsStrideAndDpi()
        {
            HeadlessAvalonia.Run(() =>
            {
                // stride більший за ширину × 4 (CoreGraphics вирівнює рядки)
                var data = MakeRawCapture(5, 3, 32, 2.0);
                using var bmp = MacScreenCaptureKit.ParseRaw(data, data.Length);
                Assert.NotNull(bmp);
                Assert.Equal(new PixelSize(5, 3), bmp!.PixelSize);
                Assert.Equal(192, bmp.Dpi.X, 3); // DPI = 96 × масштаб із заголовка

                var px = ReadPixels(bmp);
                Assert.Equal(4, px[px.Length - 4]);   // останній піксель = (4, 2)
                Assert.Equal(2, px[px.Length - 3]);
                Assert.Equal(9, px[2]);

                // Явний масштаб екрана важливіший за масштаб із заголовка
                using var scaled = MacScreenCaptureKit.ParseRaw(data, data.Length, 1.5);
                Assert.Equal(144, scaled!.Dpi.X, 3);
            });
        }

        [Fact]
        public void MacScreenCaptureKit_ParseRaw_RejectsDamagedData()
        {
            HeadlessAvalonia.Run(() =>
            {
                var good = MakeRawCapture(4, 4, 16, 1.0);
                Assert.Null(MacScreenCaptureKit.ParseRaw(good, good.Length - 1)); // неповні пікселі
                Assert.Null(MacScreenCaptureKit.ParseRaw(good, 10));              // неповний заголовок

                var badMagic = (byte[])good.Clone();
                badMagic[0] = (byte)'X';
                Assert.Null(MacScreenCaptureKit.ParseRaw(badMagic, badMagic.Length));

                var badStride = MakeRawCapture(4, 4, 16, 1.0);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(badStride.AsSpan(12), 8u); // stride < ширина × 4
                Assert.Null(MacScreenCaptureKit.ParseRaw(badStride, badStride.Length));

                var empty = MakeRawCapture(0, 4, 16, 1.0);
                Assert.Null(MacScreenCaptureKit.ParseRaw(empty, empty.Length));
            });
        }

        [Fact]
        public void MacScreenCaptureKit_RectArguments_ConvertPixelsToPoints()
        {
            // Retina: другий монітор праворуч, прямокутник Avalonia в пікселях → точки, результат у пікселях екрана
            var args = MacScreenCaptureKit.RectArguments(new PixelRect(2880, 100, 300, 201), 2.0);
            Assert.Equal(new[] { "--rect", "1440,50,150,100.5", "--scale", "2", "--raw" }, args);

            // Неправильний масштаб → 1, від'ємні координати (монітор ліворуч) зберігаються
            var plain = MacScreenCaptureKit.RectArguments(new PixelRect(-1920, 0, 1920, 1080), 0);
            Assert.Equal("-1920,0,1920,1080", plain[1]);
            Assert.Equal("1", plain[3]);
        }

        [Fact]
        public void CrashReport_RecordAndTake_RoundTrips_AndClearsMarker()
        {
            CrashReport.TakePendingReport(); // прибираємо можливий залишок
            CrashReport.Record(new InvalidOperationException("boom-test"));

            string? report = CrashReport.TakePendingReport();
            Assert.NotNull(report);
            Assert.Contains("boom-test", report);
            Assert.False(File.Exists(CrashReport.CrashFilePath));
            Assert.Null(CrashReport.TakePendingReport());
        }

        [Fact]
        public void HistoryManager_ReserveId_IsUniqueForRapidCalls_AndKeepsExisting()
        {
            var ids = Enumerable.Range(0, 50).Select(_ => HistoryManager.ReserveId(null)).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());
            Assert.Equal("20260101_120000_000", HistoryManager.ReserveId("20260101_120000_000"));
        }

        [Fact]
        public void SettingsManager_Migrate_UpgradesOldVideoHotkey_AndIsIdempotent()
        {
            var settings = new AppSettings { VideoKey = 0x52, VideoModifier = 6, SettingsVersion = 0 };

            Assert.True(SettingsManager.Migrate(settings));
            if (OperatingSystem.IsWindows())
                Assert.Equal(0x2C, settings.VideoKey); // старий Ctrl+Shift+R → Ctrl+Shift+PrintScreen
            Assert.True(settings.SettingsVersion > 0);

            Assert.False(SettingsManager.Migrate(settings)); // повторна міграція нічого не змінює
        }
    }
}
