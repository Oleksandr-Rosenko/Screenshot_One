using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ScreenShot_One_Avalonia;
using Xunit;

namespace ScreenShot_One_Avalonia.Tests
{
    /// <summary>Локалізація (2.8.0): повнота перекладів і відповідність ключів у коді й розмітці.</summary>
    public class LocTests
    {
        private static string SourceDir
        {
            get
            {
                var env = Environment.GetEnvironmentVariable("SO_SOURCE_DIR");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ScreenShot_One_Avalonia"));
            }
        }

        [Fact]
        public void EveryString_HasAllThreeLanguages_WithSamePlaceholders()
        {
            foreach (var (key, values) in Strings.All)
            {
                Assert.True(values.Length == 3, $"{key}: потрібно 3 мови");
                foreach (var v in values) Assert.False(string.IsNullOrWhiteSpace(v) && key != "History_DecimalSeparator", $"{key}: порожній переклад");
                var uk = Regex.Matches(values[0], @"\{\d+\}").Select(m => m.Value).OrderBy(x => x).Distinct().ToArray();
                for (int i = 1; i < 3; i++)
                {
                    var other = Regex.Matches(values[i], @"\{\d+\}").Select(m => m.Value).OrderBy(x => x).Distinct().ToArray();
                    Assert.True(uk.SequenceEqual(other), $"{key}: аргументи {{N}} не збігаються між мовами");
                }
            }
            Assert.Equal(12, Strings.All["History_Months"][1].Split(',').Length);
            Assert.Equal(12, Strings.All["History_Months"][2].Split(',').Length);
        }

        [Fact]
        public void EveryKeyUsedInXamlAndCode_Exists()
        {
            var missing = new System.Collections.Generic.List<string>();
            foreach (var file in Directory.GetFiles(SourceDir, "*.axaml"))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"\{DynamicResource ([A-Za-z]+_[A-Za-z0-9]+)\}"))
                    if (!Strings.All.ContainsKey(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            foreach (var file in Directory.GetFiles(SourceDir, "*.cs").Where(f => Path.GetFileName(f) != "Loc.cs"))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"Loc\.[TF]\(""([^""]+)"""))
                    if (!Strings.All.ContainsKey(m.Groups[1].Value)) missing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
            Assert.True(missing.Count == 0, "Немає рядків: " + string.Join(", ", missing));
        }

        [Fact]
        public void SystemLanguage_MapsToSupportedLanguage()
        {
            Assert.Equal("uk", Loc.FromCulture("uk"));
            Assert.Equal("ru", Loc.FromCulture("ru"));
            Assert.Equal("en", Loc.FromCulture("en"));
            Assert.Equal("en", Loc.FromCulture("de"));
            Assert.Equal("en", Loc.FromCulture(null));
            Assert.Equal("ru", Loc.Resolve("ru"));
            Assert.Equal("uk", Loc.Resolve("uk"));
        }

        [Fact]
        public void HistoryDates_AreLocalized()
        {
            var now = new DateTime(2026, 11, 11, 15, 0, 0);
            try
            {
                Loc.SetForTests("en");
                Assert.Equal("Today, 13:04", HistoryItem.FriendlyDate(new DateTime(2026, 11, 11, 13, 4, 0), now));
                Assert.Equal("Mar 3, 09:15", HistoryItem.FriendlyDate(new DateTime(2026, 3, 3, 9, 15, 0), now));
                Assert.Equal("2.4 MB", HistoryItem.FriendlySize((long)(2.4 * 1024 * 1024)));
                Loc.SetForTests("ru");
                Assert.Equal("Вчера, 18:20", HistoryItem.FriendlyDate(new DateTime(2026, 11, 10, 18, 20, 0), now));
                Assert.Equal("31 декабря 2025, 23:59", HistoryItem.FriendlyDate(new DateTime(2025, 12, 31, 23, 59, 0), now));
            }
            finally { Loc.SetForTests("uk"); }
        }
    }
}
