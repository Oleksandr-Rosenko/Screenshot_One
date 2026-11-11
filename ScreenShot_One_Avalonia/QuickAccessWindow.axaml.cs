using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Плаваюча мініатюра після знімка (як Quick Access Overlay у CleanShot X):
    /// копіювати, зберегти, редагувати або перетягнути файл у будь-яку програму, не відкриваючи редактор.
    /// Кілька мініатюр складаються стовпчиком у правому нижньому куті. Закрита мініатюра потрапляє в історію.
    /// </summary>
    public partial class QuickAccessWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);
        private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

        private const double ScreenMargin = 16;
        private const double StackSpacing = 8;

        // Відкриті мініатюри: остання — найнижча
        private static readonly List<QuickAccessWindow> _open = new List<QuickAccessWindow>();

        private readonly Bitmap _bitmap;
        private readonly DispatcherTimer _autoCloseTimer;
        private int _secondsLeft;
        private bool _pointerOver;
        private bool _handedToEditor;   // бітмап передано редактору — він сам зберігає й звільняє
        private bool _savedToFile;
        private PointerPressedEventArgs? _pressArgs;
        private Point _pressPoint;
        private bool _dragging;

        // Для дизайнера XAML
        public QuickAccessWindow() : this(new WriteableBitmap(new PixelSize(1, 1), BitmapPixels.Dpi96)) { }

        public QuickAccessWindow(Bitmap bitmap)
        {
            InitializeComponent();
            _bitmap = bitmap;
            ThumbImage.Source = bitmap;

            _secondsLeft = Math.Max(0, SettingsManager.Current.QuickAccessAutoCloseSeconds);
            _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _autoCloseTimer.Tick += AutoCloseTimer_Tick;

            RootBorder.PointerEntered += (s, e) => _pointerOver = true;
            RootBorder.PointerExited += (s, e) => _pointerOver = false;

            ThumbBorder.PointerPressed += Thumb_PointerPressed;
            ThumbBorder.PointerMoved += Thumb_PointerMoved;
            ThumbBorder.PointerReleased += (s, e) => _pressArgs = null;
            ThumbBorder.DoubleTapped += (s, e) => OpenEditor();

            KeyDown += (s, e) => { if (e.Key == Key.Escape) Close(); };
        }

        /// <summary>Показує мініатюру для щойно зробленого знімка (вікно стає власником бітмапа).</summary>
        public static void ShowFor(Bitmap bitmap)
        {
            var win = new QuickAccessWindow(bitmap);
            _open.Add(win);
            win.Show();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);

            // Windows: мініатюра не потрапляє в наступні скріншоти та відеозаписи
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var handle = TryGetPlatformHandle()?.Handle;
                    if (handle.HasValue) SetWindowDisplayAffinity(handle.Value, WDA_EXCLUDEFROMCAPTURE);
                }
                catch (Exception ex) { AppLog.Warn(ex); }
            }

            // Розмір відомий лише після розмітки
            Dispatcher.UIThread.Post(Reflow, DispatcherPriority.Loaded);

            if (_secondsLeft > 0) _autoCloseTimer.Start();

            if (SettingsManager.Current.CopyToClipboardOnOpen)
                CopyAsync(showStatus: false).FireAndForget("Мініатюра: автокопіювання");
        }

        protected override void OnClosed(EventArgs e)
        {
            _autoCloseTimer.Stop();
            _open.Remove(this);
            Dispatcher.UIThread.Post(Reflow, DispatcherPriority.Loaded);

            if (!_handedToEditor)
            {
                ThumbImage.Source = null;

                if (SettingsManager.Current.AutoSaveToFolder && !_savedToFile)
                {
                    try { ImageExport.SaveToFolder(_bitmap); }
                    catch (Exception ex) { AppLog.Error(ex, "Мініатюра: автозбереження"); }
                }

                // Знімок без анотацій — в історію (у фоні); бітмап звільниться після запису
                try
                {
                    string id = HistoryManager.ReserveId(null);
                    HistoryManager.SaveScreenshotInBackground(id, true, _bitmap, _bitmap, new List<ShapeData>(), _bitmap)
                        .FireAndForget("Мініатюра: збереження в історію");
                }
                catch (Exception ex)
                {
                    AppLog.Error(ex, "Мініатюра: збереження в історію");
                    _bitmap.Dispose();
                }
            }

            base.OnClosed(e);
        }

        /// <summary>Розставляє всі відкриті мініатюри стовпчиком від правого нижнього кута.</summary>
        private static void Reflow()
        {
            if (_open.Count == 0) return;

            var first = _open[_open.Count - 1];
            var screen = first.Screens.ScreenFromPoint(ScreenService.GetCursorPosition())
                         ?? first.Screens.Primary;
            if (screen == null) return;

            var area = screen.WorkingArea;
            double scaling = screen.Scaling;
            double bottom = area.Bottom - ScreenMargin * scaling;

            for (int i = _open.Count - 1; i >= 0; i--)
            {
                var w = _open[i];
                double width = w.Bounds.Width * scaling;
                double height = w.Bounds.Height * scaling;
                if (width <= 0 || height <= 0) continue;

                bottom -= height;
                w.Position = new PixelPoint((int)(area.Right - ScreenMargin * scaling - width), (int)bottom);
                bottom -= StackSpacing * scaling;
            }
        }

        private void AutoCloseTimer_Tick(object? sender, EventArgs e)
        {
            if (_pointerOver || _dragging) return; // поки користувач з нею працює — не ховаємо
            _secondsLeft--;
            if (_secondsLeft <= 0) Close();
        }

        private void OpenEditor()
        {
            if (_handedToEditor) return;
            _handedToEditor = true;
            ThumbImage.Source = null;
            new EditorWindow(_bitmap).Show();
            Close();
        }

        private async Task CopyAsync(bool showStatus)
        {
            await ClipboardHelper.CopyImageAsync(_bitmap, TopLevel.GetTopLevel(this));
            if (showStatus) ShowStatus("Скопійовано в буфер");
        }

        private void ShowStatus(string text)
        {
            TxtStatus.Text = text;
            TxtStatus.IsVisible = true;
            Dispatcher.UIThread.Post(Reflow, DispatcherPriority.Loaded);
        }

        private void BtnEdit_Click(object? sender, RoutedEventArgs e) => OpenEditor();

        private async void BtnCopy_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                await CopyAsync(showStatus: true);
                await Task.Delay(700);
                Close();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Мініатюра: копіювання");
                ShowStatus("Не вдалося скопіювати");
            }
        }

        private async void BtnSaveFile_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                string path = ImageExport.SaveToFolder(_bitmap);
                _savedToFile = true;
                ShowStatus("Збережено: " + System.IO.Path.GetFileName(path));
                await Task.Delay(1200);
                Close();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Мініатюра: збереження у файл");
                ShowStatus("Не вдалося зберегти");
            }
        }

        private void BtnDismiss_Click(object? sender, RoutedEventArgs e) => Close();

        // ---- Перетягування файлу в інші програми (Telegram, Slack, пошта, провідник…) ----

        private void Thumb_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(ThumbBorder).Properties.IsLeftButtonPressed) return;
            _pressArgs = e;
            _pressPoint = e.GetPosition(this);
        }

        private async void Thumb_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (_pressArgs == null || _dragging) return;
            var delta = e.GetPosition(this) - _pressPoint;
            if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;

            var pressArgs = _pressArgs;
            _pressArgs = null;
            _dragging = true;
            try
            {
                string path = ImageExport.SaveTemp(_bitmap);
                var file = await StorageProvider.TryGetFileFromPathAsync(new Uri(path));
                if (file == null) return;

                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateFile(file));
                var result = await DragDrop.DoDragDropAsync(pressArgs, data, DragDropEffects.Copy);
                if (result != DragDropEffects.None) Close();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Мініатюра: перетягування");
            }
            finally
            {
                _dragging = false;
            }
        }
    }
}
