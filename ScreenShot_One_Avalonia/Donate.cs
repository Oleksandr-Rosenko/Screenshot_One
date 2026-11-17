using System;
using System.Diagnostics;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Підтримати проєкт (3.3.0): кнопка в налаштуваннях і панелі трею відкриває сторінку донату в браузері.
    /// Українською — банка Monobank (гривні, без комісії для донатора), іншими мовами — міжнародний сервіс.
    /// Поки посилань немає, кнопки приховані.
    /// </summary>
    public static class Donate
    {
        /// <summary>Банка Monobank (send.monobank.ua/jar/…).</summary>
        public const string UkrainianUrl = "";
        /// <summary>Міжнародний сервіс (Ko-fi, Buy Me a Coffee тощо).</summary>
        public const string InternationalUrl = "";

        /// <summary>Посилання для поточної мови програми; якщо потрібного немає — будь-яке з двох.</summary>
        public static string? Url => Pick(Loc.Current, UkrainianUrl, InternationalUrl);

        public static bool IsConfigured => !string.IsNullOrEmpty(Url);

        /// <summary>Вибір посилання (публічний для тестів).</summary>
        public static string? Pick(string language, string ukrainian, string international)
        {
            string? preferred = language == "uk" ? ukrainian : international;
            string? other = language == "uk" ? international : ukrainian;
            if (!string.IsNullOrEmpty(preferred)) return preferred;
            return string.IsNullOrEmpty(other) ? null : other;
        }

        public static void Open()
        {
            string? url = Url;
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                AppLog.Write("[Donate] відкриваємо сторінку підтримки");
                if (OperatingSystem.IsMacOS())
                    Process.Start(new ProcessStartInfo { FileName = "/usr/bin/open", ArgumentList = { url }, UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.Error(ex, "Відкриття сторінки підтримки"); }
        }
    }
}
