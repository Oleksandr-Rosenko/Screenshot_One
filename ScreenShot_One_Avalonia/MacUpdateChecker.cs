using System;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Оновлення на macOS (без Velopack і без сертифіката Apple).
    /// Перше встановлення — інсталятор .pkg (він віддає .app користувачу). Далі програма оновлюється сама:
    /// скачує zip своєї архітектури, розпаковує (ditto), після виходу підміняє Contents/ і перезапускається.
    /// Якщо .app недоступний для запису (поставлений вручну від адміністратора) — відкриває інсталятор .pkg.
    /// </summary>
    public static class MacUpdateChecker
    {
        private const string LatestReleaseApi = "https://api.github.com/repos/Oleksandr-Rosenko/Screenshot_One/releases/latest";
        private static readonly HttpClient _http = CreateClient();

        private static HttpClient CreateClient()
        {
            var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Screenshot_One-UpdateCheck");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return http;
        }

        public sealed class ReleaseInfo
        {
            public Version Version = new Version(0, 0);
            public string VersionText = "";
            public string PageUrl = "";
            public string? DownloadUrl;
            public string? PkgUrl;
        }

        /// <summary>Розбирає відповідь GitHub releases/latest. Публічний для тестів.</summary>
        public static ReleaseInfo? ParseRelease(string json, bool arm64)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            string clean = tag.Trim().TrimStart('v', 'V');
            if (!Version.TryParse(clean, out var version)) return null;

            var info = new ReleaseInfo
            {
                Version = version,
                VersionText = clean,
                PageUrl = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : ""
            };

            string suffix = arm64 ? "macOS-arm64.zip" : "macOS-x64.zip";
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    string? url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                        info.DownloadUrl = url;
                    else if (name.EndsWith("macOS.pkg", StringComparison.OrdinalIgnoreCase))
                        info.PkgUrl = url;
                }
            }
            return info;
        }

        public static Version CurrentVersion()
        {
            var v = typeof(MacUpdateChecker).Assembly.GetName().Version ?? new Version(0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }

        public static async Task CheckAsync(bool isManual)
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                string json = await _http.GetStringAsync(LatestReleaseApi, cts.Token);
                var release = ParseRelease(json, RuntimeInformation.OSArchitecture == Architecture.Arm64);
                var current = CurrentVersion();

                if (release == null || release.Version <= current)
                {
                    if (isManual)
                        await ShowAsync(Loc.T("Update_Title"), Loc.F("Update_MacLatest", current.ToString(3)), ButtonEnum.Ok);
                    return;
                }

                if (!isManual &&
                    SettingsManager.Current.LastUpdateDismissedUtc.HasValue &&
                    (DateTime.UtcNow - SettingsManager.Current.LastUpdateDismissedUtc.Value).TotalHours < 24 &&
                    SettingsManager.Current.LastDismissedVersion == release.VersionText)
                {
                    return; // користувач відклав це оновлення менше доби тому
                }

                string? appBundle = CurrentAppBundle();
                bool canSelfUpdate = release.DownloadUrl != null && appBundle != null && IsWritable(appBundle);
                string question = canSelfUpdate || release.PkgUrl != null
                    ? Loc.T("Update_MacInstallNow")
                    : Loc.T("Update_MacOpenRelease");
                var answer = await ShowAsync(Loc.T("Update_MacAvailableTitle"),
                    Loc.F("Update_MacAvailable", release.VersionText, current.ToString(3)) + question,
                    ButtonEnum.YesNo);

                if (answer == ButtonResult.Yes)
                {
                    if (canSelfUpdate) await SelfUpdateAsync(release.DownloadUrl!, appBundle!);
                    else if (release.PkgUrl != null) await DownloadAndOpenPkgAsync(release.PkgUrl);
                    else Open(release.PageUrl);
                }
                else
                {
                    SettingsManager.Current.LastUpdateDismissedUtc = DateTime.UtcNow;
                    SettingsManager.Current.LastDismissedVersion = release.VersionText;
                    try { SettingsManager.Save(); } catch (Exception ex) { AppLog.Warn(ex); }
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
                if (isManual)
                    await ShowAsync(Loc.T("Update_Title"), Loc.F("Update_MacCheckFailed", ex.Message), ButtonEnum.Ok);
            }
        }

        private static readonly HttpClient _download = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };

        /// <summary>Шлях до .app, з якого запущена програма (…/Screenshot_One.app), або null поза бандлом.</summary>
        public static string? CurrentAppBundle()
        {
            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory.TrimEnd('/'));
            // …/Screenshot_One.app/Contents/MacOS
            if (dir.Name != "MacOS" || dir.Parent?.Name != "Contents") return null;
            var app = dir.Parent.Parent;
            return app != null && app.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase) ? app.FullName : null;
        }

        private static bool IsWritable(string appBundle)
        {
            try
            {
                string probe = System.IO.Path.Combine(appBundle, ".update-probe");
                System.IO.File.WriteAllText(probe, "");
                System.IO.File.Delete(probe);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static async Task<string> DownloadAsync(string url, string file)
        {
            using var response = await _download.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var src = await response.Content.ReadAsStreamAsync();
            await using var dst = System.IO.File.Create(file);
            await src.CopyToAsync(dst);
            return file;
        }

        /// <summary>
        /// Тиха заміна програми: zip → тимчасова папка → після виходу підміна Contents/ → перезапуск.
        /// Файли, завантажені самою програмою (не браузером), не отримують «карантину», тож Gatekeeper не втручається.
        /// </summary>
        private static async Task SelfUpdateAsync(string zipUrl, string appBundle)
        {
            ToastWindow? toast = null;
            try
            {
                toast = await Dispatcher.UIThread.InvokeAsync(() => ToastWindow.ShowMessage(Loc.T("Update_MacDownloading")));
                string work = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ScreenshotOne-update-" + Guid.NewGuid().ToString("N"));
                System.IO.Directory.CreateDirectory(work);
                string zip = await DownloadAsync(zipUrl, System.IO.Path.Combine(work, "update.zip"));

                string unpacked = System.IO.Path.Combine(work, "new");
                await RunAsync("/usr/bin/ditto", "-x", "-k", zip, unpacked);
                string newApp = System.IO.Path.Combine(unpacked, "Screenshot_One.app");
                if (!System.IO.File.Exists(System.IO.Path.Combine(newApp, "Contents", "MacOS", "Screenshot_One")))
                    throw new InvalidOperationException("update archive has no Screenshot_One.app");

                string script = System.IO.Path.Combine(work, "install.sh");
                System.IO.File.WriteAllText(script, BuildInstallScript(Environment.ProcessId, appBundle, newApp, work));
                Process.Start(new ProcessStartInfo("/bin/bash") { ArgumentList = { script }, UseShellExecute = false });

                await Dispatcher.UIThread.InvokeAsync(() => toast?.SetMessage(Loc.T("Update_MacRestarting"), 1500));
                await Task.Delay(800);
                await Dispatcher.UIThread.InvokeAsync(App.ShutdownForUpdate);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Mac: самооновлення");
                await Dispatcher.UIThread.InvokeAsync(() => toast?.SetMessage(Loc.F("Update_MacInstallFailed", ex.Message), 6000));
            }
        }

        /// <summary>Скрипт підміни (публічний для тестів). Якщо копіювання не вдалось — повертає стару версію.</summary>
        public static string BuildInstallScript(int pid, string appBundle, string newApp, string workDir)
        {
            static string Q(string v) => "'" + v.Replace("'", "'\\''") + "'";
            return "#!/bin/bash\n" +
                $"PID={pid}; APP={Q(appBundle)}; NEW={Q(newApp)}; WORK={Q(workDir)}\n" +
                "for i in $(seq 1 100); do kill -0 \"$PID\" 2>/dev/null || break; sleep 0.2; done\n" +
                "rm -rf \"$APP/Contents.old\"\n" +
                "if mv \"$APP/Contents\" \"$APP/Contents.old\" && /usr/bin/ditto \"$NEW/Contents\" \"$APP/Contents\"; then\n" +
                "  rm -rf \"$APP/Contents.old\"\n" +
                "else\n" +
                "  rm -rf \"$APP/Contents\"; mv \"$APP/Contents.old\" \"$APP/Contents\"\n" +
                "fi\n" +
                "/usr/bin/xattr -dr com.apple.quarantine \"$APP\" 2>/dev/null\n" +
                "/usr/bin/touch \"$APP\"\n" +
                "/usr/bin/open \"$APP\"\n" +
                "rm -rf \"$WORK\"\n";
        }

        /// <summary>Запасний шлях: скачати інсталятор .pkg і відкрити його (попросить пароль адміністратора).</summary>
        private static async Task DownloadAndOpenPkgAsync(string pkgUrl)
        {
            ToastWindow? toast = null;
            try
            {
                toast = await Dispatcher.UIThread.InvokeAsync(() => ToastWindow.ShowMessage(Loc.T("Update_MacDownloading")));
                string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), System.IO.Path.GetFileName(new Uri(pkgUrl).LocalPath));
                await DownloadAsync(pkgUrl, file);
                await Dispatcher.UIThread.InvokeAsync(() => toast?.SetMessage(Loc.T("Update_MacOpeningInstaller"), 2500));
                Open(file);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Mac: завантаження інсталятора");
                await Dispatcher.UIThread.InvokeAsync(() => toast?.SetMessage(Loc.F("Update_MacInstallFailed", ex.Message), 6000));
            }
        }

        private static async Task RunAsync(string file, params string[] args)
        {
            var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardError = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("cannot start " + file);
            string err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new InvalidOperationException($"{System.IO.Path.GetFileName(file)}: {err}");
        }

        private static Task<ButtonResult> ShowAsync(string title, string text, ButtonEnum buttons)
            => Dispatcher.UIThread.InvokeAsync(() =>
                MessageBoxManager.GetMessageBoxStandard(title, text, buttons, Icon.Info).ShowWindowAsync());

        private static void Open(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try
            {
                Process.Start(new ProcessStartInfo { FileName = "/usr/bin/open", ArgumentList = { url }, UseShellExecute = false });
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }
    }
}
