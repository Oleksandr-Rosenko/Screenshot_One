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
    ///   3) тип програми (браузер, таблиці, документ…) — лише якщо сервісу не знайдено;
    ///   4) інакше — коротка назва самої програми (3.2.1: раніше знімки з «невідомих» програм лишались без тегів).
    /// Без інтернету й без «магії» — прості правила, однакові на Windows і Mac.
    /// </summary>
    public static class AutoTagger
    {
        /// <summary>Версія правил: коли вони зміняться, автотеги старих записів перераховуються.</summary>
        public const int RulesVersion = 2;

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
            ("files", new[] { "explorer", "file explorer", "провідник", "проводник", "finder", "total commander" }),
            ("design", new[] { "photoshop", "illustrator", "gimp", "sketch", "canva", "affinity", "paint", "krita" }),
        };

        // Службові «програми», назва яких нічого не каже: робочий стіл, рамка UWP-програм тощо
        private static readonly string[] NoNameApps = { "application frame host", "desktop window manager", "program manager", "windows shell experience host", "dock", "window server", "loginwindow" };
        private static readonly string[] NamePrefixes = { "microsoft ", "google ", "adobe ", "apple " };

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
            "files" => Loc.T("AutoTag_files"),
            "design" => Loc.T("AutoTag_design"),
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

        /// <summary>Коротка назва програми як тег («Telegram Desktop» → «telegram desktop», «Microsoft Paint 3D» → «paint 3d»); null — якщо задовга чи службова.</summary>
        public static string? AppTag(string? app)
        {
            if (string.IsNullOrWhiteSpace(app)) return null;
            string name = Regex.Replace(app.Trim(), @"\s+", " ").ToLowerInvariant();
            if (name.EndsWith(".exe")) name = name[..^4];
            if (NoNameApps.Contains(name) || name.Contains("screenshot one") || name.Contains("screenshot_one")) return null;
            foreach (var prefix in NamePrefixes)
                if (name.StartsWith(prefix) && name.Length > prefix.Length) { name = name[prefix.Length..]; break; }
            return name.Length <= 20 && name.Split(' ').Length <= 2 ? name : null;
        }

        /// <summary>Автотеги для запису: «що» (з тексту) + «де» (сервіс, інакше тип програми); не більше двох.</summary>
        public static List<string> Suggest(string? app, string? title, string? text)
        {
            var result = new List<string>();
            // Робочий стіл Windows («Program Manager» у Провідника) — це не «файли»
            if (string.Equals(title?.Trim(), "Program Manager", StringComparison.OrdinalIgnoreCase)) { app = null; title = null; }
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
                string? name = category != null ? TagName(category) : AppTag(app);
                if (name != null && !result.Contains(name, StringComparer.OrdinalIgnoreCase)) result.Add(name);
            }
            return result.Take(2).ToList();
        }
    }
}
