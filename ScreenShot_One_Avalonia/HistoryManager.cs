using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
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
        private static string GenerateUniqueId()
        {
            string baseId = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
            string id = baseId;
            int suffix = 1;
            while (File.Exists(Path.Combine(HistoryFolderPath, $"{id}_flattened.png")))
                id = $"{baseId}-{suffix++}";
            return id;
        }

        public static string SaveScreenshot(Bitmap original, Bitmap flattened, List<ShapeData> shapes, string? existingId = null)
        {
            string id = string.IsNullOrEmpty(existingId) ? GenerateUniqueId() : existingId;

            string originalPath = Path.Combine(HistoryFolderPath, $"{id}_original.png");
            string flattenedPath = Path.Combine(HistoryFolderPath, $"{id}_flattened.png");
            string jsonPath = Path.Combine(HistoryFolderPath, $"{id}_meta.json");

            using (var fs = new FileStream(originalPath, FileMode.Create)) { original.Save(fs); }
            using (var fs = new FileStream(flattenedPath, FileMode.Create)) { flattened.Save(fs); }

            if (shapes != null)
            {
                File.WriteAllText(jsonPath, JsonSerializer.Serialize(shapes, JsonOptions));
            }

            if (string.IsNullOrEmpty(existingId)) EnforceMaxLimit(500);
            return id;
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
