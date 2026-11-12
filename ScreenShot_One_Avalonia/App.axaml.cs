using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Layout;
using System;
using System.IO;
using System.Threading.Tasks;
using Velopack;
using SukiUI.Controls;

namespace ScreenShot_One_Avalonia
{
    public partial class App : Application
    {
        private TrayIcon? _trayIcon;

        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
            // М'ятний акцент одразу (до завантаження налаштувань), щоб не блимав стандартний синій SukiUI
            AppTheme.ApplyAccent(RequestedThemeVariant ?? Avalonia.Styling.ThemeVariant.Dark);
            // Рядки інтерфейсу потрібні вже першим вікнам (мова з налаштувань застосовується трохи пізніше)
            Loc.InstallCurrent();
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                InitializeTrayIcon(desktop);

                SettingsManager.LoadAndRegister();
                // Мова інтерфейсу: з налаштувань або як у системі (перебудує й меню трею)
                Loc.Apply(SettingsManager.Current.Language);

                SettingsManager.OnRegionCapture += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ShowSingle(() => new MainWindow()));

                SettingsManager.OnFullScreenCapture += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => CaptureAndOpenFullScreen());

                SettingsManager.OnVideoRecord += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ShowSingle(() => new VideoControlWindow()));

                // НОВЕ: Зв'язуємо гарячу клавішу з новим методом знімку всіх екранів

                SettingsManager.OnAllScreensCapture += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => CaptureAllScreensSimultaneously());

                // Розпізнати текст: виділення зони → текст у буфері обміну
                SettingsManager.OnOcrCapture += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => ShowSingle(() => new MainWindow(ocrMode: true)));

                // macOS: одразу пояснюємо, якщо немає дозволу «Запис екрана» (інакше скріншоти «порожні»)
                Avalonia.Threading.Dispatcher.UIThread.Post(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(1500);
                    await MacPermissions.WarnIfScreenCaptureDeniedAsync();
                    // Якщо минулого разу програма впала — пропонуємо відкрити журнал
                    await CrashReport.OfferLogIfCrashedAsync();
                });

                // Прогріваємо VLC у фоні, щоб перше відкриття відеоредактора не підвисало
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(3000);
                    VlcShared.Prewarm();
                    // Windows: після оновлення — нова назва ярлика й свіжа іконка в «Пуску»
                    WindowsShell.RefreshAfterUpdate();
                });

                // Автоматична фонова перевірка оновлень через 6 секунд після запуску
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(6000);
                    await UpdateWindow.CheckAndShowAsync(isManual: false);
                });

            }

            base.OnFrameworkInitializationCompleted();

        }

        /// <summary>
        /// Показує вікно типу T в одному екземплярі. Раніше кожне натискання гарячої клавіші чи пункту трею
        /// створювало нове вікно — подвійне натискання відкривало два оверлеї виділення або дві панелі запису відео.
        /// Повторно використовується лише ВИДИМЕ вікно: приховані (напр. історія, поки відкритий редактор,
        /// або оверлей, що вже робить знімок) не чіпаємо, щоб не зламати їхній сценарій.
        /// </summary>
        private static T ShowSingle<T>(Func<T> factory) where T : Window
        {
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var w in desktop.Windows)
                {
                    if (w is T existing && existing.IsVisible)
                    {
                        if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                        existing.Activate();
                        return existing;
                    }
                }
            }
            var window = factory();
            window.Show();
            return window;
        }

        private void InitializeTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)

        {

            _trayIcon = new TrayIcon

            {

                Icon = LoadTrayIcon(isRecording: false),

                ToolTipText = "Screenshot One"

            };

            // Windows: лівий клік — панель з плитками (TrayPanelWindow), правий — звичайне меню.
            // macOS поки лишає меню: клік по значку в рядку меню відкриває саме його.
            if (OperatingSystem.IsWindows())
                _trayIcon.Clicked += (_, _) => TrayPanelWindow.Toggle(ScreenService.GetCursorPosition());

            // Червона крапка на іконці трею, поки йде запис відео
            VideoRecorderManager.OnStatusChanged += isRecording =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_trayIcon != null)
                    {
                        _trayIcon.Icon = LoadTrayIcon(isRecording);
                    }
                });

            _trayIcon.Menu = BuildTrayMenu(desktop);
            // Мова змінилась — міняємо лише підписи пунктів. Саме меню НЕ підміняємо: на macOS Avalonia
            // не дозволяє замінити меню трею («The menu being updated does not match») — у 2.8.0–2.8.1
            // це валило програму на старті, коли мова системи не українська.
            Loc.LanguageChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTrayMenuTexts);

            _trayIcon.IsVisible = true;

        }

        /// <summary>Вихід перед самооновленням на Mac: дописати історію й закрити програму.</summary>
        public static void ShutdownForUpdate()
        {
            SettingsManager.UnregisterAll();
            HistoryManager.WaitForPendingSaves(TimeSpan.FromSeconds(5));
            if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        }

        /// <summary>Дії трею — спільні для меню й панелі <see cref="TrayPanelWindow"/>.</summary>
        public enum TrayAction { Region, RegionDelayed, FullScreen, AllScreens, Ocr, Video, History, Settings, CheckUpdates, Quit }

        public void RunTrayAction(TrayAction action)
        {
            switch (action)
            {
                case TrayAction.Region: ShowSingle(() => new MainWindow()); break;
                case TrayAction.RegionDelayed: RunDelayedRegionCaptureAsync().FireAndForget("Скріншот із затримкою"); break;
                case TrayAction.FullScreen: CaptureAndOpenFullScreen(); break;
                case TrayAction.AllScreens: CaptureAllScreensSimultaneously(); break;
                case TrayAction.Ocr: ShowSingle(() => new MainWindow(ocrMode: true)); break;
                case TrayAction.Video: ShowSingle(() => new VideoControlWindow()); break;
                case TrayAction.History: ShowSingle(() => new HistoryWindow()); break;
                case TrayAction.Settings: ShowSingle(() => new SettingsWindow()); break;
                case TrayAction.CheckUpdates: UpdateWindow.CheckAndShowAsync(isManual: true).FireAndForget("Перевірка оновлень"); break;
                case TrayAction.Quit:
                    SettingsManager.UnregisterAll();
                    // Дописуємо фонові збереження історії, щоб останній скріншот не загубився
                    HistoryManager.WaitForPendingSaves(TimeSpan.FromSeconds(5));
                    if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
                    break;
            }
        }

        private async System.Threading.Tasks.Task RunDelayedRegionCaptureAsync()
        {
                var countdownWindow = new Window
                {
                    Width = 200,
                    Height = 200,
                    WindowDecorations = WindowDecorations.None,
                    Background = Avalonia.Media.Brushes.Transparent,
                    Topmost = true,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    IsHitTestVisible = false,
                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                };

                var textBlock = new TextBlock
                {
                    Text = "3",
                    FontSize = 100,
                    FontWeight = Avalonia.Media.FontWeight.Bold,
                    Foreground = Avalonia.Media.Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                var border = new Border
                {
                    Background = Avalonia.Media.SolidColorBrush.Parse("#99000000"),
                    CornerRadius = new Avalonia.CornerRadius(30),
                    Width = 150,
                    Height = 150,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = textBlock
                };

                countdownWindow.Content = border;
                countdownWindow.Show();

                for (int i = 3; i > 0; i--)
                {
                    textBlock.Text = i.ToString();
                    await Task.Delay(1000);
                }

                countdownWindow.Close();

                Avalonia.Threading.Dispatcher.UIThread.Post(() => ShowSingle(() => new MainWindow()));
        }

        // Пункти меню трею з ключами рядків — для зміни мови без перебудови меню
        private readonly System.Collections.Generic.List<(NativeMenuItem Item, string Key)> _trayMenuItems = new();

        private void UpdateTrayMenuTexts()
        {
            try
            {
                foreach (var (item, key) in _trayMenuItems) item.Header = Loc.T(key);
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        private NativeMenu BuildTrayMenu(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var menu = new NativeMenu();
            _trayMenuItems.Clear();
            void Add(string key, TrayAction action)
            {
                var item = new NativeMenuItem(Loc.T(key));
                item.Click += (s, e) => RunTrayAction(action);
                menu.Add(item);
                _trayMenuItems.Add((item, key));
            }

            Add("Tray_Region", TrayAction.Region);
            Add("Tray_RegionDelayed", TrayAction.RegionDelayed);
            Add("Tray_FullScreen", TrayAction.FullScreen);
            Add("Tray_AllScreens", TrayAction.AllScreens);
            Add("Common_Ocr", TrayAction.Ocr);
            Add("Tray_Video", TrayAction.Video);
            menu.Add(new NativeMenuItemSeparator());
            Add("Tray_History", TrayAction.History);
            Add("Tray_Settings", TrayAction.Settings);
            // Тест UnitTest1 шукає цей рядок: NativeMenuItem(Loc.T("Tray_CheckUpdates"))
            var checkUpdatesItem = new NativeMenuItem(Loc.T("Tray_CheckUpdates"));
            checkUpdatesItem.Click += (s, e) => RunTrayAction(TrayAction.CheckUpdates);
            menu.Add(checkUpdatesItem);
            _trayMenuItems.Add((checkUpdatesItem, "Tray_CheckUpdates"));
            menu.Add(new NativeMenuItemSeparator());
            Add("Tray_Exit", TrayAction.Quit);
            return menu;
        }

        // Іконка трею береться з готових PNG у Assets (tray.png / tray-recording.png).
        // Раніше вона малювалась через System.Drawing, який працює лише на Windows:
        // на macOS це давало порожню картинку, а new WindowIcon(...) падав і валив програму на старті.
        private static WindowIcon? LoadTrayIcon(bool isRecording)

        {

            // Фірмовий знак «SO» (однаковий на Windows і Mac), під час запису — з червоною точкою.
            string assetName = isRecording ? "tray-recording.png" : "tray.png";

            try

            {

                using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://Screenshot_One/Assets/{assetName}"));

                return new WindowIcon(stream);
            }
            catch (Exception ex)
            {
                AppLog.Write($"Не вдалося завантажити іконку трею {assetName}: {ex.Message}");
                return null;
            }
        }

        private void CaptureAndOpenFullScreen()
        {
            try
            {
                var mousePos = ScreenService.GetCursorPosition();
                var currentScreen = ScreenService.GetScreenFromPoint(mousePos) ?? ScreenService.GetPrimaryScreen();
                if (currentScreen != null)
                {
                    var avaloniaBitmap = ScreenCaptureService.Instance.CaptureScreen(currentScreen);
                    if (avaloniaBitmap != null)
                    {
                        CaptureRouter.Deliver(avaloniaBitmap);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Помилка повноекранного скріншота: " + ex.Message);
            }
        }

        // НОВЕ: Метод для створення єдиного великого скріншота всіх моніторів
        private void CaptureAllScreensSimultaneously()
        {
            try
            {
                var avaloniaBitmap = ScreenCaptureService.Instance.CaptureAllScreens();
                if (avaloniaBitmap != null)
                {
                    CaptureRouter.Deliver(avaloniaBitmap);
                }
            }

            catch (Exception ex)

            {

                AppLog.Write("Помилка створення скріншота всіх моніторів: " + ex.Message);

            }

        }

    }
}
