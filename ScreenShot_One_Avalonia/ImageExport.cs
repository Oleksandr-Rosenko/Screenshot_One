using System;
using System.IO;
using Avalonia.Media.Imaging;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Збереження скріншотів у файли (PNG / JPEG): «Зберегти як…», автозбереження в папку,
    /// тимчасові файли для перетягування з мініатюри.
    /// </summary>
    public static class ImageExport
    {
        public const int JpegQuality = 92;

        /// <summary>Папка за замовчуванням: «Зображення/Screenshot One».</summary>
        public static string DefaultFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshot One");

        /// <summary>Папка з налаштувань або папка за замовчуванням.</summary>
        public static string SaveFolder
        {
            get
            {
                string? custom = SettingsManager.Current.SaveFolder;
                return string.IsNullOrWhiteSpace(custom) ? DefaultFolder : custom;
            }
        }

        /// <summary>"png" або "jpg" з налаштувань (усе інше трактуємо як png).</summary>
        public static string PreferredExtension =>
            string.Equals(SettingsManager.Current.SaveFormat, "jpg", StringComparison.OrdinalIgnoreCase) ? "jpg" : "png";

        /// <summary>Ім'я файлу: Screenshot_2026-11-11_14-05-32.png</summary>
        public static string BuildFileName(DateTime time, string extension)
            => $"Screenshot_{time:yyyy-MM-dd_HH-mm-ss}.{extension.TrimStart('.')}";

        /// <summary>Шлях, що не перезаписує існуючі файли: додає " (2)", " (3)"…</summary>
        public static string MakeUniquePath(string folder, string fileName)
        {
            string path = Path.Combine(folder, fileName);
            if (!File.Exists(path)) return path;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            for (int i = 2; ; i++)
            {
                path = Path.Combine(folder, $"{name} ({i}){ext}");
                if (!File.Exists(path)) return path;
            }
        }

        public static bool IsJpeg(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Зберігає бітмап у файл; формат — за розширенням (.jpg/.jpeg → JPEG, інакше PNG).</summary>
        public static void Save(Bitmap bitmap, string path)
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            if (IsJpeg(path))
                bitmap.Save(fs, new JpegBitmapEncoderOptions { Quality = JpegQuality });
            else
                bitmap.Save(fs, PngBitmapEncoderOptions.Default);
        }

        /// <summary>Зберігає у папку з налаштувань з автоматичним ім'ям. Повертає повний шлях.</summary>
        public static string SaveToFolder(Bitmap bitmap)
        {
            string folder = SaveFolder;
            Directory.CreateDirectory(folder);
            string path = MakeUniquePath(folder, BuildFileName(DateTime.Now, PreferredExtension));
            Save(bitmap, path);
            return path;
        }

        /// <summary>Тимчасовий PNG (для перетягування в інші програми). Старі тимчасові файли прибираються.</summary>
        public static string SaveTemp(Bitmap bitmap)
        {
            string folder = Path.Combine(Path.GetTempPath(), "Screenshot_One_Drag");
            Directory.CreateDirectory(folder);
            CleanupTemp(folder, TimeSpan.FromHours(1));
            string path = MakeUniquePath(folder, BuildFileName(DateTime.Now, "png"));
            Save(bitmap, path);
            return path;
        }

        private static void CleanupTemp(string folder, TimeSpan maxAge)
        {
            try
            {
                foreach (var f in Directory.GetFiles(folder))
                {
                    if (DateTime.Now - File.GetLastWriteTime(f) > maxAge)
                    {
                        try { File.Delete(f); } catch (Exception ex) { AppLog.Warn(ex); }
                    }
                }
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }
    }
}
