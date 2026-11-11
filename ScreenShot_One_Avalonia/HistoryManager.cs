using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;

namespace ScreenShot_One_Avalonia
{
    // --- ВІДНОВЛЕНІ МОДЕЛІ ДАНИХ ---
    public enum ShapeType { Stamp, Rectangle, Arrow, StatusArrow, Pencil, Highlighter, Text, Blur, ImageOverlay }

    public class MyPoint
    {
        public double X { get; set; }
        public double Y { get; set; }
        public MyPoint() { }
        public MyPoint(double x, double y) { X = x; Y = y; }
        public static implicit operator Point(MyPoint mp) => new Point(mp.X, mp.Y);
        public static implicit operator MyPoint(Point p) => new MyPoint(p.X, p.Y);
    }

    public class ShapeData
    {
        public ShapeType Type { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        public List<MyPoint> PathPoints { get; set; } = new List<MyPoint>();
        public string ColorHex { get; set; } = "#000000";
        public int StampNumber { get; set; }
        public string TextContent { get; set; } = "";
        public double FontSize { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
        public string? ImageBase64 { get; set; }
    }
    // -------------------------------

    public static class HistoryManager
    {
        private static readonly string HistoryFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One", "Screenshots");

        static HistoryManager() { if (!Directory.Exists(HistoryFolderPath)) Directory.CreateDirectory(HistoryFolderPath); }

        // Компактний JSON: з WriteIndented файли метаданих (особливо з Base64-вставками) були помітно більші.
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = false };

        /// <summary>
        /// Унікальний ID скріншота. Раніше ID мав точність до секунди (yyyyMMdd_HHmmss),
        /// тож два скріншоти за одну секунду перезаписували один одного в історії.
        /// Формат лишається сумісним зі старими записами (той самий префікс дати й часу).
        /// </summary>
        private static readonly object _idLock = new object();
        private static readonly HashSet<string> _reservedIds = new HashSet<string>();

        private static string GenerateUniqueId()
        {
            lock (_idLock)
            {
                string baseId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
                string id = baseId;
                int suffix = 1;
                // Перевіряємо і файли на диску, і ID, зарезервовані фоновими збереженнями, які ще не записались
                while (_reservedIds.Contains(id) || File.Exists(Path.Combine(HistoryFolderPath, $"{id}_flattened.png")))
                    id = $"{baseId}-{suffix++}";
                _reservedIds.Add(id);
                return id;
            }
        }

        /// <summary>Повертає ID для запису: існуючий (повторне збереження) або новий унікальний.</summary>
        public static string ReserveId(string? existingId) => string.IsNullOrEmpty(existingId) ? GenerateUniqueId() : existingId;

        public static string SaveScreenshot(Bitmap original, Bitmap flattened, List<ShapeData> shapes, string? existingId = null)
        {
            string id = ReserveId(existingId);
            WriteFiles(id, original, flattened, shapes);
            if (string.IsNullOrEmpty(existingId)) EnforceMaxLimit(500);
            return id;
        }

        private static void WriteFiles(string id, Bitmap original, Bitmap flattened, List<ShapeData>? shapes)
        {
            string originalPath = Path.Combine(HistoryFolderPath, $"{id}_original.png");
            string flattenedPath = Path.Combine(HistoryFolderPath, $"{id}_flattened.png");
            string jsonPath = Path.Combine(HistoryFolderPath, $"{id}_meta.json");

            using (var fs = new FileStream(originalPath, FileMode.Create)) { original.Save(fs); }

            if (shapes != null)
            {
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(shapes, JsonOptions));
            }

            // _flattened.png — «ознака» запису для вікна історії, тому пишемо його ОСТАННІМ і атомарно
            // (через тимчасовий файл), щоб історія ніколи не побачила напівзаписаний PNG без метаданих.
            string tempPath = flattenedPath + ".tmp";
            using (var fs = new FileStream(tempPath, FileMode.Create)) { flattened.Save(fs); }
            File.Move(tempPath, flattenedPath, overwrite: true);

            lock (_idLock) { _reservedIds.Remove(id); }
        }

        // ---- Фонове збереження ----
        // Кодування двох PNG для великого екрана займає сотні мілісекунд; раніше це блокувало редактор
        // на кожному «Копіювати»/«Зберегти». Тепер записи йдуть у фоні строго по черзі (по одному).

        private static readonly object _queueLock = new object();
        private static Task _queueTail = Task.CompletedTask;

        /// <summary>
        /// Ставить збереження в чергу. Бітмапи мають бути «незмінними» (знімки), бо пишуться з фонового потоку;
        /// disposeAfter — бітмапи, які черга звільнить після запису (тимчасові копії).
        /// </summary>
        public static Task SaveScreenshotInBackground(string id, bool isNew, Bitmap original, Bitmap flattened,
            List<ShapeData> shapes, params IDisposable[] disposeAfter)
        {
            lock (_queueLock)
            {
                _queueTail = _queueTail.ContinueWith(_ =>
                {
                    try
                    {
                        WriteFiles(id, original, flattened, shapes);
                        if (isNew) EnforceMaxLimit(500);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Error(ex, $"Фонове збереження {id}");
                        lock (_idLock) { _reservedIds.Remove(id); }
                        throw;
                    }
                    finally
                    {
                        foreach (var d in disposeAfter)
                        {
                            try { d.Dispose(); } catch (Exception ex) { AppLog.Warn(ex); }
                        }
                    }
                }, TaskScheduler.Default);
                return _queueTail;
            }
        }

        /// <summary>Чекає, поки допишуться всі фонові збереження (напр. перед виходом із програми).</summary>
        public static bool WaitForPendingSaves(TimeSpan timeout)
        {
            Task tail;
            lock (_queueLock) { tail = _queueTail; }
            try { return tail.Wait(timeout); }
            catch (Exception ex) { AppLog.Warn(ex); return true; }
        }

        private static void EnforceMaxLimit(int maxCount)
        {
            var files = Directory.GetFiles(HistoryFolderPath, "*_flattened.png").OrderBy(File.GetCreationTime).ToArray();
            if (files.Length > maxCount)
                for (int i = 0; i < files.Length - maxCount; i++) DeleteScreenshot(files[i]);
        }

        private static void DeleteScreenshot(string flattenedPath)
        {
            try
            {
                string? dir = Path.GetDirectoryName(flattenedPath);
                if (dir == null) return;
                string id = Path.GetFileNameWithoutExtension(flattenedPath).Replace("_flattened", "");

                if (File.Exists(flattenedPath)) File.Delete(flattenedPath);
                if (File.Exists(Path.Combine(dir, $"{id}_original.png"))) File.Delete(Path.Combine(dir, $"{id}_original.png"));
                if (File.Exists(Path.Combine(dir, $"{id}_meta.json"))) File.Delete(Path.Combine(dir, $"{id}_meta.json"));
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }
        }
    }
}
