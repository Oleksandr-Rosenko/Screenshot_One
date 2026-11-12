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
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                InitializeTrayIcon(desktop);

                SettingsManager.LoadAndRegister();

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

            // Червона крапка на іконці трею, поки йде запис відео
            VideoRecorderManager.OnStatusChanged += isRecording =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_trayIcon != null)
                    {
                        _trayIcon.Icon = LoadTrayIcon(isRecording);
                    }
                });

            var menu = new NativeMenu();

            var regionItem = new NativeMenuItem("Зробити скріншот зони");

            regionItem.Click += (s, e) => ShowSingle(() => new MainWindow());

            menu.Add(regionItem);

            var regionDelayedItem = new NativeMenuItem("Скріншот зони (затримка 3 сек)");

            regionDelayedItem.Click += async (s, e) =>
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
            };
            menu.Add(regionDelayedItem);

            var fullScreenItem = new NativeMenuItem("Скріншот активного екрана");
            fullScreenItem.Click += (s, e) => CaptureAndOpenFullScreen();
            menu.Add(fullScreenItem);

            // НОВЕ: Додано пункт меню в трей для всіх екранів
            var allScreensItem = new NativeMenuItem("Скріншот усіх екранів одночасно");
            allScreensItem.Click += (s, e) => CaptureAllScreensSimultaneously();
            menu.Add(allScreensItem);

            var ocrItem = new NativeMenuItem("Розпізнати текст (OCR)");
            ocrItem.Click += (s, e) => ShowSingle(() => new MainWindow(ocrMode: true));
            menu.Add(ocrItem);

            var videoPanelItem = new NativeMenuItem("Записати відео");
            videoPanelItem.Click += (s, e) => ShowSingle(() => new VideoControlWindow());
            menu.Add(videoPanelItem);

            menu.Add(new NativeMenuItemSeparator());

            var historyItem = new NativeMenuItem("Історія");
            historyItem.Click += (s, e) => ShowSingle(() => new HistoryWindow());
            menu.Add(historyItem);

            var settingsItem = new NativeMenuItem("Налаштування");
            settingsItem.Click += (s, e) => ShowSingle(() => new SettingsWindow());
            menu.Add(settingsItem);

            var checkUpdatesItem = new NativeMenuItem("Перевірити оновлення");
            checkUpdatesItem.Click += async (s, e) => await UpdateWindow.CheckAndShowAsync(isManual: true);
            menu.Add(checkUpdatesItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Вихід");
            exitItem.Click += (s, e) =>
            {
                SettingsManager.UnregisterAll();
                // Дописуємо фонові збереження історії, щоб останній скріншот не загубився
                HistoryManager.WaitForPendingSaves(TimeSpan.FromSeconds(5));
                desktop.Shutdown();

            };

            menu.Add(exitItem);

            _trayIcon.Menu = menu;

            _trayIcon.IsVisible = true;

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
