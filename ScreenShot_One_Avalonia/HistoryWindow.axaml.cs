using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media.Imaging;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using SukiUI.Controls;

namespace ScreenShot_One_Avalonia
{
    public class HistoryItem : INotifyPropertyChanged
    {
        public string Path { get; set; } = "";
        public string FileName { get; set; } = "";
        public string DateString { get; set; } = "";
        // 2.7.0: «Сьогодні, 13:04» і «1200 × 700 · 245 КБ» замість технічного імені файлу
        public string Title { get; set; } = "";
        private string _details = "";
        public string Details
        {
            get => _details;
            set
            {
                _details = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Details)));
            }
        }
        public bool IsVideo { get; set; }

        private static readonly string[] MonthsGenitive =
            { "січня", "лютого", "березня", "квітня", "травня", "червня", "липня", "серпня", "вересня", "жовтня", "листопада", "грудня" };

        /// <summary>Людська дата: «Сьогодні, 13:04», «Вчора, 18:20», «11 листопада, 09:15», «3 березня 2025, 10:00».</summary>
        public static string FriendlyDate(DateTime time, DateTime now)
        {
            string hm = time.ToString("HH:mm");
            if (time.Date == now.Date) return $"Сьогодні, {hm}";
            if (time.Date == now.Date.AddDays(-1)) return $"Вчора, {hm}";
            string day = $"{time.Day} {MonthsGenitive[time.Month - 1]}";
            return time.Year == now.Year ? $"{day}, {hm}" : $"{day} {time.Year}, {hm}";
        }

        /// <summary>«245 КБ», «2,4 МБ».</summary>
        public static string FriendlySize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} Б";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0} КБ";
            return $"{bytes / (1024.0 * 1024.0):0.#} МБ".Replace('.', ',');
        }

        /// <summary>Розмір PNG без декодування — з заголовка IHDR (байти 16–23).</summary>
        public static (int Width, int Height)? ReadPngSize(string path)
        {
            try
            {
                using var fs = File.OpenRead(path);
                var header = new byte[24];
                if (fs.Read(header, 0, 24) < 24 || header[0] != 0x89 || header[1] != (byte)'P') return null;
                int w = (header[16] << 24) | (header[17] << 16) | (header[18] << 8) | header[19];
                int h = (header[20] << 24) | (header[21] << 16) | (header[22] << 8) | header[23];
                return (w, h);
            }
            catch { return null; }
        }

        /// <summary>Без відкриття файлу (швидко навіть для сотень записів); розміри PNG дочитуються разом із мініатюрою.</summary>
        public static HistoryItem FromFile(FileInfo file, bool isVideo)
        {
            string details = FriendlySize(file.Length);
            return new HistoryItem
            {
                Path = file.FullName,
                FileName = file.Name,
                DateString = file.CreationTime.ToString("dd.MM.yyyy HH:mm"),
                Title = FriendlyDate(file.CreationTime, DateTime.Now),
                Details = isVideo ? $"Відео · {details}" : details,
                IsVideo = isVideo,
                ImageSource = null
            };
        }

        private Bitmap? _imageSource;
        public Bitmap? ImageSource
        {
            get => _imageSource;
            set
            {
                _imageSource = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ImageSource)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public partial class HistoryWindow : SukiWindow
    {
        [ComImport]
        [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            void GetImage([In, MarshalAs(UnmanagedType.Struct)] NativeSize size, [In] int flags, [Out] out IntPtr phbm);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int cx;
            public int cy;
            public NativeSize(int cx, int cy) { this.cx = cx; this.cy = cy; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHCreateItemFromParsingName([MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc, [In] ref Guid riid, [Out, MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

        [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
        [return: MarshalAs(UnmanagedType.Bool)]

        private static extern bool DeleteObject([In] IntPtr hObject);

        private static Bitmap? GenerateVideoThumbnail(string filePath)

        {

            try

            {

                if (!OperatingSystem.IsWindows()) return null;
                if (!File.Exists(filePath)) return null;

                Guid uuid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

                int hresult = SHCreateItemFromParsingName(filePath, IntPtr.Zero, ref uuid, out IShellItemImageFactory factory);

                if (hresult == 0 && factory != null)

                {

                    factory.GetImage(new NativeSize(200, 120), 0x0, out IntPtr hBitmap);

                    if (hBitmap != IntPtr.Zero)
                    {
                        using (var sysBmp = System.Drawing.Image.FromHbitmap(hBitmap))
                        using (var ms = new MemoryStream())
                        {
                            sysBmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            ms.Position = 0;
                            var avaloniaBmp = new Bitmap(ms);
                            DeleteObject(hBitmap);
                            return avaloniaBmp;
                        }
                    }
                }
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }
            return null;

        }

        private List<HistoryItem> _screenshotItems = new List<HistoryItem>();
        // Показані картки (підвантажуються сторінками при прокрутці); прив'язані до сітки ОДИН раз
        private readonly System.Collections.ObjectModel.ObservableCollection<HistoryItem> _shownScreens = new();
        private readonly System.Collections.ObjectModel.ObservableCollection<HistoryItem> _shownVideos = new();
        private bool _isLoading = true;
        private const int PageSize = 40;

        private List<HistoryItem> _videoItems = new List<HistoryItem>();

        // Змінна для нашого нового рідного діалогового вікна

        private TaskCompletionSource<bool>? _dialogTcs;

                private bool _isOpeningVideoEditor = false;
        public HistoryWindow()

        {

            InitializeComponent();

            // Сітка карток по центру: ширина підганяється під ціле число колонок
            SizeChanged += (_, _) => UpdateGalleryLayout();

            ScreenshotsListBox.ItemsSource = _shownScreens;
            VideosListBox.ItemsSource = _shownVideos;
            // Наступна сторінка карток — коли до кінця лишається кілька рядів
            ScreenshotsListBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) => OnGalleryScrolled(e, isVideo: false));
            VideosListBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) => OnGalleryScrolled(e, isVideo: true));

            LoadHistory();

        }

        // Ширина «слота» однієї картки: 236 px картки + по 6 px відступу з кожного боку
        private const double CardSlotWidth = 248;
        // Поля сітки (Margin 12 з кожного боку) + запас під смугу прокрутки
        private const double GalleryChrome = 24 + 18;

        /// <summary>Скільки колонок вміщується і яка ширина сітки (публічне для тестів).</summary>
        public static (int Columns, double Width) ComputeGalleryLayout(double windowWidth)
        {
            int cols = Math.Max(1, (int)((windowWidth - GalleryChrome) / CardSlotWidth));
            return (cols, cols * CardSlotWidth);
        }

        /// <summary>
        /// Центрує сітку: панель отримує ширину рівно під N колонок (тоді неповний останній рядок
        /// вирівнюється по лівому краю сітки, а не «плаває»), шапка — ту саму ширину, щоб кнопка
        /// очищення стояла над правим краєм карток.
        /// </summary>
        private void UpdateGalleryLayout()
        {
            if (Bounds.Width <= 0) return;
            var (_, width) = ComputeGalleryLayout(Bounds.Width);

            if (HeaderRow != null)
            {
                HeaderRow.Width = width - 12; // вирівнювання по краях самих карток (без їхніх зовнішніх відступів)
                HeaderRow.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
            }
            foreach (var lb in new[] { ScreenshotsListBox, VideosListBox })
            {
                if (lb?.ItemsPanelRoot is Panel panel) panel.Width = width;
            }
        }

        private void Tab_Checked(object? sender, RoutedEventArgs e)

        {
            if (ScreenshotsListBox == null || VideosListBox == null) return;

            if (TabScreenshots.IsChecked == true)
            {
                ScreenshotsListBox.IsVisible = true;
                VideosListBox.IsVisible = false;
                ToolTip.SetTip(BtnClearAll, "Очистити скріншоти");
            }
            else
            {
                ScreenshotsListBox.IsVisible = false;
                VideosListBox.IsVisible = true;
                ToolTip.SetTip(BtnClearAll, "Очистити відео");
            }
            UpdatePlaceholders();
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateGalleryLayout, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private void UpdatePlaceholders()
        {
            if (EmptyScreenshotsText == null || EmptyVideosText == null) return;

            if (TabScreenshots.IsChecked == true)
            {
                EmptyScreenshotsText.IsVisible = !_isLoading && _screenshotItems.Count == 0;
                EmptyVideosText.IsVisible = false;
            }
            else
            {
                EmptyScreenshotsText.IsVisible = false;
                EmptyVideosText.IsVisible = !_isLoading && _videoItems.Count == 0;
            }
        }

        private int _loadHistoryVersion = 0;

        private void LoadHistory() => LoadHistoryAsync().FireAndForget("Завантаження історії");

        private async Task LoadHistoryAsync()
        {
            int currentVersion = ++_loadHistoryVersion;

            // Поки читаємо диск — не показуємо «тут порожньо» (раніше це висіло кілька секунд при кожному відкритті)
            _isLoading = true;
            UpdatePlaceholders();

            var (newScreenshots, newVideos) = await Task.Run(() =>
            {
                string appDataFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One");
                string appDataScreenshots = System.IO.Path.Combine(appDataFolder, "Screenshots");
                string docsFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Screenshot_One");
                string picsFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshot_One");

                var screenshotFiles = new List<string>();
                string[] imgFolders = { appDataFolder, appDataScreenshots, docsFolder, picsFolder };
                string[] imgExtensions = { "*.png", "*.jpg", "*.jpeg", "*.bmp" };

                foreach (var folder in imgFolders)
                {
                    if (Directory.Exists(folder))
                    {
                        foreach (var ext in imgExtensions)
                        {
                            try { screenshotFiles.AddRange(Directory.GetFiles(folder, ext)); } catch (Exception logEx) { AppLog.Warn(logEx); }
                        }
                    }
                }

                var screens = screenshotFiles.Distinct()
                                             .Where(f => !f.EndsWith("_original.png", StringComparison.OrdinalIgnoreCase))
                                             .Select(f => new FileInfo(f))
                                             .OrderByDescending(f => f.CreationTime)
                                             .Select(file => HistoryItem.FromFile(file, isVideo: false))
                                             .ToList();

                string videoFolder = System.IO.Path.Combine(appDataFolder, "Videos");
                var videoFiles = new List<string>();
                if (Directory.Exists(videoFolder))
                {
                    try { videoFiles.AddRange(Directory.GetFiles(videoFolder, "*.mp4")); } catch (Exception logEx) { AppLog.Warn(logEx); }
                }

                var vids = videoFiles.Select(f => new FileInfo(f))
                                     .OrderByDescending(f => f.CreationTime)
                                     .Select(file => HistoryItem.FromFile(file, isVideo: true))
                                     .ToList();

                return (screens, vids);
            });

            if (currentVersion != _loadHistoryVersion) return;

            _screenshotItems = newScreenshots;
            _videoItems = newVideos;
            // Показані колекції сповіщають сітку про кожну зміну — без перепривʼязки (WrapPanel падав на «тихих» мутаціях)
            _shownScreens.Clear();
            _shownVideos.Clear();
            AppendPage(isVideo: false);
            AppendPage(isVideo: true);
            _isLoading = false;

            UpdatePlaceholders();
            // Панель сітки створюється разом з елементами — підганяємо ширину після розмітки
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateGalleryLayout, Avalonia.Threading.DispatcherPriority.Loaded);
            // Мініатюри завантажує AppendPage — лише для показаних карток
        }

        /// <summary>
        /// Показує наступну сторінку карток і запускає для неї мініатюри.
        /// Раніше сітка створювала картки й декодувала мініатюри одразу для всіх записів (до 500) — вікно
        /// відкривалось із затримкою в кілька секунд.
        /// </summary>
        private void AppendPage(bool isVideo)
        {
            var all = isVideo ? _videoItems : _screenshotItems;
            var shown = isVideo ? _shownVideos : _shownScreens;
            if (shown.Count >= all.Count) return;

            var page = all.Skip(shown.Count).Take(PageSize).ToList();
            foreach (var item in page) shown.Add(item);
            LoadThumbnailsAsync(page).FireAndForget("Мініатюри історії");

            // На великому екрані перша сторінка може не заповнити вікно — тоді докидаємо ще одну
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                var lb = isVideo ? VideosListBox : ScreenshotsListBox;
                var sv = lb.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (sv != null && lb.IsVisible && sv.Extent.Height <= sv.Viewport.Height + 200) AppendPage(isVideo);
                UpdateGalleryLayout();
            }, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private void OnGalleryScrolled(ScrollChangedEventArgs e, bool isVideo)
        {
            if (e.Source is not ScrollViewer sv) return;
            if (sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 700) AppendPage(isVideo);
        }

        private Task LoadThumbnailsAsync(List<HistoryItem> items)
        {
            return Task.Run(() =>
            {
                Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 4 }, item =>
                {
                    try
                    {
                        if (item.IsVideo)
                        {
                            var vbmp = GenerateVideoThumbnail(item.Path);
                            if (vbmp != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => item.ImageSource = vbmp);
                            return;
                        }
                        if (!File.Exists(item.Path)) return;

                        string? details = null;
                        if (HistoryItem.ReadPngSize(item.Path) is { } size)
                            details = $"{size.Width} × {size.Height} · {HistoryItem.FriendlySize(new FileInfo(item.Path).Length)}";

                        using var fs = File.OpenRead(item.Path);
                        var bmp = Bitmap.DecodeToWidth(fs, 280);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            item.ImageSource = bmp;
                            if (details != null) item.Details = details;
                        });
                    }
                    catch (Exception logEx) { AppLog.Warn(logEx); }
                });
            });
        }

        // Динамічний Toast-повідомлення
        private void ShowToast(string message)
        {
            if (ToastText != null) ToastText.Text = message;
            if (ToastNotification != null) ToastNotification.IsVisible = true;

            DispatcherTimer.RunOnce(() =>
            {
                if (ToastNotification != null) ToastNotification.IsVisible = false;
            }, TimeSpan.FromSeconds(2));
        }

        // Красиві рідні діалогові вікна
        private async Task<bool> ShowCustomDialog(string title, string message, bool isYesNo)
        {
            DialogTitle.Text = title;
            DialogMessage.Text = message;

            BtnDialogYes.IsVisible = isYesNo;
            BtnDialogNo.IsVisible = isYesNo;
            BtnDialogOk.IsVisible = !isYesNo;

            DialogOverlay.IsVisible = true;
            _dialogTcs = new TaskCompletionSource<bool>();
            return await _dialogTcs.Task;
        }

        private void BtnDialogYes_Click(object? sender, RoutedEventArgs e) { DialogOverlay.IsVisible = false; _dialogTcs?.TrySetResult(true); }
        private void BtnDialogNo_Click(object? sender, RoutedEventArgs e) { DialogOverlay.IsVisible = false; _dialogTcs?.TrySetResult(false); }
        private void BtnDialogOk_Click(object? sender, RoutedEventArgs e) { DialogOverlay.IsVisible = false; _dialogTcs?.TrySetResult(true); }

        // Метод копіювання ТІЛЬКИ для скріншотів
        private async void BtnCopyFromHistory_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filePath)
            {
                try
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard == null) return;

                    var bitmap = new Bitmap(filePath);
                    await clipboard.SetBitmapAsync(bitmap);
                    ShowToast("Скріншот скопійовано!");
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog("Помилка", $"Не вдалося скопіювати: {ex.Message}", false);
                }
            }
        }

        private async void BtnOpenFolder_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filePath)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{filePath}\"");
                    else if (OperatingSystem.IsMacOS())
                        System.Diagnostics.Process.Start("open", $"-R \"{filePath}\"");
                    else if (OperatingSystem.IsLinux())
                    {
                        string? dir = System.IO.Path.GetDirectoryName(filePath);
                        if (!string.IsNullOrEmpty(dir))
                            System.Diagnostics.Process.Start("xdg-open", $"\"{dir}\"");
                    }
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog("Помилка", $"Не вдалося відкрити теку: {ex.Message}", false);
                }
            }
        }

        private async void BtnDeleteFromHistory_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filePath)
            {
                bool result = await ShowCustomDialog("Підтвердження", "Видалити цей елемент?", true);
                if (result)
                {
                    try
                    {
                        if (File.Exists(filePath)) File.Delete(filePath);
                        LoadHistory();
                    }
                    catch (Exception logEx) { AppLog.Warn(logEx); }
                }
            }
        }

        private async void BtnClearAll_Click(object? sender, RoutedEventArgs e)
        {
            bool isScreenshotsTab = TabScreenshots.IsChecked == true;
            string targetName = isScreenshotsTab ? "всі скріншоти" : "всі відеозаписи";

            bool result = await ShowCustomDialog("Увага!", $"Повністю видалити {targetName} з історії?", true);

            if (result)
            {
                string appDataFolder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One");
                try
                {
                    if (isScreenshotsTab)
                    {
                        string scrPath = System.IO.Path.Combine(appDataFolder, "Screenshots");
                        if (Directory.Exists(scrPath)) foreach (var f in Directory.GetFiles(scrPath)) File.Delete(f);
                    }
                    else
                    {
                        string vidPath = System.IO.Path.Combine(appDataFolder, "Videos");
                        if (Directory.Exists(vidPath)) foreach (var f in Directory.GetFiles(vidPath)) File.Delete(f);
                    }
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }

                LoadHistory();
            }
        }

        public async void BtnUploadFromHistory_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filePath)
            {
                if (!await UploadConsent.ConfirmAsync(this)) return;
                try
                {
                    ShowToast("Вивантаження на Google Диск...");

                    string link = "";
                    if (filePath.ToLower().EndsWith(".mp4"))
                        link = await GoogleDriveManager.UploadVideoAsync(filePath);
                    else
                        link = await GoogleDriveManager.UploadImageAsync(filePath);

                    if (!string.IsNullOrEmpty(link))
                    {
                        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                        if (clipboard != null) await clipboard.SetTextAsync(link);

                        ShowToast("Посилання на Google Диск скопійовано!");
                    }
                    else
                    {
                        await ShowCustomDialog("Помилка", "Посилання пусте.", false);
                    }
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog("Помилка", $"Не вдалося вивантажити:\n{ex.Message}", false);
                }
            }
        }

        private async void ListBox_DoubleTapped(object? sender, Avalonia.Input.TappedEventArgs e)
        {
            if (e.Source is Control control && (control is Button || control.Parent is Button)) return;

            var activeListBox = sender as ListBox;

            if (activeListBox?.SelectedItem is HistoryItem selectedItem)

            {

                try

                {

                    string flattenedPath = selectedItem.Path;

                    if (flattenedPath.ToLower().EndsWith(".mp4"))
                    {
                        if (_isOpeningVideoEditor) return;
                        _isOpeningVideoEditor = true;
                        try
                        {
                            var videoEditor = new VideoEditorWindow(flattenedPath, false, this, selectedItem);
                            videoEditor.Show();
                            this.Hide();
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write($"Error opening video editor: {ex}");
                        }
                        finally
                        {
                            _ = Task.Delay(1000).ContinueWith(_ => _isOpeningVideoEditor = false);
                        }
                        return;
                    }

                    string directory = System.IO.Path.GetDirectoryName(flattenedPath) ?? "";

                    string fileName = System.IO.Path.GetFileNameWithoutExtension(flattenedPath);

                    string originalPath = flattenedPath;

                    string? jsonPath = null;

                    if (fileName.EndsWith("_flattened"))

                    {
                        string id = fileName.Replace("_flattened", "");
                        string possibleOriginal = System.IO.Path.Combine(directory, $"{id}_original.png");
                        string possibleJson = System.IO.Path.Combine(directory, $"{id}_meta.json");

                        if (File.Exists(possibleOriginal)) originalPath = possibleOriginal;
                        if (File.Exists(possibleJson)) jsonPath = possibleJson;
                    }

                    var bitmap = new Bitmap(originalPath);
                    var editor = new EditorWindow(bitmap, jsonPath, this, selectedItem);
                    editor.Show();
                    this.Hide();
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog("Помилка", $"Не вдалося відкрити: {ex.Message}", false);
                }
            }
        }

        public void ReturnFromEditor(HistoryItem? targetItem, bool isVideo = false)
        {
            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    this.Show();
                    this.Activate();

                    if (isVideo)
                    {
                        TabVideos.IsChecked = true;
                        ScreenshotsListBox.IsVisible = false;
                        VideosListBox.IsVisible = true;
                        ToolTip.SetTip(BtnClearAll, "Очистити відео");

                        if (targetItem != null)
                        {
                            try
                            {
                                var newThumb = GenerateVideoThumbnail(targetItem.Path);
                                if (newThumb != null) targetItem.ImageSource = newThumb;
                            }
                            catch (Exception logEx) { AppLog.Warn(logEx); }

                            VideosListBox.SelectedItem = targetItem;
                            VideosListBox.ScrollIntoView(targetItem);
                        }
                    }
                    else
                    {
                        TabScreenshots.IsChecked = true;
                        ScreenshotsListBox.IsVisible = true;
                        VideosListBox.IsVisible = false;
                        ToolTip.SetTip(BtnClearAll, "Очистити скріншоти");

                        if (targetItem != null)
                        {
                            try
                            {
                                if (File.Exists(targetItem.Path))
                                {
                                    using (var fs = File.OpenRead(targetItem.Path))
                                    {
                                        targetItem.ImageSource = Bitmap.DecodeToWidth(fs, 280);
                                    }
                                }
                            }
                            catch (Exception logEx) { AppLog.Warn(logEx); }

                            ScreenshotsListBox.SelectedItem = targetItem;
                            ScreenshotsListBox.ScrollIntoView(targetItem);
                        }
                    }
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            }, DispatcherPriority.Loaded);
        }
}
}
