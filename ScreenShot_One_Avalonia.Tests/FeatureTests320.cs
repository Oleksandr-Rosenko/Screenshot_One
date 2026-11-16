using System;
using System.IO;
using ScreenShot_One_Avalonia;
using Xunit;

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Тести 3.2.0: автотеги — «що» (з тексту) + «де» (сервіс, інакше тип програми).</summary>
    public class FeatureTests320
    {
        public FeatureTests320() => Loc.Apply("uk");

        [Fact]
        public void AutoTags_CombineContentAndService()
        {
            // Chrome, вкладка Jira, на екрані Exception → «помилка» + «jira» (не «браузер»)
            Assert.Equal(new[] { "помилка", "jira" },
                AutoTagger.Suggest("Google Chrome", "PROJ-42 Login fails - Jira", "NullReferenceException at Login()"));
            // Chrome, YouTube → лише сервіс
            Assert.Equal(new[] { "youtube" }, AutoTagger.Suggest("Google Chrome", "Котики - YouTube", "Підписатися"));
            // Telegram, на екрані рахунок → «рахунок» + «telegram»
            Assert.Equal(new[] { "рахунок", "telegram" }, AutoTagger.Suggest("Telegram", "Бухгалтерія", "Рахунок-фактура №12\nДо сплати 1200 грн"));
            // Звичайна стаття в Safari → лише тип програми
            Assert.Equal(new[] { "браузер" }, AutoTagger.Suggest("Safari", "Як варити каву", "Кава — напій"));
            // Figma → сервіс
            Assert.Equal(new[] { "figma" }, AutoTagger.Suggest("Figma", "Макет лендингу", null));
        }

        [Fact]
        public void AutoTags_CodeEditorWithCode_IsSingleTag()
        {
            string code = "using System;\nnamespace Demo {\n  public class A {\n    public int X => 1;\n    return;\n  }\n}";
            // «код» і з тексту, і з програми — без дубля
            Assert.Equal(new[] { "код" }, AutoTagger.Suggest("Visual Studio Code", "A.cs — Demo", code));
            Assert.True(AutoTagger.LooksLikeCode(code));
            Assert.False(AutoTagger.LooksLikeCode("Звичайний текст\nбез коду\nзовсім"));
        }

        [Fact]
        public void AutoTags_NothingKnown_NoTags()
        {
            Assert.Empty(AutoTagger.Suggest(null, null, null));
            Assert.Empty(AutoTagger.Suggest("Невідома програма", "Вікно", "Привіт"));
            // Слово всередині іншого слова не рахується («terror» — не «error», «Wordle» — не Word)
            Assert.Empty(AutoTagger.Suggest("Wordle", "Гра", "terror"));
        }

        [Fact]
        public void AutoTags_RemovedStayRemoved_AndSearchFindsThem()
        {
            string dir = Path.Combine(Path.GetTempPath(), "so-auto-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "a_flattened.png");
            File.WriteAllBytes(file, new byte[] { 1 });
            try
            {
                HistoryIndex.UseFile(Path.Combine(dir, "index.json"));
                HistoryIndex.SetSource(file, new CaptureSource("Google Chrome", "Bug - Jira"));
                HistoryIndex.SetText(file, "Unhandled exception");
                Assert.Equal(new[] { "помилка", "jira" }, HistoryIndex.Get(file)!.AutoTags);
                Assert.True(HistoryIndex.Matches("#jira", HistoryIndex.Get(file)));

                HistoryIndex.RemoveAutoTag(file, "jira");
                HistoryIndex.SetText(file, "Unhandled exception again"); // перерахунок — «jira» не повертається
                Assert.Equal(new[] { "помилка" }, HistoryIndex.Get(file)!.AutoTags);

                // Власний тег, що збігся з автотегом, — лише один раз
                HistoryIndex.SetTags(file, new[] { "помилка" });
                Assert.Empty(HistoryIndex.Get(file)!.AutoTags);
            }
            finally
            {
                HistoryIndex.UseFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One", "history_index.json"));
                try { Directory.Delete(dir, true); } catch (IOException) { /* тимчасова папка — не критично */ }
            }
        }
    }
}
