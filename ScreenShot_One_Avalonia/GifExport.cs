using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Експорт відео в GIF через ffmpeg (2.9.0): одна команда з palettegen/paletteuse — власна палітра
    /// замість стандартних 256 кольорів, тож GIF чіткий і без «бруду». 15 кадрів/с, ширина до 960 px.
    /// </summary>
    public static class GifExport
    {
        public const int DefaultFps = 15;
        public const int DefaultMaxWidth = 960;

        /// <summary>Шлях до ffmpeg: поруч із програмою (Windows — ffmpeg.exe, Mac — усередині .app), далі Homebrew.</summary>
        public static string? FindFfmpeg()
        {
            if (OperatingSystem.IsMacOS()) return MacVideoRecorderService.FindFfmpeg();
            string local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            return File.Exists("ffmpeg.exe") ? Path.GetFullPath("ffmpeg.exe") : null;
        }

        /// <summary>
        /// ffmpeg на місці? Якщо ні (рідко: найчастіше його видалив чи заблокував антивірус) — пояснюємо, що сталося,
        /// і пропонуємо відкрити сторінку завантаження: перевстановлення повертає всі файли програми.
        /// </summary>
        public static async Task<bool> EnsureAvailableAsync(Avalonia.Controls.Window? owner)
        {
            if (FindFfmpeg() != null) return true;
            AppLog.Write("ffmpeg не знайдено поруч із програмою");
            var box = MsBox.Avalonia.MessageBoxManager.GetMessageBoxStandard(Loc.T("Ffmpeg_MissingTitle"), Loc.T("Ffmpeg_MissingText"),
                MsBox.Avalonia.Enums.ButtonEnum.YesNo, MsBox.Avalonia.Enums.Icon.Warning);
            var answer = owner != null ? await box.ShowWindowDialogAsync(owner) : await box.ShowWindowAsync();
            if (answer == MsBox.Avalonia.Enums.ButtonResult.Yes) OpenDownloadPage();
            return false;
        }

        public const string DownloadPage = "https://github.com/Oleksandr-Rosenko/Screenshot_One/releases/latest";

        private static void OpenDownloadPage()
        {
            try
            {
                if (OperatingSystem.IsMacOS())
                    Process.Start(new ProcessStartInfo { FileName = "/usr/bin/open", ArgumentList = { DownloadPage }, UseShellExecute = false });
                else
                    Process.Start(new ProcessStartInfo { FileName = DownloadPage, UseShellExecute = true });
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        /// <summary>Аргументи ffmpeg (публічний для тестів).</summary>
        public static string[] BuildArguments(string input, string output, int fps = DefaultFps, int maxWidth = DefaultMaxWidth)
        {
            string filter =
                $"fps={fps},scale='min({maxWidth},iw)':-2:flags=lanczos,split[s0][s1];" +
                "[s0]palettegen=max_colors=128:stats_mode=diff[p];" +
                "[s1][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle";
            return new[] { "-y", "-i", input, "-vf", filter, "-loop", "0", output };
        }

        public static async Task ExportAsync(string input, string output)
        {
            string ffmpeg = FindFfmpeg() ?? throw new FileNotFoundException("ffmpeg");
            var psi = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var a in BuildArguments(input, output)) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg не запустився");
            var stderr = p.StandardError.ReadToEndAsync();
            _ = p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            string err = await stderr;
            if (p.ExitCode != 0 || !File.Exists(output))
            {
                string tail = err.Length > 600 ? err[^600..] : err;
                throw new InvalidOperationException($"ffmpeg exit {p.ExitCode}: {tail}");
            }
            AppLog.Write($"[GIF] {Path.GetFileName(output)}: {new FileInfo(output).Length / 1024} КБ");
        }
    }
}
