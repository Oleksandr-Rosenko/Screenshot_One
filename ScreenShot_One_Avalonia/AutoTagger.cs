using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Автотеги (3.2.0): з того, що вже відомо про знімок — програма, заголовок вікна й розпізнаний текст —
    /// складаємо 1–2 теги «що + де». Конкретніша ознака перемагає загальну:
    ///   1) що на екрані (з тексту): помилка, рахунок, код;
    ///   2) сервіс чи сайт (із заголовка або назви програми): jira, github, youtube, figma, telegram…;
    ///   3) тип програми (браузер, таблиці, документ…) — лише якщо сервісу не знайдено.
    /// Без інтернету й без «магії» — прості правила, однакові на Windows і Mac.
    /// </summary>
    public static class AutoTagger
    {
        /// <summary>Версія правил: коли вони зміняться, автотеги старих записів перераховуються.</summary>
        public const int RulesVersion = 1;

        // ---- 1. Зміст (текст на знімку) ----
        private static readonly (string Key, Regex Pattern)[] ContentRules =
        {
            ("error", new Regex(@"\b(error|errors|exception|failed|failure|fatal|traceback|stack\s?trace|segmentation fault|panic|not found|access denied)\b|\bпомилк\w*|\bошибк\w*|\bвиняток\b|\bисключени\w*",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
            // Назви винятків у коді: NullReferenceException, TypeError (велика літера після малої; «terror» не підходить)
            ("error", new Regex(@"[a-z](Exception|Error)\b", RegexOptions.CultureInvariant)),
            ("invoice", new Regex(@"\b(invoice|receipt|amount due|total due|bill to|iban)\b|\bрахун(ок|ку|ки)\w*|\bквитанці\w*|\bсч[её]т(-фактур\w*)?\b|до сплати|к оплате",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)),
        };

        private static readonly Regex CodeLine = new(
            @"[{};]\s*$|=>|^\s*(def|class|import|from|using|namespace|public|private|protected|static|function|const|let|var|return|if|elif|else|for|foreach|while|switch|case|try|catch|async|await|#include|package|fn|func)\b|</?[a-zA-Z][\w-]*[ >/]",
            RegexOptions.Multiline | RegexOptions.CultureInvariant);

        // ---- 2. Сервіси (заголовок вікна або назва програми) — тег = назва сервісу ----
        private static readonly (string Tag, string[] Words)[] Services =
        {
            ("jira", new[] { "jira" }),
            ("confluence", new[] { "confluence" }),
            ("github", new[] { "github" }),
            ("gitlab", new[] { "gitlab" }),
            ("youtube", new[] { "youtube" }),
            ("figma", new[] { "figma" }),
            ("gmail", new[] { "gmail" }),
            ("outlook", new[] { "outlook" }),
            ("notion", new[] { "notion" }),
            ("trello", new[] { "trello" }),
            ("slack", new[] { "slack" }),
            ("telegram", new[] { "telegram" }),
            ("discord", new[] { "discord" }),
            ("whatsapp", new[] { "whatsapp" }),
            ("viber", new[] { "viber" }),
            ("teams", new[] { "microsoft teams", "ms teams" }),
            ("zoom", new[] { "zoom" }),
            ("linkedin", new[] { "linkedin" }),
            ("stackoverflow", new[] { "stack overflow", "stackoverflow" }),
        };

        // ---- 3. Тип програми ----
        private static readonly (string Key, string[] Apps)[] Categories =
        {
            ("browser", new[] { "chrome", "safari", "firefox", "edge", "opera", "brave", "arc", "vivaldi", "yandex" }),
            ("code", new[] { "visual studio", "vs code", "code", "rider", "intellij", "pycharm", "webstorm", "android studio", "xcode", "sublime", "cursor", "zed" }),
            ("tables", new[] { "excel", "numbers", "google sheets", "libreoffice calc" }),
            ("document", new[] { "word", "pages", "google docs", "libreoffice writer", "acrobat", "preview", "попередній перегляд" }),
            ("presentation", new[] { "powerpoint", "keynote", "google slides" }),
            ("terminal", new[] { "terminal", "iterm", "powershell", "command prompt", "cmd", "warp" }),
        };

        /// <summary>Назва тегу мовою програми (для змісту й типу програми; сервіси — власні назви).</summary>
        public static string TagName(string key) => key switch
        {
            "error" => Loc.T("AutoTag_error"),
            "invoice" => Loc.T("AutoTag_invoice"),
            "code" => Loc.T("AutoTag_code"),
            "browser" => Loc.T("AutoTag_browser"),
            "tables" => Loc.T("AutoTag_tables"),
            "document" => Loc.T("AutoTag_document"),
            "presentation" => Loc.T("AutoTag_presentation"),
            "terminal" => Loc.T("AutoTag_terminal"),
            _ => key,
        };

        private static bool HasWord(string haystack, string word) =>
            Regex.IsMatch(haystack, @"(?<![\p{L}\p{N}])" + Regex.Escape(word) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>Чи схожий текст на код: щонайменше 4 «кодові» рядки й хоча б третина непорожніх.</summary>
        public static bool LooksLikeCode(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            var lines = text.Split('\n').Where(l => l.Trim().Length > 0).ToList();
            int code = lines.Count(l => CodeLine.IsMatch(l));
            return code >= 4 && code * 3 >= lines.Count;
        }

        /// <summary>Автотеги для запису: «що» (з тексту) + «де» (сервіс, інакше тип програми); не більше двох.</summary>
        public static List<string> Suggest(string? app, string? title, string? text)
        {
            var result = new List<string>();
            string where = $"{title}\n{app}";

            // Що на екрані
            string? content = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                foreach (var (key, pattern) in ContentRules)
                    if (pattern.IsMatch(text)) { content = key; break; }
                if (content == null && LooksLikeCode(text)) content = "code";
            }
            if (content != null) result.Add(TagName(content));

            // Де: спершу конкретний сервіс, потім тип програми
            string? service = Services.FirstOrDefault(s => s.Words.Any(w => HasWord(where, w))).Tag;
            if (service != null) result.Add(service);
            else if (!string.IsNullOrWhiteSpace(app))
            {
                string? category = Categories.FirstOrDefault(c => c.Apps.Any(a => HasWord(app, a))).Key;
                if (category != null)
                {
                    string name = TagName(category);
                    if (!result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
                }
            }
            return result.Take(2).ToList();
        }
    }
}
