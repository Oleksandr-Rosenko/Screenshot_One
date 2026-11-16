using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ScreenShot_One_Avalonia
{
    /// <summary>Дані запису історії для пошуку (3.1.0).</summary>
    public sealed class HistoryInfo
    {
        public List<string> Tags { get; set; } = new();
        /// <summary>Розпізнаний текст на скріншоті (OCR).</summary>
        public string? Text { get; set; }
        /// <summary>true — OCR уже виконано (навіть якщо тексту немає).</summary>
        public bool TextDone { get; set; }
        public string? App { get; set; }
        public string? Title { get; set; }
        /// <summary>Автотеги (3.2.0) — обчислюються з програми, вікна й тексту (AutoTagger).</summary>
        public List<string> AutoTags { get; set; } = new();
        /// <summary>Автотеги, які користувач прибрав, — більше не повертаються.</summary>
        public List<string> RemovedAutoTags { get; set; } = new();
        /// <summary>Версія правил, за якими пораховано AutoTags (0 — ще не рахували).</summary>
        public int AutoTagsVersion { get; set; }

        /// <summary>Усі теги, які бачить користувач: власні + автотеги (якщо автотеги ввімкнені).</summary>
        public IEnumerable<string> AllTags() =>
            HistoryIndex.AutoTagsEnabled ? Tags.Concat(AutoTags.Where(a => !Tags.Contains(a, StringComparer.OrdinalIgnoreCase))) : Tags;
    }

    /// <summary>
    /// Індекс історії для пошуку й тегів (3.1.0): один файл history_index.json у папці даних, ключ — повний шлях до файла.
    /// Окремо від {id}_meta.json (там лише фігури редактора): старі записи й старі версії програми цей файл просто не помічають.
    /// </summary>
    public static class HistoryIndex
    {
        private static readonly object _lock = new();
        private static Dictionary<string, HistoryInfo>? _entries;
        private static string _path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One", "history_index.json");
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        /// <summary>Автотеги ввімкнені (налаштування «Автоматичні теги»; за замовчуванням — так).</summary>
        public static bool AutoTagsEnabled = true;

        /// <summary>Подія: змінились теги/текст/джерело (вікно історії оновлює фільтр і картки).</summary>
        public static event Action<string>? Changed;

        /// <summary>Інший файл індексу (для тестів).</summary>
        public static void UseFile(string path)
        {
            lock (_lock) { _path = path; _entries = null; }
        }

        private static string Key(string path) =>
            OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToLowerInvariant() : Path.GetFullPath(path);

        private static Dictionary<string, HistoryInfo> Entries()
        {
            if (_entries != null) return _entries;
            try
            {
                if (File.Exists(_path))
                    _entries = JsonSerializer.Deserialize<Dictionary<string, HistoryInfo>>(File.ReadAllText(_path), JsonOptions);
            }
            catch (Exception ex) { AppLog.Error(ex, "Читання індексу історії"); }
            _entries ??= new Dictionary<string, HistoryInfo>();
            return _entries;
        }

        private static void SaveLocked()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex) { AppLog.Error(ex, "Запис індексу історії"); }
        }

        public static HistoryInfo? Get(string path)
        {
            lock (_lock) return Entries().TryGetValue(Key(path), out var i) ? i : null;
        }

        private static void Update(string path, Action<HistoryInfo> change)
        {
            lock (_lock)
            {
                var all = Entries();
                string k = Key(path);
                if (!all.TryGetValue(k, out var info)) all[k] = info = new HistoryInfo();
                change(info);
                SaveLocked();
            }
            Changed?.Invoke(path);
        }

        public static void SetSource(string path, CaptureSource source) =>
            Update(path, i => { i.App = source.App; i.Title = source.Title; RecomputeAutoTags(i); });

        public static void SetText(string path, string text) =>
            Update(path, i => { i.Text = string.IsNullOrWhiteSpace(text) ? null : text; i.TextDone = true; RecomputeAutoTags(i); });

        /// <summary>Автотеги з програми, вікна й тексту — без тих, що користувач прибрав або вже має сам.</summary>
        private static void RecomputeAutoTags(HistoryInfo i)
        {
            i.AutoTags = AutoTagger.Suggest(i.App, i.Title, i.Text)
                .Where(t => !i.RemovedAutoTags.Contains(t, StringComparer.OrdinalIgnoreCase) && !i.Tags.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToList();
            i.AutoTagsVersion = AutoTagger.RulesVersion;
        }

        /// <summary>Перерахувати автотеги, якщо їх рахували за старими правилами (або ще не рахували).</summary>
        public static void RefreshAutoTags(string path)
        {
            var info = Get(path);
            if (info == null || info.AutoTagsVersion >= AutoTagger.RulesVersion) return;
            Update(path, RecomputeAutoTags);
        }

        /// <summary>Користувач прибрав автотег — він більше не повертається для цього запису.</summary>
        public static void RemoveAutoTag(string path, string tag) =>
            Update(path, i =>
            {
                i.AutoTags.RemoveAll(t => string.Equals(t, tag, StringComparison.OrdinalIgnoreCase));
                if (!i.RemovedAutoTags.Contains(tag, StringComparer.OrdinalIgnoreCase)) i.RemovedAutoTags.Add(tag);
            });

        /// <summary>Знімок змінено (нові підписи) — текст розпізнати заново.</summary>
        public static void ResetText(string path) =>
            Update(path, i => { i.Text = null; i.TextDone = false; });

        public static IReadOnlyList<string> SetTags(string path, IEnumerable<string> tags)
        {
            var clean = NormalizeTags(tags);
            // Власний тег, що збігся з автотегом, — тепер «свій» (автотег не дублюємо)
            Update(path, i => { i.Tags = clean; i.AutoTags.RemoveAll(a => clean.Contains(a, StringComparer.OrdinalIgnoreCase)); });
            return clean;
        }

        public static void Remove(string path)
        {
            lock (_lock)
            {
                if (Entries().Remove(Key(path))) SaveLocked();
            }
        }

        /// <summary>Прибирає записи про файли, яких уже немає.</summary>
        public static void Prune()
        {
            lock (_lock)
            {
                var all = Entries();
                var gone = all.Keys.Where(k => !File.Exists(k)).ToList();
                if (gone.Count == 0) return;
                foreach (var k in gone) all.Remove(k);
                SaveLocked();
            }
        }

        /// <summary>Усі теги з кількістю записів (за спаданням).</summary>
        public static List<(string Tag, int Count)> AllTags()
        {
            lock (_lock)
            {
                return Entries().Values.SelectMany(i => i.AllTags())
                    .GroupBy(t => t, StringComparer.OrdinalIgnoreCase)
                    .Select(g => (g.First(), g.Count()))
                    .OrderByDescending(x => x.Item2).ThenBy(x => x.Item1, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
        }

        /// <summary>Тег: без «#», пробілів по краях і дублікатів (без урахування регістру), до 32 символів.</summary>
        public static List<string> NormalizeTags(IEnumerable<string> tags)
        {
            var result = new List<string>();
            foreach (var raw in tags)
            {
                string t = raw.Trim().TrimStart('#').Trim();
                if (t.Length == 0) continue;
                if (t.Length > 32) t = t[..32];
                if (!result.Contains(t, StringComparer.OrdinalIgnoreCase)) result.Add(t);
            }
            return result;
        }

        /// <summary>
        /// Чи підходить запис під запит. Кожне слово запиту має знайтись хоч десь: у даті, назві файла, програмі,
        /// заголовку вікна, тексті на знімку або тегах; «#тег» — лише серед тегів. Без урахування регістру.
        /// </summary>
        public static bool Matches(string query, HistoryInfo? info, params string?[] extra)
        {
            if (string.IsNullOrWhiteSpace(query)) return true;
            var tags = info?.AllTags().ToList() ?? new List<string>();
            string haystack = string.Join("\n", extra.Concat(new[] { info?.App, info?.Title, info?.Text }).Concat(tags)
                .Where(s => !string.IsNullOrEmpty(s))).ToLowerInvariant();
            foreach (var word in query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                string w = word.ToLowerInvariant();
                if (w.StartsWith('#'))
                {
                    string tag = w.TrimStart('#');
                    if (tag.Length > 0 && !tags.Any(t => t.Contains(tag, StringComparison.OrdinalIgnoreCase))) return false;
                }
                else if (!haystack.Contains(w)) return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Розпізнавання тексту на скріншотах історії у фоні (3.1.0): нові знімки — одразу після збереження,
    /// старі — поступово після запуску (по одному, з паузами, не під час запису відео).
    /// </summary>
    public static class HistoryTextIndexer
    {
        private static readonly object _lock = new();
        private static readonly LinkedList<string> _queue = new();
        private static bool _running;
        private static bool _unavailable;

        /// <summary>Скільки знімків уже з текстом (для самоперевірки й журналу).</summary>
        public static int Indexed;

        public static void Enqueue(string path, bool urgent = true)
        {
            if (_unavailable) return;
            lock (_lock)
            {
                if (_queue.Contains(path)) return;
                if (urgent) _queue.AddFirst(path); else _queue.AddLast(path);
                if (_running) return;
                _running = true;
            }
            RunAsync().FireAndForget("Розпізнавання тексту в історії");
        }

        /// <summary>Старі знімки без тексту — у кінець черги (через хвилину після запуску програми).</summary>
        public static void StartBackfill(Func<IEnumerable<string>> files)
        {
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(60));
                foreach (var f in files())
                {
                    var info = HistoryIndex.Get(f);
                    if (info?.TextDone != true) Enqueue(f, urgent: false);
                    else HistoryIndex.RefreshAutoTags(f); // автотеги для вже розпізнаних знімків (3.2.0)
                }
            }).FireAndForget("Черга розпізнавання історії");
        }

        private static async Task RunAsync()
        {
            while (true)
            {
                string? path;
                lock (_lock)
                {
                    if (_queue.Count == 0) { _running = false; return; }
                    path = _queue.First!.Value;
                    _queue.RemoveFirst();
                }
                // Під час запису відео не навантажуємо процесор
                while (VideoRecorderManager.IsRecording) await Task.Delay(TimeSpan.FromSeconds(10));
                if (!File.Exists(path)) continue;
                try
                {
                    string text;
                    using (var bmp = new Bitmap(path))
                        text = await OcrService.RecognizeAsync(bmp);
                    HistoryIndex.SetText(path, OcrService.CleanUp(text));
                    Interlocked.Increment(ref Indexed);
                }
                catch (OcrUnavailableException ex)
                {
                    // Немає OCR (наприклад, мовного пакета) — до кінця сесії не пробуємо
                    AppLog.Write("[History] розпізнавання тексту недоступне: " + ex.Message);
                    _unavailable = true;
                    lock (_lock) { _queue.Clear(); _running = false; }
                    return;
                }
                catch (Exception ex) { AppLog.Warn(ex); }
                await Task.Delay(TimeSpan.FromMilliseconds(800)); // поступово, щоб не гріти процесор
            }
        }
    }
}
