using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Локалізація (2.8.0): українська, англійська, російська.
    /// Рядки — у таблиці <see cref="Strings"/> (ключ → uk / en / ru). У XAML: <c>{DynamicResource Ключ}</c>
    /// (відкриті вікна міняють мову одразу), у коді: <c>Loc.T("Ключ")</c> / <c>Loc.F("Ключ", аргументи)</c>.
    /// Налаштування <c>AppSettings.Language</c>: "auto" (мова ОС), "uk", "en", "ru".
    /// </summary>
    public static class Loc
    {
        public static readonly string[] Languages = { "uk", "en", "ru" };

        /// <summary>Поточна мова інтерфейсу (до <see cref="Apply"/> — українська).</summary>
        public static string Current { get; private set; } = "uk";

        /// <summary>Мова змінилась — рядки, задані з коду (трей, підказки), треба оновити.</summary>
        public static event Action? LanguageChanged;

        private static ResourceDictionary? _dictionary;

        /// <summary>Ставить словник поточної мови без визначення мови ОС (старт програми, тести).</summary>
        public static void InstallCurrent() => InstallDictionary(Current);

        /// <summary>"auto" → мова ОС: українська → uk, російська → ru, усе інше → en.</summary>
        public static string Resolve(string? setting)
        {
            if (!string.IsNullOrEmpty(setting) && Array.IndexOf(Languages, setting) >= 0) return setting;
            return FromCulture(SystemLanguage());
        }

        public static string FromCulture(string? twoLetter) => twoLetter?.ToLowerInvariant() switch
        {
            "uk" => "uk",
            "ru" => "ru",
            _ => "en"
        };

        /// <summary>Застосовує мову: рядки XAML через словник ресурсів програми + подія для коду.</summary>
        public static void Apply(string? setting)
        {
            string lang = Resolve(setting);
            bool changed = lang != Current;
            Current = lang;

            InstallDictionary(lang);

            try
            {
                var culture = CultureInfo.GetCultureInfo(lang);
                CultureInfo.DefaultThreadCurrentUICulture = culture;
                CultureInfo.CurrentUICulture = culture;
            }
            catch (Exception ex) { AppLog.Warn(ex); }

            if (changed) LanguageChanged?.Invoke();
        }

        private static void InstallDictionary(string lang)
        {
            var app = Application.Current;
            if (app == null) return;
            var dict = new ResourceDictionary();
            int index = Math.Max(0, Array.IndexOf(Languages, lang));
            foreach (var (key, values) in Strings.All) dict[key] = values[index];
            if (_dictionary != null) app.Resources.MergedDictionaries.Remove(_dictionary);
            app.Resources.MergedDictionaries.Add(dict);
            _dictionary = dict;
        }

        /// <summary>Тільки для тестів: мова без Application.</summary>
        public static void SetForTests(string lang) => Current = Array.IndexOf(Languages, lang) >= 0 ? lang : "uk";

        public static string T(string key)
        {
            if (Strings.All.TryGetValue(key, out var values))
            {
                int index = Array.IndexOf(Languages, Current);
                return values[index < 0 ? 0 : index];
            }
            AppLog.Write($"[Loc] Немає рядка «{key}»");
            return key;
        }

        public static string F(string key, params object?[] args)
        {
            try { return string.Format(T(key), args); }
            catch (FormatException ex) { AppLog.Warn(ex); return T(key); }
        }

        /// <summary>Мова інтерфейсу ОС (дволітерний код).</summary>
        private static string SystemLanguage()
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                {
                    // Програма з Finder часто стартує без LANG, тож CultureInfo дає Invariant — питаємо macOS напряму
                    var mac = MacPreferredLanguage();
                    if (!string.IsNullOrEmpty(mac)) return mac;
                }
                var ui = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
                var cur = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                return cur != "iv" ? cur : ui;
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
                return "en";
            }
        }

        private static string? MacPreferredLanguage()
        {
            try
            {
                var psi = new ProcessStartInfo("/usr/bin/defaults", "read -g AppleLanguages")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                string output = p.StandardOutput.ReadToEnd();
                p.WaitForExit(2000);
                // ( "uk-UA", "en-US" ) → "uk"
                foreach (var raw in output.Split(new[] { '\n', ',', '(', ')' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var item = raw.Trim().Trim('"').Trim();
                    if (item.Length >= 2 && char.IsLetter(item[0])) return item.Substring(0, 2);
                }
            }
            catch (Exception ex) { AppLog.Warn(ex); }
            return null;
        }
    }
}
