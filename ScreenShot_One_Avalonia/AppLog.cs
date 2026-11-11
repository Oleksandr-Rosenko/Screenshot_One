using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Простий потокобезпечний файловий лог: %LocalAppData%\Screenshot_One\logs\app.log.
    /// Раніше помилки або ковтались порожнім catch { }, або писались через Debug.WriteLine,
    /// який у Release-збірці взагалі вирізається компілятором — тобто в користувача не лишалось жодного сліду.
    /// Файл ротується при досягненні ~1 МБ (зберігається одна попередня копія app.old.log).
    /// </summary>
    public static class AppLog
    {
        private const long MaxLogSize = 1024 * 1024;
        private static readonly object _lock = new object();

        public static string LogDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One", "logs");

        public static string LogFilePath { get; } = Path.Combine(LogDirectory, "app.log");

        /// <summary>Інформаційне повідомлення (заміна Debug.WriteLine, яке не працює в Release).</summary>
        public static void Write(string? message) => WriteLine("INFO", message ?? "");

        /// <summary>Помилка, яку свідомо проігнорували (замість порожнього catch { }).</summary>
        public static void Warn(Exception ex,
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
            => WriteLine("WARN", $"{Path.GetFileName(file)}:{line} {member}: {ex.GetType().Name}: {ex.Message}");

        /// <summary>Серйозна помилка з повним стеком.</summary>
        public static void Error(Exception ex, string? context = null,
            [CallerMemberName] string member = "",
            [CallerFilePath] string file = "",
            [CallerLineNumber] int line = 0)
            => WriteLine("ERROR", $"{Path.GetFileName(file)}:{line} {member}{(context != null ? " (" + context + ")" : "")}: {ex}");

        private static void WriteLine(string level, string message)
        {
            string text = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            Debug.WriteLine(text);
            try
            {
                lock (_lock)
                {
                    Directory.CreateDirectory(LogDirectory);
                    var info = new FileInfo(LogFilePath);
                    if (info.Exists && info.Length > MaxLogSize)
                    {
                        string old = Path.Combine(LogDirectory, "app.old.log");
                        File.Copy(LogFilePath, old, overwrite: true);
                        File.Delete(LogFilePath);
                    }
                    File.AppendAllText(LogFilePath, text + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch
            {
                // Лог не повинен валити програму (диск повний / немає прав) — тут мовчання виправдане.
            }
        }
    }
}
