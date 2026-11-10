using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.Controls.Primitives;
using System.Linq;
using LibVLCSharp.Shared;
using LibVLCSharp.Avalonia;
using Xabe.FFmpeg;
using Xabe.FFmpeg.Downloader;
using SukiUI.Controls;
using MsBox.Avalonia;

namespace ScreenShot_One_Avalonia
{
    public partial class VideoEditorWindow : SukiWindow
    {
        private LibVLC? _libVLC;
        private LibVLCSharp.Shared.MediaPlayer? _mediaPlayer;
        private string _sourceFile;
        private DispatcherTimer? _timer;

        private bool _isDraggingSlider = false;
        private bool _wasPlayingBeforeDrag = false;
        private DateTime _lastSliderSeekTime = DateTime.MinValue;

        private bool _isClosedCleanly = false;
        private bool _isNewRecording = false;
        private bool _isVideoKept = false;
        private bool _isVideoLoaded = false;

        private bool _isProgrammaticTrimChange = false;
        private bool _trimSeekPending = false;
        private DateTime _lastTrimChangeTime = DateTime.MinValue;
        private double _lastTrimValue = 0;
        private bool _isCutoutMode = false;
        private static readonly IBrush InactiveTrimTextBrush = new SolidColorBrush(Color.Parse("#94A3B8"));

        private HistoryWindow? _historyOwner;
        private HistoryItem? _sourceHistoryItem;
        private bool _returnedToHistory = false;
        private string? _backupOriginalFile;

        public VideoEditorWindow(string videoPath, bool isNewRecording = false, HistoryWindow? historyOwner = null, HistoryItem? sourceItem = null)
        {
            InitializeComponent();
            UpdateButtonStyles();
            _sourceFile = videoPath;
            _isNewRecording = isNewRecording;
            _historyOwner = historyOwner;
            _sourceHistoryItem = sourceItem;

            if (_historyOwner != null)
            {
                BtnBackToHistory.IsVisible = true;
                if (SepBackToHistory != null) SepBackToHistory.IsVisible = true;
            }

            try
            {
                Core.Initialize();
                _libVLC = new LibVLC();
                _mediaPlayer = new LibVLCSharp.Shared.MediaPlayer(_libVLC);

                _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _timer.Tick += Timer_Tick;

                TrimStartSlider.PropertyChanged += TrimSlider_PropertyChanged;
                TrimEndSlider.PropertyChanged += TrimSlider_PropertyChanged;

                // Seamless timeline scrubbing without jitter
                VideoSlider.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
                {
                    _isDraggingSlider = true;
                    _wasPlayingBeforeDrag = _mediaPlayer?.IsPlaying == true;
                    if (_wasPlayingBeforeDrag && _mediaPlayer != null)
                    {
                        _mediaPlayer.Pause();
                        MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
                    }
                }, RoutingStrategies.Tunnel);

                VideoSlider.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
                {
                    CommitSliderSeek();
                }, RoutingStrategies.Tunnel);

                VideoSlider.AddHandler(InputElement.PointerCaptureLostEvent, (s, e) =>
                {
                    CommitSliderSeek();
                }, RoutingStrategies.Tunnel);

                VideoSlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == nameof(Slider.Value) && _isDraggingSlider && _isVideoLoaded && _mediaPlayer != null && _mediaPlayer.Length > 0)
                    {
                        if (e.NewValue is double val)
                        {
                            long targetMs = (long)(val / 100.0 * _mediaPlayer.Length);
                            TxtCurrentTime.Text = FormatTimeWithHundredths(targetMs);

                            if ((DateTime.Now - _lastSliderSeekTime).TotalMilliseconds > 60)
                            {
                                _lastSliderSeekTime = DateTime.Now;
                                _mediaPlayer.Time = targetMs;
                            }
                        }
                    }
                };
            }
            catch (Exception ex)
            {
                _ = ShowErrorDialog("Помилка ініціалізації плеєра (Конструктор)", ex);
            }

            this.Opened += async (s, e) =>
            {
                try
                {
                    TxtLoading.Text = "Обробка та завантаження відео, зачекайте...";
                    TxtLoading.IsVisible = true;

                    if (!string.IsNullOrEmpty(_sourceFile))
                    {
                        TxtVideoTitle.Text = Path.GetFileName(_sourceFile);
                        UpdateButtonStyles();
                        SetVideoSliderTrackVisibility(BtnTrimMode.IsChecked != true);

                        if (File.Exists(_sourceFile) && string.IsNullOrEmpty(_backupOriginalFile))
                        {
                            try
                            {
                                string dir = Path.GetDirectoryName(_sourceFile) ?? "";
                                string name = Path.GetFileNameWithoutExtension(_sourceFile);
                                string ext = Path.GetExtension(_sourceFile);
                                string bak = Path.Combine(dir, $"{name}_session_bak{ext}");
                                File.Copy(_sourceFile, bak, overwrite: true);
                                _backupOriginalFile = bak;
                            }
                            catch (Exception exBak)
                            {
                                Debug.WriteLine($"Session backup error: {exBak.Message}");
                            }
                        }
                    }

                    if (_mediaPlayer != null)
                    {
                        VideoPlayer.MediaPlayer = _mediaPlayer;
                    }

                    await WaitForFileUnlock(_sourceFile);

                    if (_libVLC == null || _mediaPlayer == null || _isClosedCleanly) return;

                    var media = new LibVLCSharp.Shared.Media(_libVLC, _sourceFile, FromType.FromPath);
                    _mediaPlayer.Media = media;

                    _mediaPlayer.Play();

                    await Task.Delay(350);

                    if (!_isClosedCleanly)
                    {
                        _mediaPlayer.Pause();
                        MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;

                        TxtLoading.IsVisible = false;
                        _isVideoLoaded = true;

                        UpdateTrimHighlight();
                        UpdateTrimTimeLabels();

                        _timer?.Start();
                    }
                }
                catch (Exception ex)
                {
                    TxtLoading.Text = "Помилка завантаження відео";
                    await ShowErrorDialog("Помилка під час відкриття відео (Opened)", ex);
                }
            };

            this.Closing += (s, e) => { CleanUpVlcBackground(); };

            if (_mediaPlayer != null)
            {
                _mediaPlayer.EndReached += (s, e) =>
                {
                    Task.Run(() =>
                    {
                        try { _mediaPlayer?.Stop(); } catch { }
                    });

                    Dispatcher.UIThread.Post(() =>
                    {
                        MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
                        VideoSlider.Value = 0;
                        TxtCurrentTime.Text = "00:00.00";
                    });
                };
            }
        }

        
        private static string FormatTimeWithHundredths(long ms)
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            int hundredths = ts.Milliseconds / 10;
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{hundredths:D2}";
        }

        private void EnsurePlayerReadyForPlayback()
        {
            if (_mediaPlayer == null || _libVLC == null || string.IsNullOrEmpty(_sourceFile)) return;

            var state = _mediaPlayer.State;
            if (state == VLCState.Ended || state == VLCState.Stopped || state == VLCState.Error || _mediaPlayer.Time < 0)
            {
                try
                {
                    var media = new LibVLCSharp.Shared.Media(_libVLC, _sourceFile, FromType.FromPath);
                    _mediaPlayer.Media = media;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"EnsurePlayerReadyForPlayback error: {ex.Message}");
                }
            }
        }

        private void CommitSliderSeek()
        {
            if (!_isDraggingSlider) return;
            _isDraggingSlider = false;

            if (_isVideoLoaded && _mediaPlayer != null && _mediaPlayer.Length > 0)
            {
                EnsurePlayerReadyForPlayback();

                long targetMs = (long)(VideoSlider.Value / 100.0 * _mediaPlayer.Length);
                _mediaPlayer.Time = targetMs;
                TxtCurrentTime.Text = FormatTimeWithHundredths(targetMs);

                if (_wasPlayingBeforeDrag)
                {
                    _mediaPlayer.Play();
                    MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Pause;
                }
                else
                {
                    MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
                }
            }
        }

        private async Task ShowErrorDialog(string title, Exception ex)
        {
            try
            {
                string details = $"Заголовок: {title}\n\nФайл: {_sourceFile}\n\nТип помилки: {ex.GetType().FullName}\nПовідомлення: {ex.Message}\n\nStack Trace:\n{ex.StackTrace}";
                if (ex.InnerException != null) details += $"\n\nInner Exception: {ex.InnerException.Message}\n{ex.InnerException.StackTrace}";

                var box = MessageBoxManager.GetMessageBoxStandard("Критична помилка редактора", details);
                await box.ShowWindowDialogAsync(this);
            }
            catch
            {
                Debug.WriteLine($"[CRITICAL ERROR] {title}: {ex}");
            }
        }

        private async Task WaitForFileUnlock(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return;

            // For existing recordings (e.g. opened from History), the file is already finished
            if (!_isNewRecording)
            {
                if (File.Exists(filePath)) return;
            }

            for (int i = 0; i < 30; i++)
            {
                try
                {
                    if (File.Exists(filePath) && IsFileReady(filePath)) return;
                }
                catch { }
                await Task.Delay(100);
            }
        }

        private bool IsFileReady(string filename)
        {
            try
            {
                using (FileStream stream = File.Open(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    return stream.Length > 0;
                }
            }
            catch
            {
                return false;
            }
        }

        private void CleanUpVlcBackground()
        {
            if (_isClosedCleanly) return;
            _isClosedCleanly = true;
            _isVideoLoaded = false;
            _timer?.Stop();

            try
            {
                var player = _mediaPlayer;
                var vlc = _libVLC;

                if (VideoPlayer != null) VideoPlayer.MediaPlayer = null;

                _mediaPlayer = null;
                _libVLC = null;

                Task.Run(() =>
                {
                    try
                    {
                        if (player != null)
                        {
                            if (player.IsPlaying) player.Stop();
                            player.Media?.Dispose();
                            player.Dispose();
                        }
                        vlc?.Dispose();
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            try
            {
                if (_isClosedCleanly || !_isVideoLoaded || _mediaPlayer == null) return;

                if (_trimSeekPending)
                {
                    if ((DateTime.Now - _lastTrimChangeTime).TotalMilliseconds > 120)
                    {
                        _trimSeekPending = false;

                        if (_mediaPlayer.IsPlaying)
                        {
                            _mediaPlayer.Pause();
                            MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
                        }

                        long previewTimeMs = _mediaPlayer.Length > 0 ? (long)(_lastTrimValue / 100.0 * _mediaPlayer.Length) : 0;
                        _mediaPlayer.Time = previewTimeMs;
                    }
                    return;
                }

                if (_isDraggingSlider) return;

                long current = _mediaPlayer.Time;
                long total = _mediaPlayer.Length;

                if (total > 0)
                {
                    if (BtnTrimMode.IsChecked == true)
                    {
                        long startMs = (long)(TrimStartSlider.Value / 100.0 * total);
                        long endMs = (long)(TrimEndSlider.Value / 100.0 * total);

                        if (!_isCutoutMode)
                        {
                            if (current >= endMs && _mediaPlayer.IsPlaying)
                            {
                                _mediaPlayer.Pause();
                                _mediaPlayer.Time = startMs;
                                VideoSlider.Value = (double)startMs / total * 100.0;
                                TxtCurrentTime.Text = FormatTimeWithHundredths(startMs);
                                MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
                                return;
                            }
                        }
                        else
                        {
                            // In Cutout mode: seamlessly skip over the cut section during playback preview!
                            if (current >= startMs && current < endMs && _mediaPlayer.IsPlaying)
                            {
                                _mediaPlayer.Time = endMs;
                                current = endMs;
                            }
                        }
                    }
                    else
                    {
                        // Video reached end in normal playback
                        if ((current >= total - 150 || _mediaPlayer.State == VLCState.Ended) && _mediaPlayer.IsPlaying)
                        {
                            _mediaPlayer.Pause();
                            _mediaPlayer.Time = 0;
                            VideoSlider.Value = 0;
                            TxtCurrentTime.Text = "00:00.00";
                            MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
                            return;
                        }
                    }

                    if (!_mediaPlayer.IsPlaying && _mediaPlayer.State != VLCState.Opening && _mediaPlayer.State != VLCState.Buffering)
                    {
                        if (MainPlayIcon.Kind != Material.Icons.MaterialIconKind.Play)
                        {
                            MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
                        }
                    }

                    if (total - current < 150) current = total;

                    VideoSlider.Value = (double)current / total * 100.0;
                    TxtCurrentTime.Text = FormatTimeWithHundredths(current);
                    TxtTotalTime.Text = FormatTimeWithHundredths(total);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Timer error: {ex}");
            }
        }

        
        private void SetVideoSliderTrackVisibility(bool isVisible)
        {
            try
            {
                if (VideoSlider == null) return;
                if (!isVisible)
                {
                    if (!VideoSlider.Classes.Contains("NoTrack"))
                        VideoSlider.Classes.Add("NoTrack");
                }
                else
                {
                    VideoSlider.Classes.Remove("NoTrack");
                }

                foreach (var repeatBtn in VideoSlider.GetVisualDescendants().OfType<RepeatButton>())
                {
                    repeatBtn.Opacity = isVisible ? 1.0 : 0.0;
                }
            }
            catch { }
        }

        private void UpdateButtonStyles()
        {
            try
            {
                bool isDark = this.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;
                if (BtnUpload != null)
                {
                    BtnUpload.Background = isDark ? new SolidColorBrush(Color.Parse("#1E293B")) : new SolidColorBrush(Color.Parse("#F1F5F9"));
                    BtnUpload.BorderBrush = new SolidColorBrush(Color.Parse("#94A3B8"));
                    BtnUpload.BorderThickness = new Thickness(1.5);
                }
                if (TxtBtnUpload != null)
                {
                    TxtBtnUpload.Foreground = isDark ? Brushes.White : new SolidColorBrush(Color.Parse("#0F172A"));
                    TxtBtnUpload.FontWeight = FontWeight.Bold;
                }
            }
            catch { }
        }

        private void UpdateTrimHighlight()
        {
            if (TrimHighlightGrid == null || TrimHighlightGrid.ColumnDefinitions.Count < 3) return;

            double startVal = Math.Clamp(TrimStartSlider.Value, 0, 100);
            double endVal = Math.Clamp(TrimEndSlider.Value, 0, 100);
            if (startVal > endVal) startVal = endVal;

            double activeVal = Math.Max(0.1, endVal - startVal);
            double rightVal = Math.Max(0, 100.0 - endVal);

            TrimHighlightGrid.ColumnDefinitions[0].Width = new GridLength(startVal, GridUnitType.Star);
            TrimHighlightGrid.ColumnDefinitions[1].Width = new GridLength(activeVal, GridUnitType.Star);
            TrimHighlightGrid.ColumnDefinitions[2].Width = new GridLength(rightVal, GridUnitType.Star);

            if (TrimMaskLeft != null && TrimMaskActive != null && TrimMaskRight != null)
            {
                if (!_isCutoutMode)
                {
                    // Mode: Keep Range
                    // Left/Right: Red-tinted / Dimmed (Cut)
                    TrimMaskLeft.Background = new SolidColorBrush(Color.FromArgb(160, 239, 68, 68));
                    TrimMaskRight.Background = new SolidColorBrush(Color.FromArgb(160, 239, 68, 68));
                    if (TxtMaskLeft != null) { TxtMaskLeft.Text = "✂ ВИДАЛЯЄТЬСЯ"; TxtMaskLeft.Foreground = Brushes.White; TxtMaskLeft.IsVisible = startVal > 10; }
                    if (TxtMaskRight != null) { TxtMaskRight.Text = "✂ ВИДАЛЯЄТЬСЯ"; TxtMaskRight.Foreground = Brushes.White; TxtMaskRight.IsVisible = rightVal > 10; }

                    // Middle: Vivid Green (Keep)
                    TrimMaskActive.Background = new SolidColorBrush(Color.Parse("#10B981"));
                    if (TxtMaskActive != null) { TxtMaskActive.Text = "✓ ЗБЕРІГАЄТЬСЯ"; TxtMaskActive.Foreground = Brushes.White; TxtMaskActive.IsVisible = activeVal > 12; }
                }
                else
                {
                    // Mode: Cutout Middle
                    // Left/Right: Vivid Green (Keep)
                    TrimMaskLeft.Background = new SolidColorBrush(Color.Parse("#10B981"));
                    TrimMaskRight.Background = new SolidColorBrush(Color.Parse("#10B981"));
                    if (TxtMaskLeft != null) { TxtMaskLeft.Text = "✓ ЗБЕРІГАЄТЬСЯ"; TxtMaskLeft.Foreground = Brushes.White; TxtMaskLeft.IsVisible = startVal > 10; }
                    if (TxtMaskRight != null) { TxtMaskRight.Text = "✓ ЗБЕРІГАЄТЬСЯ"; TxtMaskRight.Foreground = Brushes.White; TxtMaskRight.IsVisible = rightVal > 10; }

                    // Middle: Vibrant Red (Cut)
                    TrimMaskActive.Background = new SolidColorBrush(Color.Parse("#EF4444"));
                    if (TxtMaskActive != null) { TxtMaskActive.Text = "✂ ВИДАЛЯЄТЬСЯ"; TxtMaskActive.Foreground = Brushes.White; TxtMaskActive.IsVisible = activeVal > 12; }
                }
            }
        }

        private void BtnTrimModeKeep_Click(object? sender, RoutedEventArgs e) => SetTrimMode(false);
        private void BtnTrimModeCut_Click(object? sender, RoutedEventArgs e) => SetTrimMode(true);

        private void SetTrimMode(bool isCutout)
        {
            try
            {
                _isCutoutMode = isCutout;
                bool isDark = this.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

                if (IconTrimHeader != null && TxtTrimHeader != null)
                {
                    if (!_isCutoutMode)
                    {
                        IconTrimHeader.Kind = Material.Icons.MaterialIconKind.ArrowCollapseHorizontal;
                        IconTrimHeader.Foreground = new SolidColorBrush(Color.Parse("#10B981"));
                        TxtTrimHeader.Text = "Режим обрізки (Залишити діапазон)";
                        TxtTrimHeader.Foreground = new SolidColorBrush(Color.Parse("#10B981"));

                        // Mode Segmented Buttons
                        if (BtnTrimModeKeep != null)
                        {
                            BtnTrimModeKeep.Background = new SolidColorBrush(Color.Parse("#10B981"));
                            if (IconTrimModeKeep != null) IconTrimModeKeep.Foreground = Brushes.White;
                            if (TxtTrimModeKeep != null) { TxtTrimModeKeep.FontWeight = FontWeight.Bold; TxtTrimModeKeep.Foreground = Brushes.White; }
                        }
                        if (BtnTrimModeCut != null)
                        {
                            BtnTrimModeCut.Background = Brushes.Transparent;
                            if (IconTrimModeCut != null) IconTrimModeCut.Foreground = InactiveTrimTextBrush;
                            if (TxtTrimModeCut != null) { TxtTrimModeCut.FontWeight = FontWeight.SemiBold; TxtTrimModeCut.Foreground = InactiveTrimTextBrush; }
                        }

                        // Badges
                        if (TxtTrimStartLabel != null) TxtTrimStartLabel.Text = "Початок:";
                        if (TxtTrimDurationLabel != null) TxtTrimDurationLabel.Text = "Буде збережено:";
                        if (TxtTrimEndLabel != null) TxtTrimEndLabel.Text = "Кінець:";

                        // Sliders
                        if (TxtTrimStartRowHeader != null) { TxtTrimStartRowHeader.Text = "Початок (звідки залишити)"; TxtTrimStartRowHeader.Foreground = new SolidColorBrush(Color.Parse("#10B981")); }
                        if (TrimStartSlider != null) TrimStartSlider.Foreground = new SolidColorBrush(Color.Parse("#10B981"));
                        if (TxtTrimEndRowHeader != null) { TxtTrimEndRowHeader.Text = "Кінець (доки залишити)"; TxtTrimEndRowHeader.Foreground = new SolidColorBrush(Color.Parse("#10B981")); }
                        if (TrimEndSlider != null) TrimEndSlider.Foreground = new SolidColorBrush(Color.Parse("#10B981"));

                        if (TxtPreviewTrim != null) TxtPreviewTrim.Text = "Прев'ю збереженого";
                        if (TxtConfirmTrim != null) TxtConfirmTrim.Text = "Зберегти виділене";
                        if (IconConfirmTrim != null) { IconConfirmTrim.Kind = Material.Icons.MaterialIconKind.Check; IconConfirmTrim.Foreground = Brushes.White; }
                        if (TxtBtnSave != null) TxtBtnSave.Text = "Обрізати та зберегти";

                        // Status Banner Styling (Compact & calm)
                        if (TrimStatusBanner != null)
                        {
                            TrimStatusBanner.Background = isDark ? new SolidColorBrush(Color.Parse("#1E293B")) : new SolidColorBrush(Color.Parse("#F8FAFC"));
                            TrimStatusBanner.BorderBrush = new SolidColorBrush(Color.Parse("#FCA5A5"));
                        }
                        if (IconTrimStatusBanner != null)
                        {
                            IconTrimStatusBanner.Kind = Material.Icons.MaterialIconKind.ContentCut;
                            IconTrimStatusBanner.Foreground = new SolidColorBrush(Color.Parse("#EF4444"));
                        }
                        if (TxtTrimStatusBanner != null)
                        {
                            TxtTrimStatusBanner.Foreground = isDark ? new SolidColorBrush(Color.Parse("#FEF2F2")) : new SolidColorBrush(Color.Parse("#334155"));
                        }
                    }
                }

                UpdateTrimHighlight();
                UpdateTrimTimeLabels();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SetTrimMode error: {ex}");
            }
        }

        private void UpdateTrimTimeLabels()
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0) return;

            long totalMs = _mediaPlayer.Length;
            long startMs = (long)(TrimStartSlider.Value / 100.0 * totalMs);
            long endMs = (long)(TrimEndSlider.Value / 100.0 * totalMs);
            long durationMs = Math.Max(0, endMs - startMs);

            string startStr = FormatTimeWithHundredths(startMs);
            string endStr = FormatTimeWithHundredths(endMs);
            string durationStr = FormatTimeWithHundredths(durationMs);
            string totalStr = FormatTimeWithHundredths(totalMs);

            if (TxtTrimStartBadge != null) TxtTrimStartBadge.Text = startStr;
            if (TxtTrimStartVal != null) TxtTrimStartVal.Text = startStr;

            if (TxtTrimEndBadge != null) TxtTrimEndBadge.Text = endStr;
            if (TxtTrimEndVal != null) TxtTrimEndVal.Text = endStr;

            if (TxtTrimDurationBadge != null) TxtTrimDurationBadge.Text = durationStr;

            if (TxtTrimStatusBanner != null)
            {
                if (!_isCutoutMode)
                {
                    TxtTrimStatusBanner.Text = $"✓ ЗБЕРЕЖЕТЬСЯ: відрізок [{startStr} – {endStr}] (тривалість {durationStr}). Початок (00:00.00–{startStr}) та кінець ({endStr}–{totalStr}) буде обрізано.";
                }
                else
                {
                    long keptMs = Math.Max(0, totalMs - durationMs);
                    string keptStr = FormatTimeWithHundredths(keptMs);
                    TxtTrimStatusBanner.Text = $"✂ ВИДАЛЯЄТЬСЯ: шматок [{startStr} – {endStr}] (-{durationStr}). Частини [00:00.00–{startStr}] та [{endStr}–{totalStr}] буде об'єднано разом (залишиться {keptStr}).";
                }
            }
        }

        private void TrimSlider_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            try
            {
                if (!_isVideoLoaded || _isClosedCleanly || _isProgrammaticTrimChange) return;

                if (e.Property.Name == "Value" && e.NewValue is double)
                {
                    _isProgrammaticTrimChange = true;
                    try
                    {
                        // Enforce minimum gap between start and end (min 100ms or 0.1%)
                        double minGapPercent = (_mediaPlayer != null && _mediaPlayer.Length > 0) ? (100.0 / _mediaPlayer.Length * 100.0) : 0.2;
                        if (minGapPercent < 0.1) minGapPercent = 0.1;
                        if (TrimStartSlider.Value >= TrimEndSlider.Value - minGapPercent)
                        {
                            if (ReferenceEquals(sender, TrimStartSlider))
                                TrimStartSlider.Value = Math.Max(0, TrimEndSlider.Value - minGapPercent);
                            else
                                TrimEndSlider.Value = Math.Min(100, TrimStartSlider.Value + minGapPercent);
                        }

                        UpdateTrimHighlight();
                        UpdateTrimTimeLabels();

                        if (_mediaPlayer != null && _mediaPlayer.Length > 0)
                        {
                            double activeValue = ReferenceEquals(sender, TrimEndSlider) ? TrimEndSlider.Value : TrimStartSlider.Value;
                            long previewTimeMs = (long)(activeValue / 100.0 * _mediaPlayer.Length);

                            _lastTrimChangeTime = DateTime.Now;
                            _lastTrimValue = activeValue;
                            _trimSeekPending = true;

                            VideoSlider.Value = activeValue;
                            TxtCurrentTime.Text = FormatTimeWithHundredths(previewTimeMs);
                        }
                    }
                    finally
                    {
                        _isProgrammaticTrimChange = false;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"TrimSlider error: {ex}");
            }
        }

        private void StepTrimStart(long deltaMs)
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0) return;
            long currentStartMs = (long)(TrimStartSlider.Value / 100.0 * _mediaPlayer.Length);
            long maxAllowed = (long)(TrimEndSlider.Value / 100.0 * _mediaPlayer.Length) - 100;
            long newStartMs = Math.Clamp(currentStartMs + deltaMs, 0, Math.Max(0, maxAllowed));
            TrimStartSlider.Value = (double)newStartMs / _mediaPlayer.Length * 100.0;
        }

        private void StepTrimEnd(long deltaMs)
        {
            if (_mediaPlayer == null || _mediaPlayer.Length <= 0) return;
            long currentEndMs = (long)(TrimEndSlider.Value / 100.0 * _mediaPlayer.Length);
            long minAllowed = (long)(TrimStartSlider.Value / 100.0 * _mediaPlayer.Length) + 100;
            long newEndMs = Math.Clamp(currentEndMs + deltaMs, Math.Min(_mediaPlayer.Length, minAllowed), _mediaPlayer.Length);
            TrimEndSlider.Value = (double)newEndMs / _mediaPlayer.Length * 100.0;
        }

        private void BtnTrimStartMinus_Click(object? sender, RoutedEventArgs e) => StepTrimStart(-500);
        private void BtnTrimStartPlus_Click(object? sender, RoutedEventArgs e) => StepTrimStart(500);
        private void BtnTrimStartMinus100_Click(object? sender, RoutedEventArgs e) => StepTrimStart(-100);
        private void BtnTrimStartPlus100_Click(object? sender, RoutedEventArgs e) => StepTrimStart(100);

        private void BtnTrimEndMinus_Click(object? sender, RoutedEventArgs e) => StepTrimEnd(-500);
        private void BtnTrimEndPlus_Click(object? sender, RoutedEventArgs e) => StepTrimEnd(500);
        private void BtnTrimEndMinus100_Click(object? sender, RoutedEventArgs e) => StepTrimEnd(-100);
        private void BtnTrimEndPlus100_Click(object? sender, RoutedEventArgs e) => StepTrimEnd(100);

        private void BtnRewind5_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer != null && _mediaPlayer.Length > 0)
            {
                EnsurePlayerReadyForPlayback();
                long newTime = Math.Max(0, _mediaPlayer.Time - 5000);
                _mediaPlayer.Time = newTime;
                VideoSlider.Value = (double)newTime / _mediaPlayer.Length * 100.0;
                TxtCurrentTime.Text = FormatTimeWithHundredths(newTime);
            }
        }

        private void BtnForward5_Click(object? sender, RoutedEventArgs e)
        {
            if (_mediaPlayer != null && _mediaPlayer.Length > 0)
            {
                EnsurePlayerReadyForPlayback();
                long newTime = Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 5000);
                _mediaPlayer.Time = newTime;
                VideoSlider.Value = (double)newTime / _mediaPlayer.Length * 100.0;
                TxtCurrentTime.Text = FormatTimeWithHundredths(newTime);
            }
        }

        private void BtnPreviewTrim_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isVideoLoaded || _mediaPlayer == null || _mediaPlayer.Length <= 0) return;

                EnsurePlayerReadyForPlayback();

                long startMs = (long)(TrimStartSlider.Value / 100.0 * _mediaPlayer.Length);
                long endMs = (long)(TrimEndSlider.Value / 100.0 * _mediaPlayer.Length);

                if (!_isCutoutMode)
                {
                    _mediaPlayer.Time = startMs;
                }
                else
                {
                    // For cutout preview, seek 1.5 seconds before the cut if possible to preview the splice transition
                    long previewStart = Math.Max(0, startMs - 1500);
                    _mediaPlayer.Time = previewStart;
                }

                _mediaPlayer.Play();
                MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Pause;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PreviewTrim error: {ex}");
            }
        }

        private void BtnPlayPause_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                if (!_isVideoLoaded || _mediaPlayer == null) return;

                if (_mediaPlayer.IsPlaying)
                {
                    _mediaPlayer.Pause();
                    MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;
                }
                else
                {
                    EnsurePlayerReadyForPlayback();

                    long total = _mediaPlayer.Length;
                    long current = _mediaPlayer.Time;

                    if (BtnTrimMode.IsChecked == true)
                    {
                        long startMs = (long)(TrimStartSlider.Value / 100.0 * total);
                        long endMs = (long)(TrimEndSlider.Value / 100.0 * total);
                        if (current >= endMs - 200 || current < startMs)
                        {
                            _mediaPlayer.Time = startMs;
                            VideoSlider.Value = (double)startMs / total * 100.0;
                            TxtCurrentTime.Text = FormatTimeWithHundredths(startMs);
                        }
                    }
                    else
                    {
                        if (current >= total - 250 || current < 0)
                        {
                            _mediaPlayer.Time = 0;
                            VideoSlider.Value = 0;
                            TxtCurrentTime.Text = "00:00.00";
                        }
                    }

                    _mediaPlayer.Play();
                    MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Pause;
                }
            }
            catch (Exception ex)
            {
                _ = ShowErrorDialog("Помилка відтворення (BtnPlayPause)", ex);
            }
        }

        private void BtnTrimMode_IsCheckedChanged(object? sender, RoutedEventArgs e)
        {
            try
            {
                bool isTrim = BtnTrimMode.IsChecked == true;
                TrimPanel.IsVisible = isTrim;
                TrimHighlightGrid.IsVisible = isTrim;
                bool isDark = this.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark;

                if (isTrim)
                {
                    BtnTrimMode.Background = isDark ? new SolidColorBrush(Color.Parse("#78350F")) : new SolidColorBrush(Color.Parse("#FEF3C7"));
                    BtnTrimMode.BorderBrush = new SolidColorBrush(Color.Parse("#F59E0B"));
                    BtnTrimMode.BorderThickness = new Thickness(1.5);
                    if (TxtBtnTrimMode != null)
                    {
                        TxtBtnTrimMode.Text = "Обрізка: Увімкнено";
                        TxtBtnTrimMode.Foreground = isDark ? new SolidColorBrush(Color.Parse("#FCD34D")) : new SolidColorBrush(Color.Parse("#B45309"));
                    }

                    if (TimelineTrackBorder != null) TimelineTrackBorder.Height = 24;
                    SetVideoSliderTrackVisibility(false);

                    SetTrimMode(false);
                    ToolTip.SetTip(BtnSave, "Обрізати та зберегти відео");
                }
                else
                {
                    BtnTrimMode.Background = Brushes.Transparent;
                    BtnTrimMode.BorderBrush = Brushes.Transparent;
                    BtnTrimMode.BorderThickness = new Thickness(0);
                    if (TxtBtnTrimMode != null)
                    {
                        TxtBtnTrimMode.Text = "Обрізка";
                        TxtBtnTrimMode.Foreground = null;
                    }

                    if (TimelineTrackBorder != null) TimelineTrackBorder.Height = 8;
                    SetVideoSliderTrackVisibility(true);

                    TxtBtnSave.Text = "Зберегти";
                    ToolTip.SetTip(BtnSave, "Зберегти відео");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"BtnTrimMode_IsCheckedChanged error: {ex}");
            }
        }

        private void BtnCancelTrim_Click(object? sender, RoutedEventArgs e)
        {
            _isProgrammaticTrimChange = true;
            TrimStartSlider.Value = 0;
            TrimEndSlider.Value = 100;
            BtnTrimMode.IsChecked = false;
            _isProgrammaticTrimChange = false;
            SetTrimMode(false);
        }

        private async void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            _isVideoKept = true;

            BtnSave.IsEnabled = false;
            BtnUpload.IsEnabled = false;

            await Task.Delay(100);

            if (BtnTrimMode.IsChecked == true && (TrimStartSlider.Value > 0.05 || TrimEndSlider.Value < 99.95))
            {
                ShowToast("Обрізка відео...");
                if (_mediaPlayer == null)
                {
                    BtnSave.IsEnabled = true;
                    BtnUpload.IsEnabled = true;
                    return;
                }

                long totalDurationMs = _mediaPlayer.Length;
                long startMs = (long)(TrimStartSlider.Value / 100.0 * totalDurationMs);
                long endMs = (long)(TrimEndSlider.Value / 100.0 * totalDurationMs);
                long durationMs = endMs - startMs;

                string directory = Path.GetDirectoryName(_sourceFile) ?? "";
                string ext = Path.GetExtension(_sourceFile);
                string name = Path.GetFileNameWithoutExtension(_sourceFile);
                string outputPath = Path.Combine(directory, $"{name}_trimmed_{DateTime.Now:HHmmssfff}{ext}");

                // Detach media cleanly before running FFmpeg so VLC releases any file locks
                _timer?.Stop();
                _isVideoLoaded = false;
                try
                {
                    if (_mediaPlayer.IsPlaying) _mediaPlayer.Stop();
                    var oldMedia = _mediaPlayer.Media;
                    _mediaPlayer.Media = null;
                    oldMedia?.Dispose();
                }
                catch (Exception exDetach)
                {
                    Debug.WriteLine($"VLC detach error: {exDetach.Message}");
                }

                await Task.Delay(150);

                try
                {
                    await Task.Run(async () =>
                    {
                        await Task.Delay(100);

                        string exeName = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
                        string ffmpegExecutable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, exeName);

                        if (!File.Exists(ffmpegExecutable))
                        {
                            if (File.Exists(exeName))
                            {
                                ffmpegExecutable = Path.GetFullPath(exeName);
                            }
                            else
                            {
                                await FFmpegDownloader.GetLatestVersion(FFmpegVersion.Official);
                                if (File.Exists(exeName)) ffmpegExecutable = Path.GetFullPath(exeName);
                            }
                        }

                        string args;
                        if (!_isCutoutMode)
                        {
                            string startStr = TimeSpan.FromMilliseconds(startMs).ToString(@"hh\:mm\:ss\.fff");
                            string durationStr = TimeSpan.FromMilliseconds(durationMs).ToString(@"hh\:mm\:ss\.fff");
                            args = $"-y -i \"{_sourceFile}\" -ss {startStr} -t {durationStr} -c copy \"{outputPath}\"";
                        }
                        else
                        {
                            // Cutout mode
                            if (startMs <= 200)
                            {
                                long remDuration = totalDurationMs - endMs;
                                string startStr = TimeSpan.FromMilliseconds(endMs).ToString(@"hh\:mm\:ss\.fff");
                                string durStr = TimeSpan.FromMilliseconds(remDuration).ToString(@"hh\:mm\:ss\.fff");
                                args = $"-y -i \"{_sourceFile}\" -ss {startStr} -t {durStr} -c copy \"{outputPath}\"";
                            }
                            else if (endMs >= totalDurationMs - 200)
                            {
                                string durStr = TimeSpan.FromMilliseconds(startMs).ToString(@"hh\:mm\:ss\.fff");
                                args = $"-y -i \"{_sourceFile}\" -ss 00:00:00.000 -t {durStr} -c copy \"{outputPath}\"";
                            }
                            else
                            {
                                // Detect audio stream to build safe concat filter
                                bool hasAudio = false;
                                try
                                {
                                    var probeInfo = new ProcessStartInfo
                                    {
                                        FileName = ffmpegExecutable,
                                        Arguments = $"-i \"{_sourceFile}\"",
                                        RedirectStandardError = true,
                                        UseShellExecute = false,
                                        CreateNoWindow = true
                                    };
                                    using (var probeProc = Process.Start(probeInfo))
                                    {
                                        if (probeProc != null)
                                        {
                                            string probeOut = await probeProc.StandardError.ReadToEndAsync();
                                            await probeProc.WaitForExitAsync();
                                            hasAudio = probeOut.Contains("Audio:", StringComparison.OrdinalIgnoreCase);
                                        }
                                    }
                                }
                                catch (Exception exProbe)
                                {
                                    Debug.WriteLine($"Audio probe failed: {exProbe.Message}");
                                }

                                string s1 = (startMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                                string s2 = (endMs / 1000.0).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

                                if (hasAudio)
                                {
                                    string filter = $"[0:v]trim=end={s1},setpts=PTS-STARTPTS[v1]; [0:v]trim=start={s2},setpts=PTS-STARTPTS[v2]; [0:a]atrim=end={s1},asetpts=PTS-STARTPTS[a1]; [0:a]atrim=start={s2},asetpts=PTS-STARTPTS[a2]; [v1][a1][v2][a2]concat=n=2:v=1:a=1[v][a]";
                                    args = $"-y -i \"{_sourceFile}\" -filter_complex \"{filter}\" -map \"[v]\" -map \"[a]\" -c:v libx264 -preset veryfast -crf 18 -c:a aac \"{outputPath}\"";
                                }
                                else
                                {
                                    string filter = $"[0:v]trim=end={s1},setpts=PTS-STARTPTS[v1]; [0:v]trim=start={s2},setpts=PTS-STARTPTS[v2]; [v1][v2]concat=n=2:v=1:a=0[v]";
                                    args = $"-y -i \"{_sourceFile}\" -filter_complex \"{filter}\" -map \"[v]\" -c:v libx264 -preset veryfast -crf 18 \"{outputPath}\"";
                                }
                            }
                        }

                        var processInfo = new ProcessStartInfo
                        {
                            FileName = ffmpegExecutable,
                            Arguments = args,
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };

                        using (var process = Process.Start(processInfo))
                        {
                            if (process != null) await process.WaitForExitAsync();
                            if (process == null || process.ExitCode != 0)
                            {
                                throw new InvalidOperationException($"FFmpeg повернув помилку {process?.ExitCode}");
                            }
                        }

                        // Replace _sourceFile with retry policy for file lock release
                        bool replaced = false;
                        for (int retry = 0; retry < 10; retry++)
                        {
                            try
                            {
                                if (File.Exists(_sourceFile)) File.Delete(_sourceFile);
                                File.Move(outputPath, _sourceFile);
                                replaced = true;
                                break;
                            }
                            catch (IOException) when (retry < 9)
                            {
                                await Task.Delay(150);
                            }
                        }

                        if (!replaced)
                        {
                            throw new IOException("Не вдалося оновити файл після обрізки: файл заблоковано системою.");
                        }
                    });

                    // Seamlessly reload the new trimmed video back into the player!
                    if (_libVLC != null && _mediaPlayer != null)
                    {
                        var newMedia = new LibVLCSharp.Shared.Media(_libVLC, _sourceFile, FromType.FromPath);
                        _mediaPlayer.Media = newMedia;
                        _mediaPlayer.Play();
                        await Task.Delay(300);
                        _mediaPlayer.Pause();
                        _mediaPlayer.Time = 0;
                        MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;

                        _isVideoLoaded = true;

                        _isProgrammaticTrimChange = true;
                        TrimStartSlider.Value = 0;
                        TrimEndSlider.Value = 100;
                        VideoSlider.Value = 0;
                        _isProgrammaticTrimChange = false;

                        UpdateTrimHighlight();
                        UpdateTrimTimeLabels();

                        _timer?.Start();

                        ShowToast("Обрізку застосовано! Продовжуйте редагування або збережіть.");
                    }
                }
                catch (Exception ex)
                {
                    await ShowErrorDialog("Помилка збереження обрізки", ex);
                    try
                    {
                        if (_libVLC != null && _mediaPlayer != null && File.Exists(_sourceFile))
                        {
                            _mediaPlayer.Media = new LibVLCSharp.Shared.Media(_libVLC, _sourceFile, FromType.FromPath);
                            _isVideoLoaded = true;
                            _timer?.Start();
                        }
                    }
                    catch { }
                }
                finally
                {
                    BtnSave.IsEnabled = true;
                    BtnUpload.IsEnabled = true;
                }
            }
            else
            {
                // When trim sliders are not moved, "Зберегти" saves/finalizes and closes the editor
                ShowToast("Відео збережено!");
                CleanUpVlcBackground();
                await Task.Delay(400);
                this.Close();
            }
        }

        private async void BtnUpload_Click(object? sender, RoutedEventArgs e)
        {
            _isVideoKept = true;

            BtnSave.IsEnabled = false;
            BtnUpload.IsEnabled = false;

            ShowToast("Підготовка до завантаження...");

            await Task.Delay(150);

            CleanUpVlcBackground();

            ShowToast("Завантаження на Google Диск...");
            try
            {
                string link = await GoogleDriveManager.UploadVideoAsync(_sourceFile);

                if (!string.IsNullOrEmpty(link))
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null) await clipboard.SetTextAsync(link);
                    ShowToast("Посилання скопійовано в буфер!");

                    await Task.Delay(1000);
                    this.Close();
                }
                else
                {
                    ShowToast("Помилка: Посилання пусте");
                    BtnSave.IsEnabled = true;
                    BtnUpload.IsEnabled = true;
                }
            }
            catch (Exception ex)
            {
                await ShowErrorDialog("Помилка завантаження на Google Диск", ex);
                ShowToast("Помилка завантаження");
                BtnSave.IsEnabled = true;
                BtnUpload.IsEnabled = true;
            }
        }

        private void ShowToast(string message)
        {
            ToastText.Text = message;
            ToastNotification.IsVisible = true;
            DispatcherTimer.RunOnce(() =>
            {
                if (ToastNotification != null) ToastNotification.IsVisible = false;
            }, TimeSpan.FromSeconds(3));
        }

        private void ReturnToHistoryIfNeeded()
        {
            if (_returnedToHistory) return;
            _returnedToHistory = true;
            _historyOwner?.ReturnFromEditor(_sourceHistoryItem, isVideo: true);
        }

        private void BtnBackToHistory_Click(object? sender, RoutedEventArgs e)
        {
            CleanUpVlcBackground();
            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            CleanUpVlcBackground();
            ReturnToHistoryIfNeeded();

            if (!string.IsNullOrEmpty(_backupOriginalFile))
            {
                try { if (File.Exists(_backupOriginalFile)) File.Delete(_backupOriginalFile); }
                catch { }
            }

            if (_isNewRecording && !_isVideoKept)
            {
                string pathToDelete = _sourceFile;
                Task.Run(async () =>
                {
                    await Task.Delay(500);
                    try { if (File.Exists(pathToDelete)) File.Delete(pathToDelete); }
                    catch (Exception ex) { Debug.WriteLine($"Не вдалося видалити тимчасове відео: {ex.Message}"); }
                });
            }
        }
    }
}
