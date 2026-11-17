using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using ScreenShot_One_Avalonia;
using Xunit;

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Тести 3.2.2: програма — за вікном під виділенням; автотеги з тексту для старих записів.</summary>
    public class FeatureTests322
    {
        public FeatureTests322() => Loc.Apply("uk");

        [Fact]
        public void Source_IsWindowUnderSelection_NotActiveOne()
        {
            // Згори вниз: Claude (праворуч), Chrome (ліворуч, на весь екран під ним)
            var windows = new List<WindowLocator.Entry>
            {
                new(new PixelRect(1000, 0, 900, 1000), 1, "Claude", "Claude"),
                new(new PixelRect(0, 0, 1900, 1000), 2, "Figma — Google Chrome", "Google Chrome"),
            };
            Assert.Equal(2, CaptureSource.Pick(windows, new PixelRect(100, 100, 400, 300))!.Pid);  // зона в браузері
            Assert.Equal(1, CaptureSource.Pick(windows, new PixelRect(1200, 100, 300, 300))!.Pid); // зона в Claude
            Assert.Null(CaptureSource.Pick(windows, new PixelRect(2500, 100, 100, 100)));          // поза вікнами
        }

        [Fact]
        public void AutoTags_FromText_TableAndSiteAddress()
        {
            string table = "Було (липень) Стало\nTACHO 6.66 c 6.63 c\nTACHO 12.32 c 12.24 c\nTACHO 16.91 c 12.65 c\nTACHO 15.14 c 11.42 c";
            Assert.True(AutoTagger.LooksLikeTable(table));
            Assert.Equal(new[] { "таблиці" }, AutoTagger.Suggest(null, null, table));
            // Чат із часом повідомлень — не таблиця
            Assert.False(AutoTagger.LooksLikeTable("забув сказати\nзараз оновлюю сервер 17:21\nто цікаво буде 17:22\nчи це дасть щось 17:23"));
            // Адреса сайту в тексті — сервіс, навіть без програми
            Assert.Equal(new[] { "figma" }, AutoTagger.Suggest(null, null, "https://www.figma.com/design/abc\nМакет"));
        }

        [Fact]
        public void OldRecords_IgnoreActiveWindowForAutoTags()
        {
            string dir = Path.Combine(Path.GetTempPath(), "so-322-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "a_flattened.png");
            File.WriteAllBytes(file, new byte[] { 1 });
            string index = Path.Combine(dir, "index.json");
            try
            {
                // Запис 3.1.0–3.2.1: активним був Claude, а знято браузер з помилкою
                string key = OperatingSystem.IsWindows() ? Path.GetFullPath(file).ToLowerInvariant() : Path.GetFullPath(file);
                File.WriteAllText(index, System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, HistoryInfo>
                {
                    [key] = new HistoryInfo { App = "Claude", Title = "Claude", Text = "Unhandled exception", TextDone = true, AutoTags = { "помилка", "claude" }, AutoTagsVersion = 2 },
                }));
                HistoryIndex.UseFile(index);
                HistoryIndex.RefreshAutoTags(file);
                Assert.Equal(new[] { "помилка" }, HistoryIndex.Get(file)!.AutoTags);
                Assert.True(HistoryIndex.Matches("claude", HistoryIndex.Get(file))); // для пошуку програма лишається

                // Нові знімки (джерело перевірене) — програма враховується
                HistoryIndex.SetSource(file, new CaptureSource("Steam", "Бібліотека"));
                Assert.Equal(new[] { "помилка", "steam" }, HistoryIndex.Get(file)!.AutoTags);
            }
            finally
            {
                HistoryIndex.UseFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One", "history_index.json"));
                try { Directory.Delete(dir, true); } catch (IOException) { /* тимчасова папка — не критично */ }
            }
        }
    }
}
