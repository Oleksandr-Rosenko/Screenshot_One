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

        // ---- Пошук і теги (3.1.0) ----
        private string _tagsLine = "";
        public string TagsLine
        {
            get => _tagsLine;
            private set { _tagsLine = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TagsLine))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasTags))); }
        }
        public bool HasTags => _tagsLine.Length > 0;
        public IReadOnlyList<string> Tags { get; private set; } = Array.Empty<string>();

        // Автотеги (3.2.0) — окремим, світлішим рядком
        private string _autoTagsLine = "";
        public string AutoTagsLine
        {
            get => _autoTagsLine;
            private set { _autoTagsLine = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutoTagsLine))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAutoTags))); }
        }
        public bool HasAutoTags => _autoTagsLine.Length > 0;
        public IReadOnlyList<string> AutoTags { get; private set; } = Array.Empty<string>();

        /// <summary>Власні теги й автотеги разом (фільтр за тегом).</summary>
        public bool HasTag(string tag) =>
            Tags.Contains(tag, StringComparer.OrdinalIgnoreCase) || AutoTags.Contains(tag, StringComparer.OrdinalIgnoreCase);
        /// <summary>Програма, з якої знято (якщо відома) — показуємо на картці.</summary>
        public string? App { get; private set; }

        /// <summary>Підтягує теги з індексу історії.</summary>
        public void RefreshInfo()
        {
            var info = HistoryIndex.Get(Path);
            Tags = info?.Tags.ToArray() ?? Array.Empty<string>();
            AutoTags = HistoryIndex.AutoTagsEnabled && info != null
                ? info.AutoTags.Where(a => !Tags.Contains(a, StringComparer.OrdinalIgnoreCase)).ToArray()
                : Array.Empty<string>();
            App = info?.App;
            TagsLine = string.Join("  ", Tags.Select(t => "#" + t));
            AutoTagsLine = (Tags.Count > 0 && AutoTags.Count > 0 ? "  " : "") + string.Join("  ", AutoTags.Select(t => "#" + t));
        }

        /// <summary>Чи підходить запис під пошук: дата, назва файла, програма/вікно, текст на знімку, теги.</summary>
        public bool Matches(string query) => HistoryIndex.Matches(query, HistoryIndex.Get(Path), Title, DateString, FileName);


        /// <summary>Людська дата: «Сьогодні, 13:04», «Вчора, 18:20», «11 листопада, 09:15», «3 березня 2025, 10:00».</summary>
        public static string FriendlyDate(DateTime time, DateTime now)
        {
            string hm = time.ToString("HH:mm");
            if (time.Date == now.Date) return Loc.F("History_Today", hm);
            if (time.Date == now.Date.AddDays(-1)) return Loc.F("History_Yesterday", hm);
            string month = Loc.T("History_Months").Split(',')[time.Month - 1];
            return time.Year == now.Year
                ? Loc.F("History_DateThisYear", time.Day, month, hm)
                : Loc.F("History_DateOtherYear", time.Day, month, hm, time.Year);
        }

        /// <summary>«245 КБ», «2,4 МБ».</summary>
        public static string FriendlySize(long bytes)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            if (bytes < 1024) return Loc.F("History_Bytes", bytes);
            if (bytes < 1024 * 1024) return Loc.F("History_KB", (bytes / 1024.0).ToString("0", inv));
            return Loc.F("History_MB", (bytes / (1024.0 * 1024.0)).ToString("0.#", inv).Replace(".", Loc.T("History_DecimalSeparator")));
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
                Details = isVideo ? Loc.F("History_VideoDetails", details) : details,
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

                if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return null;
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
        // Після пошуку/фільтра за тегом — саме ці списки показуються сторінками
        private List<HistoryItem> _filteredScreens = new List<HistoryItem>();
        private List<HistoryItem> _filteredVideos = new List<HistoryItem>();
        private string _query = "";
        private string? _tagFilter;
        private readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
        // Перелік тегів над галереєю перебудовуємо не на кожен запис фонового розпізнавання, а раз на ~1,5 с
        private bool _isClosed;
        private readonly DispatcherTimer _chipsTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };

        // Змінна для нашого нового рідного діалогового вікна

        private TaskCompletionSource<bool>? _dialogTcs;

                private bool _isOpeningVideoEditor = false;
        public HistoryWindow()

        {

            InitializeComponent();

            // Сітка карток по центру: ширина підганяється під ціле число колонок
            SizeChanged += (_, _) => UpdateGalleryLayout();
            // Видима область змінюється й тоді, коли з'являється смуга прокрутки — теж перераховуємо
            LayoutUpdated += (_, _) =>
            {
                var lb = VideosListBox.IsVisible ? VideosListBox : ScreenshotsListBox;
                double w = lb.ItemsPanelRoot is Panel p ? GallerySpace(p) : 0;
                if (Math.Abs(w - _lastViewportWidth) > 0.5) { _lastViewportWidth = w; UpdateGalleryLayout(); }
            };

            ScreenshotsListBox.ItemsSource = _shownScreens;
            VideosListBox.ItemsSource = _shownVideos;

            // Пошук: застосовуємо, коли користувач на мить зупинився; розпізнаний у фоні текст теж підхоплюємо
            _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); ApplyFilter(); };
            _chipsTimer.Tick += (_, _) => { _chipsTimer.Stop(); RebuildTagChips(); };
            HistoryIndex.Changed += OnIndexChanged;
            Closed += (_, _) => { _isClosed = true; HistoryIndex.Changed -= OnIndexChanged; _searchTimer.Stop(); _chipsTimer.Stop(); };
            // Наступна сторінка карток — коли до кінця лишається кілька рядів
            ScreenshotsListBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) => OnGalleryScrolled(e, isVideo: false));
            VideosListBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) => OnGalleryScrolled(e, isVideo: true));

            LoadHistory();

        }

        // Ширина «слота» однієї картки: 236 px картки + по 6 px відступу з кожного боку (запасне значення,
        // справжню ширину беремо з першої картки — на Windows вона може бути трохи більшою через шрифти/DPI)
        private const double CardSlotWidth = 248;
        private double _lastViewportWidth;

        /// <summary>
        /// Скільки колонок вміщується у видиму область і який відступ зліва/справа потрібен, щоб сітка
        /// стала по центру (публічне для тестів). Відступ округлюється вниз — тоді колонки гарантовано
        /// вміщаються й WrapPanel не переносить останню на новий рядок.
        /// </summary>
        public static (int Columns, double SideMargin) ComputeGalleryLayout(double viewportWidth, double slotWidth = CardSlotWidth)
        {
            if (slotWidth <= 0) slotWidth = CardSlotWidth;
            int cols = Math.Max(1, (int)Math.Floor(viewportWidth / slotWidth));
            double margin = Math.Max(0, Math.Floor((viewportWidth - cols * slotWidth) / 2) - 1);
            return (cols, margin);
        }

        private static double GallerySpace(Panel panel) => panel.Bounds.Width + panel.Margin.Left + panel.Margin.Right;

        /// <summary>
        /// Центрує сітку: панель карток отримує однакові відступи зліва й справа (неповний останній рядок
        /// вирівнюється по лівому краю сітки), а шапка — такі самі межі, щоб кнопка очищення стояла над
        /// правим краєм карток. Раніше панелі задавалась фіксована ширина під 248 px на картку; на Windows
        /// картка виходила ширшою, остання колонка переносилась, і сітка «липла» ліворуч.
        /// </summary>
        private void UpdateGalleryLayout()
        {
            var lb = VideosListBox?.IsVisible == true ? VideosListBox : ScreenshotsListBox;
            if (lb?.ItemsPanelRoot is not Panel panel) return;
            // Місце, яке список реально дає панелі (видима ширина мінус внутрішні відступи шаблону SukiUI)
            double space = GallerySpace(panel);
            if (space <= 0) return;

            double slot = panel.Children.Count > 0 ? panel.Children[0].DesiredSize.Width : CardSlotWidth;
            var (cols, margin) = ComputeGalleryLayout(space, slot);

            foreach (var l in new[] { ScreenshotsListBox, VideosListBox })
            {
                if (l?.ItemsPanelRoot is Panel p && Math.Abs(p.Margin.Left - margin) > 0.5)
                    p.Margin = new Thickness(margin, 0, margin, 0);
            }

            if (HeaderRow?.Parent is Visual headerParent)
            {
                // Лівий край місця під панель = позиція панелі мінус її поточний відступ
                var panelOrigin = panel.TranslatePoint(new Point(0, 0), headerParent);
                if (panelOrigin == null) return;
                double spaceLeft = panelOrigin.Value.X - panel.Margin.Left;
                double left = spaceLeft + margin + 6;                    // 6 — зовнішній відступ картки
                double right = headerParent.Bounds.Width - (spaceLeft + margin + cols * slot - 6);
                HeaderRow.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
                HeaderRow.Width = double.NaN;
                HeaderRow.Margin = new Thickness(Math.Max(0, left), 14, Math.Max(0, right), 6);
                TagChips.Margin = new Thickness(Math.Max(0, left), 0, Math.Max(0, right), 4); // теги — рівно над сіткою
            }
        }

        private void Tab_Checked(object? sender, RoutedEventArgs e)

        {
            if (ScreenshotsListBox == null || VideosListBox == null) return;

            if (TabScreenshots.IsChecked == true)
            {
                ScreenshotsListBox.IsVisible = true;
                VideosListBox.IsVisible = false;
                ToolTip.SetTip(BtnClearAll, Loc.T("History_ClearScreenshots"));
            }
            else
            {
                ScreenshotsListBox.IsVisible = false;
                VideosListBox.IsVisible = true;
                ToolTip.SetTip(BtnClearAll, Loc.T("History_ClearVideos"));
            }
            UpdatePlaceholders();
            Avalonia.Threading.Dispatcher.UIThread.Post(UpdateGalleryLayout, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private void UpdatePlaceholders()
        {
            if (EmptyScreenshotsText == null || EmptyVideosText == null) return;

            bool screens = TabScreenshots.IsChecked == true;
            var all = screens ? _screenshotItems : _videoItems;
            var filtered = screens ? _filteredScreens : _filteredVideos;
            EmptyScreenshotsText.IsVisible = screens && !_isLoading && all.Count == 0;
            EmptyVideosText.IsVisible = !screens && !_isLoading && all.Count == 0;
            // Записи є, але пошук нічого не знайшов
            NoResultsText.IsVisible = !_isLoading && all.Count > 0 && filtered.Count == 0;
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
                HistoryIndex.Prune(); // записи пошуку про вже видалені файли
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
            foreach (var item in _screenshotItems) item.RefreshInfo();
            foreach (var item in _videoItems) item.RefreshInfo();

            _isLoading = false;
            RebuildTagChips();
            ApplyFilter();
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
            var all = isVideo ? _filteredVideos : _filteredScreens;
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
                            details = (item.App != null ? item.App + " · " : "") + $"{size.Width} × {size.Height} · {HistoryItem.FriendlySize(new FileInfo(item.Path).Length)}";

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

        // ---- Пошук і теги (3.1.0) ----

        private void TxtSearch_TextChanged(object? sender, TextChangedEventArgs e)
        {
            _query = TxtSearch.Text?.Trim() ?? "";
            _searchTimer.Stop();
            _searchTimer.Start();
        }

        private void OnIndexChanged(string path)
        {
            // Фоновий OCR дописав текст або автотеги — оновлюємо картку одразу (раніше теги з'являлись лише після
            // повторного відкриття вікна), перелік тегів — трохи згодом, результати пошуку — якщо зараз шукаємо
            Dispatcher.UIThread.Post(() =>
            {
                string full = System.IO.Path.GetFullPath(path);
                foreach (var item in _screenshotItems.Concat(_videoItems))
                    if (string.Equals(System.IO.Path.GetFullPath(item.Path), full, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        item.RefreshInfo();
                if (!_chipsTimer.IsEnabled) _chipsTimer.Start();
                if (_query.Length > 0 && !_searchTimer.IsEnabled) _searchTimer.Start();
            });
        }

        /// <summary>Фільтрує записи за пошуком і тегом і показує першу сторінку.</summary>
        private void ApplyFilter()
        {
            bool Pass(HistoryItem i) =>
                (_tagFilter == null || i.HasTag(_tagFilter)) && i.Matches(_query);
            _filteredScreens = _screenshotItems.Where(Pass).ToList();
            _filteredVideos = _videoItems.Where(Pass).ToList();

            // Показані колекції сповіщають сітку про кожну зміну — без перепривʼязки (WrapPanel падав на «тихих» мутаціях)
            _shownScreens.Clear();
            _shownVideos.Clear();
            AppendPage(isVideo: false);
            AppendPage(isVideo: true);
            UpdatePlaceholders();
            Dispatcher.UIThread.Post(UpdateGalleryLayout, DispatcherPriority.Loaded);
        }

        /// <summary>«Пігулки» тегів над галереєю: клік — показати лише записи з тегом, повторний клік — усі.</summary>
        private void RebuildTagChips()
        {
            TagChips.Children.Clear();
            var tags = HistoryIndex.AllTags();
            if (_tagFilter != null && !tags.Any(t => string.Equals(t.Tag, _tagFilter, StringComparison.OrdinalIgnoreCase))) _tagFilter = null;
            foreach (var (tag, count) in tags.Take(30))
            {
                bool active = string.Equals(tag, _tagFilter, StringComparison.OrdinalIgnoreCase);
                var chip = new Button
                {
                    Content = $"#{tag}  {count}",
                    Margin = new Thickness(0, 0, 6, 6),
                    Padding = new Thickness(10, 3),
                    CornerRadius = new CornerRadius(12),
                    FontSize = 12,
                };
                chip.Classes.Add(active ? "Flat" : "Basic");
                if (!active)
                {
                    chip.Background = AppTheme.Brush(this, "PanelBrush", Avalonia.Media.Brushes.White);
                    chip.BorderBrush = AppTheme.Brush(this, "PanelBorderBrush", Avalonia.Media.Brushes.Gray);
                    chip.BorderThickness = new Thickness(1);
                }
                chip.Click += (_, _) =>
                {
                    _tagFilter = active ? null : tag;
                    RebuildTagChips();
                    ApplyFilter();
                };
                TagChips.Children.Add(chip);
            }
            TagChips.IsVisible = TagChips.Children.Count > 0;
        }

        /// <summary>Теги запису: поточні (клік — прибрати), поле для нового (Enter), підказки з уже наявних тегів.</summary>
        private void BtnTags_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button button || button.Tag is not string path) return;
            var item = _screenshotItems.Concat(_videoItems).FirstOrDefault(i => i.Path == path);
            if (item == null) return;

            var current = new WrapPanel();
            var suggestions = new WrapPanel();
            var input = new TextBox { PlaceholderText = Loc.T("History_TagAddHint"), MinWidth = 240 };

            var autoTitle = new TextBlock { Text = Loc.T("History_AutoTags"), FontSize = 12, Foreground = AppTheme.Brush(this, "MutedTextBrush", Avalonia.Media.Brushes.Gray) };
            var autoPanel = new WrapPanel();

            void Refresh()
            {
                current.Children.Clear();
                foreach (var t in item.Tags)
                {
                    string tag = t;
                    current.Children.Add(Chip($"#{tag}  ✕", () => SetTags(item.Tags.Where(x => x != tag)), accent: true));
                }
                // Автотеги: прибрати одним кліком (більше не повернуться)
                autoPanel.Children.Clear();
                foreach (var t in item.AutoTags)
                {
                    string tag = t;
                    var chip = Chip($"#{tag}  ✕", () =>
                    {
                        HistoryIndex.RemoveAutoTag(path, tag);
                        item.RefreshInfo();
                        Refresh();
                        RebuildTagChips();
                        if (_tagFilter != null || _query.Length > 0) ApplyFilter();
                    }, accent: false);
                    chip.Foreground = AppTheme.Brush(this, "MutedTextBrush", Avalonia.Media.Brushes.Gray);
                    autoPanel.Children.Add(chip);
                }
                autoTitle.IsVisible = autoPanel.IsVisible = autoPanel.Children.Count > 0;
                suggestions.Children.Clear();
                foreach (var (t, _) in HistoryIndex.AllTags().Where(x => !item.HasTag(x.Tag)).Take(8))
                {
                    string tag = t;
                    suggestions.Children.Add(Chip($"+ #{tag}", () => SetTags(item.Tags.Append(tag)), accent: false));
                }
                suggestions.IsVisible = suggestions.Children.Count > 0;
            }

            void SetTags(IEnumerable<string> tags)
            {
                HistoryIndex.SetTags(path, tags);
                item.RefreshInfo();
                Refresh();
                RebuildTagChips();
                if (_tagFilter != null || _query.Length > 0) ApplyFilter();
            }

            Button Chip(string text, Action onClick, bool accent)
            {
                var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6), Padding = new Thickness(10, 3), CornerRadius = new CornerRadius(12), FontSize = 12 };
                b.Classes.Add(accent ? "Flat" : "Basic");
                b.Click += (_, _) => onClick();
                return b;
            }

            input.KeyDown += (_, ke) =>
            {
                if (ke.Key != Avalonia.Input.Key.Enter) return;
                ke.Handled = true;
                var added = (input.Text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
                if (added.Length > 0) SetTags(item.Tags.Concat(added));
                input.Text = "";
            };
            Refresh();

            var content = new StackPanel { Spacing = 8, Width = 270 };
            content.Children.Add(new TextBlock { Text = Loc.T("History_TagsTitle"), FontWeight = Avalonia.Media.FontWeight.SemiBold });
            content.Children.Add(current);
            content.Children.Add(autoTitle);
            content.Children.Add(autoPanel);
            content.Children.Add(input);
            content.Children.Add(suggestions);
            var card = new Border
            {
                Child = content, Padding = new Thickness(14), CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1),
                Background = AppTheme.Brush(this, "PanelBrush", Avalonia.Media.Brushes.White),
                BorderBrush = AppTheme.Brush(this, "PanelBorderBrush", Avalonia.Media.Brushes.Gray),
            };
            var flyout = new Flyout { Content = card, Placement = PlacementMode.BottomEdgeAlignedRight };
            flyout.Opened += (_, _) => Dispatcher.UIThread.Post(() => input.Focus(), DispatcherPriority.Loaded);
            flyout.ShowAt(button);
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
                    ShowToast(Loc.T("History_Copied"));
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog(Loc.T("Common_Error"), Loc.F("History_CopyFailed", ex.Message), false);
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
                    await ShowCustomDialog(Loc.T("Common_Error"), Loc.F("History_OpenFolderFailed", ex.Message), false);
                }
            }
        }

        private async void BtnDeleteFromHistory_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.Tag is string filePath)
            {
                bool result = await ShowCustomDialog(Loc.T("Common_Confirm"), Loc.T("History_DeleteItem"), true);
                if (result)
                {
                    try
                    {
                        // Запис програми — разом з оригіналом, фігурами й даними пошуку; інші файли — самі
                        if (filePath.EndsWith("_flattened.png", StringComparison.OrdinalIgnoreCase)) HistoryManager.DeleteScreenshot(filePath);
                        else
                        {
                            if (File.Exists(filePath)) File.Delete(filePath);
                            HistoryIndex.Remove(filePath);
                        }
                        LoadHistory();
                    }
                    catch (Exception logEx) { AppLog.Warn(logEx); }
                }
            }
        }

        private async void BtnClearAll_Click(object? sender, RoutedEventArgs e)
        {
            bool isScreenshotsTab = TabScreenshots.IsChecked == true;
            string targetName = isScreenshotsTab ? Loc.T("History_AllScreenshots") : Loc.T("History_AllVideos");

            bool result = await ShowCustomDialog(Loc.T("Common_Attention"), Loc.F("History_ClearConfirm", targetName), true);

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
                    ShowToast(Loc.T("History_UploadingDrive"));

                    string link = "";
                    if (filePath.ToLower().EndsWith(".mp4"))
                        link = await GoogleDriveManager.UploadVideoAsync(filePath);
                    else
                        link = await GoogleDriveManager.UploadImageAsync(filePath);

                    if (!string.IsNullOrEmpty(link))
                    {
                        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                        if (clipboard != null) await clipboard.SetTextAsync(link);

                        ShowToast(Loc.T("History_DriveLinkCopied"));
                    }
                    else
                    {
                        await ShowCustomDialog(Loc.T("Common_Error"), Loc.T("History_EmptyLink"), false);
                    }
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog(Loc.T("Common_Error"), Loc.F("History_UploadFailed", ex.Message), false);
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
                    // Історія лишається відкритою під редактором: раніше вона ховалась і після редактора з'являлась
                    // наново — виглядало, ніби вікно закривається й відкривається
                    editor.Show();
                }
                catch (Exception ex)
                {
                    await ShowCustomDialog(Loc.T("Common_Error"), Loc.F("History_OpenFailed", ex.Message), false);
                }
            }
        }

        public void ReturnFromEditor(HistoryItem? targetItem, bool isVideo = false)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_isClosed) return; // історію закрили, поки був відкритий редактор
                try
                {
                    if (!IsVisible) Show();
                    if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                    Activate();

                    if (isVideo)
                    {
                        TabVideos.IsChecked = true;
                        ScreenshotsListBox.IsVisible = false;
                        VideosListBox.IsVisible = true;
                        ToolTip.SetTip(BtnClearAll, Loc.T("History_ClearVideos"));

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
                        ToolTip.SetTip(BtnClearAll, Loc.T("History_ClearScreenshots"));

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
