using System;
using System.IO;
using System.Linq;
using ScreenShot_One_Avalonia;
using Xunit;

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Тести 3.1.0: пошук і теги в історії.</summary>
    public class FeatureTests310
    {
        private static HistoryInfo Info() => new HistoryInfo
        {
            App = "Google Chrome",
            Title = "Jira — задача 42",
            Text = "Quarterly invoice\nTotal 1042",
            Tags = { "робота", "Звіт" },
        };

        [Fact]
        public void Search_MatchesAnyField_AllWordsRequired()
        {
            var i = Info();
            Assert.True(HistoryIndex.Matches("", i));
            Assert.True(HistoryIndex.Matches("invoice", i));          // текст на знімку
            Assert.True(HistoryIndex.Matches("CHROME", i));           // програма, без урахування регістру
            Assert.True(HistoryIndex.Matches("jira 42", i));          // заголовок вікна, обидва слова
            Assert.True(HistoryIndex.Matches("сьогодні", i, "Сьогодні, 13:04")); // дата з картки
            Assert.False(HistoryIndex.Matches("invoice banana", i));  // кожне слово має знайтись
            Assert.False(HistoryIndex.Matches("banana", null));
        }

        [Fact]
        public void Search_HashMatchesOnlyTags()
        {
            var i = Info();
            Assert.True(HistoryIndex.Matches("#звіт", i));
            Assert.True(HistoryIndex.Matches("#роб", i));             // частина тегу
            Assert.False(HistoryIndex.Matches("#chrome", i));         // «#» — лише серед тегів
            Assert.True(HistoryIndex.Matches("#", i));                // порожній тег не фільтрує
        }

        [Fact]
        public void Tags_AreNormalized()
        {
            var tags = HistoryIndex.NormalizeTags(new[] { " #робота ", "Робота", "", "#", "звіт", new string('x', 40) });
            Assert.Equal(3, tags.Count);
            Assert.Equal("робота", tags[0]);
            Assert.Equal("звіт", tags[1]);
            Assert.Equal(32, tags[2].Length);
        }

        [Fact]
        public void Index_PersistsTagsAndText()
        {
            string dir = Path.Combine(Path.GetTempPath(), "so-index-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "a_flattened.png");
            File.WriteAllBytes(file, new byte[] { 1 });
            try
            {
                HistoryIndex.UseFile(Path.Combine(dir, "index.json"));
                HistoryIndex.SetTags(file, new[] { "баги", "баги", "робота" });
                HistoryIndex.SetText(file, "hello world");
                HistoryIndex.SetSource(file, new CaptureSource("Telegram", "Чат"));

                HistoryIndex.UseFile(Path.Combine(dir, "index.json")); // перечитати з диска
                var info = HistoryIndex.Get(file);
                Assert.NotNull(info);
                Assert.Equal(new[] { "баги", "робота" }, info!.Tags);
                Assert.Equal("hello world", info.Text);
                Assert.True(info.TextDone);
                Assert.Equal("Telegram", info.App);
                Assert.Contains(HistoryIndex.AllTags(), t => t.Tag == "баги" && t.Count == 1);

                // Повторне збереження знімка — текст розпізнати заново, теги лишаються
                HistoryIndex.ResetText(file);
                Assert.False(HistoryIndex.Get(file)!.TextDone);
                Assert.Equal(2, HistoryIndex.Get(file)!.Tags.Count);

                // Файл видалено — запис прибирається
                File.Delete(file);
                HistoryIndex.Prune();
                Assert.Null(HistoryIndex.Get(file));
            }
            finally
            {
                HistoryIndex.UseFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One", "history_index.json"));
                try { Directory.Delete(dir, true); } catch (IOException) { /* тимчасова папка — не критично */ }
            }
        }
    }
}
