using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Material.Icons;
using SukiUI.Controls;
using Velopack;

namespace ScreenShot_One_Avalonia
{
    public enum UpdateWindowState
    {
        Checking,
        UpdateAvailable,
        UpToDate,
        Error
    }

    public partial class UpdateWindow : SukiWindow
    {
        // Кольори з палітри «Хвоя і м'ята» (App.axaml) замість жорстко прописаного синього
        private IBrush Accent => AppTheme.Brush(this, "AccentBrush", Brushes.SeaGreen);
        private IBrush AccentSoft => AppTheme.Brush(this, "ActiveToolBrush", Brushes.Transparent);
        private IBrush Danger => AppTheme.Brush(this, "DangerBrush", Brushes.IndianRed);

        private static UpdateWindow? _activeWindow;

        private UpdateWindowState _state = UpdateWindowState.Checking;
        private UpdateManager? _updateManager;
        private UpdateInfo? _updateInfo;
        private string _currentVersion = "2.8.0";
        private string _newVersion = "2.8.0";
        private bool _isDownloading = false;
        private bool _isReadyToRestart = false;

        public UpdateWindow()
        {
            InitializeComponent();
            _currentVersion = GetCurrentVersionString(null);
            SetCheckingState();
        }

        public UpdateWindow(UpdateManager? updateManager, UpdateInfo? updateInfo, string currentVersion, string newVersion, bool isUpToDate = false)
        {
            InitializeComponent();

            _updateManager = updateManager;
            _updateInfo = updateInfo;
            _currentVersion = currentVersion;
            _newVersion = newVersion;

            if (isUpToDate)
            {
                SetUpToDateState();
            }
            else
            {
                SetUpdateAvailableState();
            }
        }

        public async Task StartCheckAsync()
        {
            SetCheckingState();

            try
            {
                string repoUrl = "https://github.com/Oleksandr-Rosenko/Screenshot_One";
                var source = new Velopack.Sources.GithubSource(repoUrl, accessToken: null /* публічний репозиторій — токен не потрібен; токен у коді вшився б у кожен .exe */, prerelease: false);
                _updateManager = new UpdateManager(source);
                _currentVersion = GetCurrentVersionString(_updateManager);

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                _updateInfo = await _updateManager.CheckForUpdatesAsync().WaitAsync(cts.Token);

                if (_updateInfo == null)
                {
                    SetUpToDateState();
                }
                else
                {
                    _newVersion = _updateInfo.TargetFullRelease.Version.ToString();
                    SetUpdateAvailableState();
                }
            }
            catch (Exception ex)
            {
                SetErrorState(ex);
            }
        }

        private void SetCheckingState()
        {
            _state = UpdateWindowState.Checking;

            TxtTitle.Text = Loc.T("Update_Checking");
            IconHeader.Kind = MaterialIconKind.Radar;
            IconHeader.Foreground = Accent;
            BorderIconHeader.Background = AccentSoft;

            TxtCurrentVersion.Text = _currentVersion.Trim().TrimStart('v', 'V');
            TxtArrow.IsVisible = false;
            BorderNewVersion.IsVisible = false;

            IconCardHeader.Kind = MaterialIconKind.ServerNetwork;
            IconCardHeader.Foreground = Accent;
            TxtCardHeader.Text = Loc.T("Update_SearchHeader");
            TxtChangelog.Text = Loc.T("Update_Connecting");

            PanelProgress.IsVisible = true;
            ProgressBarUpdate.IsIndeterminate = true;
            TxtProgressStatus.Text = Loc.T("Update_CheckingStatus");
            TxtProgressPercent.IsVisible = false;
            TxtStatusMessage.Text = "";

            BtnLater.IsVisible = true;
            BtnLater.Content = Loc.T("Common_Close");
            BtnLater.IsEnabled = true;

            BtnAction.IsVisible = false;
        }

        /// <summary>
        /// Повертає пензель для ресурсу теми, незалежно від того, чи це Color, чи Brush.
        /// </summary>
        private IBrush GetThemeBrush(string resourceKey, IBrush fallback) => AppTheme.Brush(this, resourceKey, fallback);

        private void SetUpdateAvailableState()
        {
            _state = UpdateWindowState.UpdateAvailable;

            TxtTitle.Text = Loc.T("Update_Available");
            IconHeader.Kind = MaterialIconKind.RocketLaunch;
            // SukiPrimaryColor у SukiUI — це Color, а не пензель. Пряме приведення (IBrush) кидало InvalidCastException,
            // і замість «Доступне оновлення» показувалась помилка — оновитися через програму було неможливо.
            IconHeader.Foreground = Accent;
            BorderIconHeader.Background = AccentSoft;

            TxtCurrentVersion.Text = _currentVersion.Trim().TrimStart('v', 'V');
            TxtArrow.IsVisible = true;
            BorderNewVersion.IsVisible = true;
            TxtNewVersion.Text = _newVersion.Trim().TrimStart('v', 'V');
            TxtNewVersion.Foreground = Accent;

            IconCardHeader.Kind = MaterialIconKind.FormatListBulleted;
            IconCardHeader.Foreground = Accent;
            TxtCardHeader.Text = Loc.T("Update_WhatsNew");

            PanelProgress.IsVisible = false;
            ProgressBarUpdate.IsIndeterminate = false;
            ProgressBarUpdate.Value = 0;
            TxtProgressPercent.IsVisible = true;
            TxtProgressPercent.Text = "0%";
            TxtStatusMessage.Text = "";

            BtnLater.IsVisible = true;
            BtnLater.Content = Loc.T("Common_Later");
            BtnLater.IsEnabled = true;

            BtnAction.IsVisible = true;
            BtnAction.IsEnabled = true;
            TxtActionBtn.Text = Loc.T("Update_Now");
            IconAction.Kind = MaterialIconKind.Download;

            _ = LoadReleaseNotesAsync(_newVersion);
        }

        private void SetUpToDateState()
        {
            _state = UpdateWindowState.UpToDate;

            TxtTitle.Text = Loc.T("Update_Latest");
            IconHeader.Kind = MaterialIconKind.CheckCircleOutline;
            IconHeader.Foreground = Accent;
            BorderIconHeader.Background = AccentSoft;

            TxtCurrentVersion.Text = _currentVersion.Trim().TrimStart('v', 'V');
            TxtArrow.IsVisible = true;
            BorderNewVersion.IsVisible = true;
            TxtNewVersion.Text = Loc.T("Update_UpToDate");
            TxtNewVersion.Foreground = Accent;

            IconCardHeader.Kind = MaterialIconKind.ShieldCheckOutline;
            IconCardHeader.Foreground = Accent;
            TxtCardHeader.Text = Loc.T("Update_StatusHeader");
            TxtChangelog.Text = Loc.T("Update_AllInstalled");

            PanelProgress.IsVisible = false;
            ProgressBarUpdate.IsIndeterminate = false;
            TxtStatusMessage.Text = "";

            BtnLater.IsVisible = false;

            BtnAction.IsVisible = true;
            BtnAction.IsEnabled = true;
            TxtActionBtn.Text = Loc.T("Update_Great");
            IconAction.Kind = MaterialIconKind.Check;
        }

        private void SetErrorState(Exception ex)
        {
            _state = UpdateWindowState.Error;

            TxtTitle.Text = Loc.T("Update_CheckFailed");
            IconHeader.Kind = MaterialIconKind.AlertCircleOutline;
            IconHeader.Foreground = Brushes.Coral;
            BorderIconHeader.Background = new SolidColorBrush(Color.FromArgb(30, 255, 127, 80));

            TxtCurrentVersion.Text = _currentVersion.Trim().TrimStart('v', 'V');
            TxtArrow.IsVisible = false;
            BorderNewVersion.IsVisible = false;

            IconCardHeader.Kind = MaterialIconKind.CloudAlert;
            IconCardHeader.Foreground = Brushes.Coral;
            TxtCardHeader.Text = Loc.T("Update_ConnHeader");

            string msg;
            string exStr = ex.ToString();
            if (ex is TimeoutException || ex is OperationCanceledException || ex is TaskCanceledException || exStr.Contains("504") || exStr.Contains("Gateway Time-out") || exStr.Contains("timed out") || exStr.Contains("Timeout") || exStr.Contains("canceled") || exStr.Contains("cancelled"))
            {
                msg = Loc.T("Update_Err504");
            }
            else if (exStr.Contains("403") || exStr.Contains("rate limit"))
            {
                msg = Loc.T("Update_ErrRateLimit");
            }
            else if (exStr.Contains("Socket") || exStr.Contains("NameResolution") || exStr.Contains("No such host"))
            {
                msg = Loc.T("Update_ErrOffline");
            }
            else
            {
                msg = Loc.F("Update_ErrOther", ex.Message);
            }

            TxtChangelog.Text = msg;
            PanelProgress.IsVisible = false;
            ProgressBarUpdate.IsIndeterminate = false;
            TxtStatusMessage.Text = "";

            BtnLater.IsVisible = true;
            BtnLater.Content = Loc.T("Common_Close");
            BtnLater.IsEnabled = true;

            BtnAction.IsVisible = true;
            BtnAction.IsEnabled = true;
            TxtActionBtn.Text = Loc.T("Update_TryAgain");
            IconAction.Kind = MaterialIconKind.Refresh;
        }

        private async Task LoadReleaseNotesAsync(string? version)
        {
            string notes = "";
            string cleanVersion = (version ?? "").Trim().TrimStart('v', 'V');
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Screenshot_One_Updater");
                client.Timeout = TimeSpan.FromSeconds(5);

                // Check clean tag first (e.g. 2.4.4), fallback to tag with 'v' (e.g. v2.4.4)
                string url = $"https://api.github.com/repos/Oleksandr-Rosenko/Screenshot_One/releases/tags/{cleanVersion}";
                var resp = await client.GetAsync(url);
                if (!resp.IsSuccessStatusCode)
                {
                    url = $"https://api.github.com/repos/Oleksandr-Rosenko/Screenshot_One/releases/tags/v{cleanVersion}";
                    resp = await client.GetAsync(url);
                }

                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("body", out var bodyEl))
                    {
                        notes = bodyEl.GetString() ?? "";
                    }
                }
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(notes))
                {
                    string cleanNotes = notes.Trim();
                    if (cleanNotes.StartsWith("###", StringComparison.OrdinalIgnoreCase))
                    {
                        var firstLineEnd = cleanNotes.IndexOf('\n');
                        if (firstLineEnd > 0)
                            cleanNotes = cleanNotes.Substring(firstLineEnd + 1).Trim();
                    }
                    TxtChangelog.Text = cleanNotes.Replace("**", "");
                }
                else
                {
                    TxtChangelog.Text = "• Панель інструментів редактора: базові іконки отримали адаптивний контрастний колір теми.\n• Маркер: покращено видимість жовтого маркера на білому фоні.\n• Світла тема: повноцінна підтримка світлого оформлення з Live Preview.\n• Оновлення дизайну: лаконічні номери версій та автоматичний список змін.";
                }
            });
        }

        private void BtnLater_Click(object? sender, RoutedEventArgs e)
        {
            if (_state == UpdateWindowState.Checking || _state == UpdateWindowState.Error || _state == UpdateWindowState.UpToDate)
            {
                this.Close();
                return;
            }

            if (!_isDownloading)
            {
                SettingsManager.Current.LastUpdateDismissedUtc = DateTime.UtcNow;
                SettingsManager.Current.LastDismissedVersion = _newVersion;
                SettingsManager.Save();
                this.Close();
            }
        }

        private async void BtnAction_Click(object? sender, RoutedEventArgs e)
        {
            if (_state == UpdateWindowState.UpToDate)
            {
                this.Close();
                return;
            }

            if (_state == UpdateWindowState.Error)
            {
                await StartCheckAsync();
                return;
            }

            if (_updateInfo == null)
            {
                this.Close();
                return;
            }

            if (_isReadyToRestart)
            {
                // Safety gate: Check for active editor windows
                if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                {
                    bool hasActiveEditors = desktop.Windows.Any(w => w is EditorWindow || w is VideoEditorWindow);
                    if (hasActiveEditors)
                    {
                        TxtStatusMessage.Text = Loc.T("Update_CloseEditorFirst");
                        TxtStatusMessage.Foreground = Brushes.Orange;
                        return;
                    }
                }

                try
                {
                    SettingsManager.UnregisterAll();
                    HistoryManager.WaitForPendingSaves(TimeSpan.FromSeconds(5));
                    _updateManager?.ApplyUpdatesAndRestart(_updateInfo);
                }
                catch (Exception ex)
                {
                    TxtStatusMessage.Text = Loc.F("Update_RestartFailed", ex.Message);
                    TxtStatusMessage.Foreground = Danger;
                }
                return;
            }

            // Start downloading
            if (_updateManager == null) return;

            _isDownloading = true;
            BtnLater.IsEnabled = false;
            BtnAction.IsEnabled = false;
            PanelProgress.IsVisible = true;
            ProgressBarUpdate.IsIndeterminate = false;
            TxtProgressStatus.Text = Loc.T("Update_Downloading");
            TxtStatusMessage.Text = "";

            try
            {
                Action<int> progressCallback = (p) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        ProgressBarUpdate.Value = p;
                        TxtProgressPercent.Text = $"{p}%";
                    });
                };

                await _updateManager.DownloadUpdatesAsync(_updateInfo, progressCallback);

                _isReadyToRestart = true;
                _isDownloading = false;
                ProgressBarUpdate.Value = 100;
                TxtProgressPercent.Text = "100%";
                TxtProgressStatus.Text = Loc.T("Update_Ready");
                TxtActionBtn.Text = Loc.T("Update_RestartNow");
                IconAction.Kind = MaterialIconKind.Restart;
                BtnAction.IsEnabled = true;
                BtnLater.IsEnabled = true;
                BtnLater.Content = Loc.T("Common_Later");
            }
            catch (Exception ex)
            {
                _isDownloading = false;
                BtnAction.IsEnabled = true;
                BtnLater.IsEnabled = true;
                TxtStatusMessage.Text = Loc.F("Update_DownloadFailed", ex.Message);
                TxtStatusMessage.Foreground = Danger;
            }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (_isDownloading && !_isReadyToRestart)
            {
                e.Cancel = true;
                TxtStatusMessage.Text = Loc.T("Update_InProgress");
                TxtStatusMessage.Foreground = Brushes.Orange;
                return;
            }

            base.OnClosing(e);
        }

        private static string GetCurrentVersionString(UpdateManager? updateManager)
        {
            if (updateManager != null && updateManager.IsInstalled && updateManager.CurrentVersion != null)
            {
                return updateManager.CurrentVersion.ToString();
            }

            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            if (assemblyVersion != null)
            {
                return $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}";
            }

            return "2.8.0";
        }

        public static async Task CheckAndShowAsync(bool isManual = true)
        {
            // На Mac немає інсталятора Velopack — окрема перевірка через GitHub (див. MacUpdateChecker)
            if (OperatingSystem.IsMacOS())
            {
                await MacUpdateChecker.CheckAsync(isManual);
                return;
            }

            if (isManual)
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    if (_activeWindow != null)
                    {
                        _activeWindow.Activate();
                        return;
                    }

                    var win = new UpdateWindow();
                    _activeWindow = win;
                    win.Closed += (s, e) => _activeWindow = null;
                    win.Show();
                    win.Activate();

                    await win.StartCheckAsync();
                });
            }
            else
            {
                try
                {
                    string repoUrl = "https://github.com/Oleksandr-Rosenko/Screenshot_One";
                    var source = new Velopack.Sources.GithubSource(repoUrl, accessToken: null, prerelease: false);
                    var updateManager = new UpdateManager(source);

                    string currentVersionStr = GetCurrentVersionString(updateManager);

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    var updateInfo = await updateManager.CheckForUpdatesAsync().WaitAsync(cts.Token);

                    if (updateInfo != null)
                    {
                        string newVersionStr = updateInfo.TargetFullRelease.Version.ToString();

                        if (SettingsManager.Current.LastUpdateDismissedUtc.HasValue &&
                            (DateTime.UtcNow - SettingsManager.Current.LastUpdateDismissedUtc.Value).TotalHours < 24 &&
                            SettingsManager.Current.LastDismissedVersion == newVersionStr)
                        {
                            return; // Dismissed less than 24h ago
                        }

                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (_activeWindow != null)
                            {
                                _activeWindow.Activate();
                                return;
                            }

                            var win = new UpdateWindow(updateManager, updateInfo, currentVersionStr, newVersionStr, isUpToDate: false);
                            _activeWindow = win;
                            win.Closed += (s, e) => _activeWindow = null;
                            win.Show();
                            win.Activate();
                        });
                    }
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            }
        }
    }
}
