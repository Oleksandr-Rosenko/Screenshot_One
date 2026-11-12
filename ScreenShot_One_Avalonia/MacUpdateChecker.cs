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
    /// Перевірка оновлень на macOS. Mac-версія ставиться з zip-архіву, а не через Velopack,
    /// тож Velopack на Mac завжди повертав помилку («програма не встановлена»).
    /// Тут — просте порівняння з останнім релізом на GitHub і посилання на потрібний архів (arm64 / x64).
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
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        info.DownloadUrl = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        break;
                    }
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

                bool hasZip = release.DownloadUrl != null;
                var answer = await ShowAsync(Loc.T("Update_MacAvailableTitle"),
                    Loc.F("Update_MacAvailable", release.VersionText, current.ToString(3)) +
                    (hasZip
                        ? Loc.T("Update_MacDownloadZip")
                        : Loc.T("Update_MacOpenRelease")),
                    ButtonEnum.YesNo);

                if (answer == ButtonResult.Yes)
                {
                    Open(release.DownloadUrl ?? release.PageUrl);
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
