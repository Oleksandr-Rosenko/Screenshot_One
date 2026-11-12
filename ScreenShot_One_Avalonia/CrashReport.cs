using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Якщо програма впала (необроблене виключення), записуємо logs\last_crash.txt.
    /// При наступному запуску пропонуємо відкрити журнал — так причину збою легко передати розробнику.
    /// </summary>
    public static class CrashReport
    {
        public static string CrashFilePath => Path.Combine(AppLog.LogDirectory, "last_crash.txt");

        public static void Record(Exception ex)
        {
            try
            {
                Directory.CreateDirectory(AppLog.LogDirectory);
                File.WriteAllText(CrashFilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n{ex}");
            }
            catch
            {
                // програма вже падає — тут нічого не вдієш
            }
        }

        /// <summary>Повертає текст попереднього збою (і видаляє позначку) або null, якщо збою не було.</summary>
        public static string? TakePendingReport()
        {
            try
            {
                if (!File.Exists(CrashFilePath)) return null;
                string text = File.ReadAllText(CrashFilePath);
                File.Delete(CrashFilePath);
                return text;
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
                return null;
            }
        }

        public static async Task OfferLogIfCrashedAsync()
        {
            string? report = TakePendingReport();
            if (report == null) return;

            string firstLine = report.Split('\n').Length > 1 ? report.Split('\n')[1] : report;
            if (firstLine.Length > 300) firstLine = firstLine.Substring(0, 300) + "…";

            var box = MessageBoxManager.GetMessageBoxStandard(
                Loc.T("Crash_Title"),
                Loc.F("Crash_Text", firstLine),
                ButtonEnum.YesNo, Icon.Warning);

            if (await box.ShowWindowAsync() == ButtonResult.Yes) OpenLogFolder();
        }

        public static void OpenLogFolder()
        {
            try
            {
                Directory.CreateDirectory(AppLog.LogDirectory);
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { AppLog.LogDirectory }, UseShellExecute = false });
                else if (OperatingSystem.IsMacOS())
                    Process.Start(new ProcessStartInfo { FileName = "/usr/bin/open", ArgumentList = { AppLog.LogDirectory }, UseShellExecute = false });
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }
    }
}
