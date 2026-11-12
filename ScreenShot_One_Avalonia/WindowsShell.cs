using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Windows: ярлики й кеш іконок після оновлення.
    /// Ярлики в «Пуску» й на робочому столі Velopack створив під час першого встановлення з назвою пакета
    /// («ScreenShotOne»), а Windows тримає іконку програми в кеші — після зміни іконки (2.7.1) у пошуку
    /// й меню «Пуск» лишалась стара. Раз на версію перейменовуємо старий ярлик на «Screenshot One»
    /// і просимо оболонку перечитати іконки.
    /// </summary>
    public static class WindowsShell
    {
        private const string OldShortcutName = "ScreenShotOne.lnk";
        private const string NewShortcutName = "Screenshot One.lnk";

        [DllImport("shell32.dll")]
        private static extern void SHChangeNotify(int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
        private const int SHCNE_ASSOCCHANGED = 0x08000000;
        private const uint SHCNF_IDLIST = 0x0000;

        public static void RefreshAfterUpdate()
        {
            if (!OperatingSystem.IsWindows()) return;
            try
            {
                string version = typeof(WindowsShell).Assembly.GetName().Version?.ToString(3) ?? "";
                var settings = SettingsManager.Current;
                if (settings.LastShellRefreshVersion == version) return;

                int renamed = 0;
                foreach (var dir in ShortcutFolders())
                    renamed += RenameShortcut(dir);

                SHChangeNotify(SHCNE_ASSOCCHANGED, SHCNF_IDLIST, IntPtr.Zero, IntPtr.Zero);
                AppLog.Write($"Оновлено ярлики ({renamed}) і кеш іконок для версії {version}");

                settings.LastShellRefreshVersion = version;
                SettingsManager.Save();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
            }
        }

        private static System.Collections.Generic.IEnumerable<string> ShortcutFolders()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (Directory.Exists(desktop)) yield return desktop;

            string programs = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            if (!Directory.Exists(programs)) yield break;
            yield return programs;
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(programs); }
            catch (Exception ex) { AppLog.Warn(ex); yield break; }
            foreach (var d in subdirs) yield return d; // ярлик може лежати в папці видавця
        }

        /// <summary>«ScreenShotOne.lnk» → «Screenshot One.lnk» (якщо новий уже є — старий дубль прибираємо).</summary>
        private static int RenameShortcut(string dir)
        {
            string oldPath = Path.Combine(dir, OldShortcutName);
            if (!File.Exists(oldPath)) return 0;
            try
            {
                string newPath = Path.Combine(dir, NewShortcutName);
                if (File.Exists(newPath)) File.Delete(oldPath);
                else File.Move(oldPath, newPath);
                return 1;
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
                return 0;
            }
        }
    }
}
