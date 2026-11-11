using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
// using ScreenRecorderLib; (decoupled via VideoRecorderManager)
using MsBox.Avalonia;

namespace ScreenShot_One_Avalonia
{
    public class MonitorInfo
    {
        public string DisplayName { get; set; } = "";
        public string DeviceName { get; set; } = "";
    }

    public partial class VideoControlWindow : Window
    {
        [DllImport("user32.dll")]
        public static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

        private DispatcherTimer _timer;
        private DateTime _startTime;
        // Час, записаний до останньої паузи (таймер не рахує паузи)
        private TimeSpan _elapsedBeforePause = TimeSpan.Zero;
        private VideoRegionWindow? _activeRegionWindow;
        private bool _isBusy; // йде старт або зупинка запису — повторні кліки ігноруємо
        private bool _ownsRecording; // запис запустило саме це вікно

        public VideoControlWindow()
        {
            InitializeComponent();
            this.Topmost = true;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Timer_Tick;

            LoadMonitors();

            ChkRecordMic.IsChecked = SettingsManager.Current.RecordMicrophone;

            // На Mac системний звук без віртуального аудіопристрою недоступний — лишаємо лише мікрофон
            if (OperatingSystem.IsMacOS())
            {
                ChkRecordAudio.IsChecked = false;
                ChkRecordAudio.IsVisible = false;
            }

            VideoRecorderManager.OnRecordingFailed += OnRecordingFailed;
        }

        protected override void OnClosed(EventArgs e)
        {
            VideoRecorderManager.OnRecordingFailed -= OnRecordingFailed;
            base.OnClosed(e);
        }

        // Запис зупинився сам (помилка енкодера / ffmpeg) — повертаємо панель у вихідний стан і показуємо причину
        private void OnRecordingFailed(string error)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                if (!_ownsRecording) return;
                _ownsRecording = false;
                SetRecordingStateUI(false);
                CloseRegionWindow();
                await ShowErrorAsync("Запис зупинено", error);
            });
        }

        private void CloseRegionWindow()
        {
            if (_activeRegionWindow != null)
            {
                _activeRegionWindow.Close();
                _activeRegionWindow = null;
            }
        }

        private Task ShowErrorAsync(string title, string message)
        {
            var box = MessageBoxManager.GetMessageBoxStandard(title, message);
            return box.ShowWindowDialogAsync(this);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (OperatingSystem.IsWindows())
            {
                var handle = this.TryGetPlatformHandle()?.Handle;
                if (handle.HasValue) SetWindowDisplayAffinity(handle.Value, 0x00000011);
            }

            var screen = this.Screens.Primary;
            if (screen != null)
            {
                int screenWidth = screen.Bounds.Width;
                int screenHeight = screen.Bounds.Height;
                int windowWidth = (int)(this.Width * screen.Scaling);
                this.Position = new PixelPoint(screen.Bounds.X + (screenWidth - windowWidth) / 2, screen.Bounds.Y + screenHeight - (int)(140 * screen.Scaling));
            }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            TxtTimer.Text = FormatElapsed(_elapsedBeforePause + (DateTime.Now - _startTime));
        }

        private static string FormatElapsed(TimeSpan t)
            => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

        private async void BtnPause_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy || !VideoRecorderManager.IsRecording) return;
            _isBusy = true;
            BtnPause.IsEnabled = false;
            try
            {
                if (VideoRecorderManager.IsPaused)
                {
                    if (await VideoRecorderManager.ResumeAsync())
                    {
                        _startTime = DateTime.Now;
                        _timer.Start();
                        TxtPauseIcon.Text = "⏸";
                        TxtTimer.Foreground = Brushes.White;
                    }
                    else
                    {
                        await ShowErrorAsync("Помилка", "Не вдалося продовжити запис.");
                    }
                }
                else
                {
                    if (await VideoRecorderManager.PauseAsync())
                    {
                        _elapsedBeforePause += DateTime.Now - _startTime;
                        _timer.Stop();
                        TxtTimer.Text = FormatElapsed(_elapsedBeforePause);
                        TxtPauseIcon.Text = "▶";
                        TxtTimer.Foreground = Brushes.Orange;
                    }
                    else
                    {
                        await ShowErrorAsync("Помилка", "Не вдалося поставити запис на паузу.");
                    }
                }
            }
            finally
            {
                BtnPause.IsEnabled = true;
                _isBusy = false;
            }
        }

        private void LoadMonitors()
        {
            try
            {
                var monitorList = VideoRecorderManager.GetDisplays();
                ComboMonitor.ItemsSource = monitorList;
                if (monitorList.Count > 0) ComboMonitor.SelectedIndex = 0;
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }
        }

        private void Border_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var hitControl = e.Source as Control;
            if (hitControl != null && IsInteractiveControl(hitControl)) return;

            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);
            }
        }

        private bool IsInteractiveControl(Control? control)
        {
            Control? current = control;
            while (current != null)
            {
                if (current is Button || current is ComboBox || current is CheckBox || current is ComboBoxItem)
                    return true;
                current = current.Parent as Control;
            }
            return false;
        }

        private async void BtnRecordToggle_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (VideoRecorderManager.IsRecording)
            {
                await StopAndOpenEditorAsync(closeWindow: true);
                return;
            }

            try
            {
                bool showClicks = ChkShowMouse.IsChecked == true;
                bool recordAudio = ChkRecordAudio.IsChecked == true;
                bool recordMic = ChkRecordMic.IsChecked == true;
                bool isRegion = ComboRecordArea.SelectedIndex == 1;

                var selectedMonitor = ComboMonitor.SelectedItem as MonitorInfo;
                string deviceName = selectedMonitor != null ? selectedMonitor.DeviceName : (this.Screens.Primary?.ToString() ?? "Primary");

                if (isRegion)
                {
                    _activeRegionWindow = new VideoRegionWindow(
                        async (left, top, right, bottom) =>
                        {
                            await StartRecordingAsync(showClicks, recordAudio, deviceName, left, top, right, bottom, recordMic);
                        },
                        () =>
                        {
                            _activeRegionWindow = null;
                        },
                        deviceName
                    );

                    _activeRegionWindow.Show();

                    this.Topmost = false;
                    this.Topmost = true;
                    this.Activate();
                }
                else
                {
                    await StartRecordingAsync(showClicks, recordAudio, deviceName, recordMic: recordMic);
                }
            }
            catch (Exception ex)
            {
                TxtTimer.Text = "Err";
                await ShowErrorAsync("Критична помилка", ex.ToString());
            }
        }

        private async Task StartRecordingAsync(bool showClicks, bool recordAudio, string deviceName,
            int? left = null, int? top = null, int? right = null, int? bottom = null, bool recordMic = false)
        {
            _isBusy = true;
            BtnRecordToggle.IsEnabled = false;
            try
            {
                // Запам'ятовуємо вибір мікрофона для наступних записів
                if (SettingsManager.Current.RecordMicrophone != recordMic)
                {
                    SettingsManager.Current.RecordMicrophone = recordMic;
                    try { SettingsManager.Save(); } catch (Exception ex) { AppLog.Warn(ex); }
                }

                var result = await VideoRecorderManager.StartRecordingAsync(showClicks, recordAudio, deviceName, left, top, right, bottom, recordMic);

                if (!result.Success)
                {
                    // Раніше тут інтерфейс однаково показував «йде запис», навіть якщо запис не стартував
                    SetRecordingStateUI(false);
                    CloseRegionWindow();
                    await ShowErrorAsync("Не вдалося почати запис", result.Error ?? "Невідома помилка.");
                    return;
                }

                _ownsRecording = true;
                SetRecordingStateUI(true);
                this.Topmost = false;
                this.Topmost = true;
                this.Activate();
            }
            finally
            {
                BtnRecordToggle.IsEnabled = true;
                _isBusy = false;
            }
        }

        /// <summary>
        /// Зупиняє запис, чекає, поки файл буде повністю дописаний, і лише тоді відкриває редактор.
        /// Раніше редактор відкривався одразу, коли файл ще дописувався у фоні.
        /// </summary>
        private async Task StopAndOpenEditorAsync(bool closeWindow)
        {
            _ownsRecording = false;
            _isBusy = true;
            BtnRecordToggle.IsEnabled = false;
            _timer.Stop();
            TxtTimer.Text = "...";

            string videoPath;
            try
            {
                videoPath = await VideoRecorderManager.StopRecordingAsync();
            }
            finally
            {
                BtnRecordToggle.IsEnabled = true;
                _isBusy = false;
            }

            SetRecordingStateUI(false);
            CloseRegionWindow();

            if (!string.IsNullOrEmpty(videoPath))
            {
                new VideoEditorWindow(videoPath, true).Show();
                // Закриваємо, а не ховаємо: щоб приховані панелі не накопичувались
                if (closeWindow) this.Close();
            }
            else
            {
                await ShowErrorAsync("Відео не збережено", "Не вдалося отримати файл запису. Спробуйте записати ще раз.");
            }
        }

        private void SetRecordingStateUI(bool isRecording)
        {
            if (isRecording)
            {
                RecordIcon.Background = Brushes.White;
                RecordIcon.CornerRadius = new CornerRadius(2);
                RecordIcon.Width = 12; RecordIcon.Height = 12;
                _elapsedBeforePause = TimeSpan.Zero;
                _startTime = DateTime.Now; TxtTimer.Text = "00:00"; _timer.Start();
                BtnPause.IsVisible = true; TxtPauseIcon.Text = "⏸"; TxtTimer.Foreground = Brushes.White;
                ChkRecordMic.IsEnabled = false; ChkRecordAudio.IsEnabled = false;
            }
            else
            {
                RecordIcon.Background = Brushes.Red;
                RecordIcon.CornerRadius = new CornerRadius(8);
                RecordIcon.Width = 16; RecordIcon.Height = 16;
                _timer.Stop(); TxtTimer.Text = "00:00";
                BtnPause.IsVisible = false; TxtTimer.Foreground = Brushes.White;
                ChkRecordMic.IsEnabled = true; ChkRecordAudio.IsEnabled = true;
            }
        }

        private async void BtnClose_Click(object? sender, RoutedEventArgs e)
        {
            if (_isBusy) return;

            if (VideoRecorderManager.IsRecording)
            {
                await StopAndOpenEditorAsync(closeWindow: false);
            }

            CloseRegionWindow();
            this.Close();
        }
    }
}
