using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Панель трею (2.8.2): клік по значку «SO» відкриває картку з плитками дій, останніми знімками
    /// й нижнім рядком (налаштування, оновлення, вихід). Ховається, коли фокус іде деінде, або по Esc.
    /// Дії виконує <see cref="App.RunTrayAction"/> — ті самі, що й у меню трею.
    /// </summary>
    public partial class TrayPanelWindow : Window
    {
        private static TrayPanelWindow? _current;
        private bool _closing;

        public TrayPanelWindow()
        {
            InitializeComponent();
            var v = typeof(TrayPanelWindow).Assembly.GetName().Version;
            TxtVersion.Text = v == null ? "" : $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}";

            var s = SettingsManager.Current;
            KeysRegion.Text = ShortKeys(s.RegionModifier, s.RegionKey);
            KeysFullScreen.Text = ShortKeys(s.FullScreenModifier, s.FullScreenKey);
            KeysAllScreens.Text = ShortKeys(s.AllScreensModifier, s.AllScreensKey);
            KeysOcr.Text = ShortKeys(s.OcrModifier, s.OcrKey);
            KeysVideo.Text = ShortKeys(s.VideoModifier, s.VideoKey);

            Deactivated += (_, _) => SafeClose();
            KeyDown += (_, e) => { if (e.Key == Key.Escape) SafeClose(); };
            LoadRecentAsync().FireAndForget("Панель трею: останні знімки");
        }

        /// <summary>Показує панель біля точки кліку (повторний клік — ховає).</summary>
        public static void Toggle(PixelPoint? anchor)
        {
            if (_current != null) { _current.SafeClose(); return; }
            var w = new TrayPanelWindow();
            _current = w;
            w.Closed += (_, _) => { if (_current == w) _current = null; };
            w.Opacity = 0; // показуємо після розміщення, щоб не блимало в куті екрана
            w.Show();
            Dispatcher.UIThread.Post(() =>
            {
                w.PlaceNear(anchor ?? ScreenService.GetCursorPosition());
                w.Opacity = 1;
                w.Activate();
            }, DispatcherPriority.Loaded);
        }

        /// <summary>
        /// Біля значка трею: над точкою, якщо вона в нижній половині екрана (панель задач Windows унизу),
        /// інакше під нею (рядок меню Mac, панель задач угорі). Завжди в межах робочої області.
        /// </summary>
        private void PlaceNear(PixelPoint p)
        {
            try
            {
                var screen = Screens.ScreenFromPoint(p) ?? Screens.Primary;
                if (screen == null) return;
                var area = screen.WorkingArea;
                double scale = screen.Scaling > 0 ? screen.Scaling : 1.0;
                int w = (int)(Bounds.Width * scale), h = (int)(Bounds.Height * scale);
                int x = p.X - w / 2;
                int y = p.Y > area.Y + area.Height / 2 ? p.Y - h - 4 : p.Y + 4;
                x = Math.Clamp(x, area.X, Math.Max(area.X, area.Right - w));
                y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - h));
                Position = new PixelPoint(x, y);
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        private void SafeClose()
        {
            if (_closing) return;
            _closing = true;
            Close();
        }

        /// <summary>Компактна клавіша для плитки: «Ctrl+PrtSc», на Mac «⌘⇧7».</summary>
        public static string ShortKeys(int modifier, int key)
        {
            string text = SettingsWindow.FormatHotkey(modifier, key).Replace("PrintScreen", "PrtSc");
            if (OperatingSystem.IsMacOS())
                return text.Replace("Ctrl + ", "⌃").Replace("Option + ", "⌥").Replace("Shift + ", "⇧").Replace("Cmd + ", "⌘");
            return text.Replace(" + ", "+");
        }

        private void Tile_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string tag } || !Enum.TryParse<App.TrayAction>(tag, out var action)) return;
            SafeClose();
            // Панель має зникнути до знімка, інакше вона потрапить на скріншот
            Task.Delay(180).ContinueWith(_ => Dispatcher.UIThread.Post(() =>
            {
                if (Application.Current is App app) app.RunTrayAction(action);
            }));
        }

        private async Task LoadRecentAsync()
        {
            var files = await Task.Run(() =>
            {
                try
                {
                    return new DirectoryInfo(HistoryManager.HistoryFolderPath).GetFiles("*_flattened.png")
                        .OrderByDescending(f => f.CreationTimeUtc).Take(3).ToList();
                }
                catch (Exception ex) { AppLog.Warn(ex); return new System.Collections.Generic.List<FileInfo>(); }
            });
            TxtNoRecent.IsVisible = files.Count == 0;
            RecentGrid.IsVisible = files.Count > 0;

            foreach (var file in files)
            {
                Bitmap? thumb = await Task.Run(() =>
                {
                    try { using var fs = File.OpenRead(file.FullName); return Bitmap.DecodeToWidth(fs, 220); }
                    catch (Exception ex) { AppLog.Warn(ex); return null; }
                });
                var button = new Button { Content = new Image { Source = thumb, Stretch = Stretch.UniformToFill }, Tag = file.FullName };
                button.Classes.Add("thumb");
                ToolTip.SetTip(button, HistoryItem.FriendlyDate(file.CreationTime, DateTime.Now));
                button.Click += Thumb_Click;
                RecentGrid.Children.Add(button);
            }
        }

        /// <summary>Відкрити знімок з історії в редакторі (з фігурами, якщо є _meta.json).</summary>
        private void Thumb_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string flattened }) return;
            SafeClose();
            try
            {
                string dir = Path.GetDirectoryName(flattened) ?? "";
                string id = Path.GetFileNameWithoutExtension(flattened).Replace("_flattened", "");
                string original = Path.Combine(dir, $"{id}_original.png");
                string json = Path.Combine(dir, $"{id}_meta.json");
                var bitmap = new Bitmap(File.Exists(original) ? original : flattened);
                var item = HistoryItem.FromFile(new FileInfo(flattened), isVideo: false);
                new EditorWindow(bitmap, File.Exists(json) ? json : null, null, item).Show();
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Панель трею: відкриття знімка");
            }
        }
    }
}
