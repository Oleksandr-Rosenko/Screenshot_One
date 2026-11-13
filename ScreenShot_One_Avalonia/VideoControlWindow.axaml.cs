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

        // Кольори таймера в тонах палітри (панель завжди темна)
        private static readonly IBrush TimerBrush = new SolidColorBrush(Color.Parse("#E2EDEA"));
        private static readonly IBrush PausedBrush = new SolidColorBrush(Color.Parse("#F0B54A"));

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

            TglMic.IsChecked = SettingsManager.Current.RecordMicrophone;
            TglKeys.IsChecked = SettingsManager.Current.ShowKeystrokes;

            // На Mac системний звук без віртуального аудіопристрою недоступний — лишаємо лише мікрофон
            if (OperatingSystem.IsMacOS())
            {
                TglAudio.IsChecked = false;
                TglAudio.IsVisible = false;
            }

            VideoRecorderManager.OnRecordingFailed += OnRecordingFailed;
        }

        protected override void OnClosed(EventArgs e)
        {
            KeystrokeOverlay.Stop();
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
                KeystrokeOverlay.Stop();
                SetRecordingStateUI(false);
                CloseRegionWindow();
                await ShowErrorAsync(Loc.T("Video_Stopped"), error);
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

        /// <summary>Перемикач «Екран / Зона»: завжди вибраний рівно один режим.</summary>
        private void Mode_Click(object? sender, RoutedEventArgs e)
        {
            bool region = sender == TglRegion;
            TglRegion.IsChecked = region;
            TglFullScreen.IsChecked = !region;
        }

        /// <summary>Тримає панель по центру тієї ж точки, коли вона міняє ширину (налаштування ↔ запис).</summary>
        private void KeepCentered(double oldWidth)
        {
            if (oldWidth <= 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                double scale = RenderScaling > 0 ? RenderScaling : 1.0;
                int dx = (int)((oldWidth - Bounds.Width) * scale / 2);
                Position = new PixelPoint(Position.X + dx, Position.Y);
            }, DispatcherPriority.Loaded);
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            if (OperatingSystem.IsWindows())
            {
                var handle = this.TryGetPlatformHandle()?.Handle;
                if (handle.HasValue) SetWindowDisplayAffinity(handle.Value, 0x00000011);
            }

            // Унизу по центру головного екрана (розмір відомий лише після розмітки)
            Dispatcher.UIThread.Post(() =>
            {
                var screen = this.Screens.Primary;
                if (screen == null) return;
                var area = screen.WorkingArea;
                int w = (int)(Bounds.Width * screen.Scaling), h = (int)(Bounds.Height * screen.Scaling);
                Position = new PixelPoint(area.X + (area.Width - w) / 2, area.Bottom - h - (int)(24 * screen.Scaling));
            }, DispatcherPriority.Loaded);
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
                        IcoPause.Kind = Material.Icons.MaterialIconKind.Pause;
                        RecDot.Classes.Set("pulse", true);
                        TxtTimer.Foreground = TimerBrush;
                    }
                    else
                    {
                        await ShowErrorAsync(Loc.T("Common_Error"), Loc.T("Video_ResumeFailed"));
                    }
                }
                else
                {
                    if (await VideoRecorderManager.PauseAsync())
                    {
                        _elapsedBeforePause += DateTime.Now - _startTime;
                        _timer.Stop();
                        TxtTimer.Text = FormatElapsed(_elapsedBeforePause);
                        IcoPause.Kind = Material.Icons.MaterialIconKind.Play;
                        RecDot.Classes.Set("pulse", false);
                        TxtTimer.Foreground = PausedBrush;
                    }
                    else
                    {
                        await ShowErrorAsync(Loc.T("Common_Error"), Loc.T("Video_PauseFailed"));
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
                ComboMonitor.IsVisible = monitorList.Count > 1; // вибір монітора — лише коли є з чого вибирати
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
                if (current is Button || current is ComboBox || current is Avalonia.Controls.Primitives.ToggleButton || current is ComboBoxItem)
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
                bool showClicks = TglClicks.IsChecked == true;
                bool recordAudio = TglAudio.IsChecked == true;
                bool recordMic = TglMic.IsChecked == true;
                bool isRegion = TglRegion.IsChecked == true;

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
                await ShowErrorAsync(Loc.T("Video_CriticalError"), ex.ToString());
            }
        }

        private async Task StartRecordingAsync(bool showClicks, bool recordAudio, string deviceName,
            int? left = null, int? top = null, int? right = null, int? bottom = null, bool recordMic = false)
        {
            _isBusy = true;
            BtnRecordToggle.IsEnabled = false;
            try
            {
                // Запам'ятовуємо вибір мікрофона й показу клавіш для наступних записів
                bool showKeys = TglKeys.IsChecked == true;
                if (SettingsManager.Current.RecordMicrophone != recordMic || SettingsManager.Current.ShowKeystrokes != showKeys)
                {
                    SettingsManager.Current.RecordMicrophone = recordMic;
                    SettingsManager.Current.ShowKeystrokes = showKeys;
                    try { SettingsManager.Save(); } catch (Exception ex) { AppLog.Warn(ex); }
                }

                var result = await VideoRecorderManager.StartRecordingAsync(showClicks, recordAudio, deviceName, left, top, right, bottom, recordMic);

                if (!result.Success)
                {
                    // Раніше тут інтерфейс однаково показував «йде запис», навіть якщо запис не стартував
                    SetRecordingStateUI(false);
                    CloseRegionWindow();
                    await ShowErrorAsync(Loc.T("Video_StartFailedTitle"), result.Error ?? Loc.T("Common_UnknownError"));
                    return;
                }

                _ownsRecording = true;
                SetRecordingStateUI(true);

                // Натиснуті клавіші — унизу області запису (зона або весь вибраний монітор)
                if (showKeys)
                {
                    PixelRect area = left.HasValue && top.HasValue && right.HasValue && bottom.HasValue
                        ? new PixelRect(left.Value, top.Value, right.Value - left.Value, bottom.Value - top.Value)
                        : (ScreenService.FindScreenByDeviceName(Screens, deviceName) ?? Screens.Primary)?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
                    try { KeystrokeOverlay.Start(area); } catch (Exception ex) { AppLog.Error(ex, "Показ клавіш"); }
                }
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
            KeystrokeOverlay.Stop();
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
                await ShowErrorAsync(Loc.T("Video_NotSavedTitle"), Loc.T("Video_NotSaved"));
            }
        }

        private void SetRecordingStateUI(bool isRecording)
        {
            double oldWidth = Bounds.Width;
            SetupPanel.IsVisible = !isRecording;
            RecordingPanel.IsVisible = isRecording;
            if (isRecording)
            {
                // Кнопка стає «Стоп»: білий квадрат замість кола
                RecordIcon.CornerRadius = new CornerRadius(2);
                TxtRecordLabel.Text = Loc.T("Video_Stop");
                _elapsedBeforePause = TimeSpan.Zero;
                _startTime = DateTime.Now; TxtTimer.Text = "00:00"; _timer.Start();
                IcoPause.Kind = Material.Icons.MaterialIconKind.Pause;
                RecDot.Classes.Set("pulse", true);
                TxtTimer.Foreground = TimerBrush;
            }
            else
            {
                RecordIcon.CornerRadius = new CornerRadius(6);
                TxtRecordLabel.Text = Loc.T("Video_Record");
                _timer.Stop(); TxtTimer.Text = "00:00";
                TxtTimer.Foreground = TimerBrush;
            }
            KeepCentered(oldWidth);
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
