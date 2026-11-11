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
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

                InitializeTrayIcon(desktop);

                SettingsManager.LoadAndRegister();

                SettingsManager.OnRegionCapture += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => new MainWindow().Show());

                SettingsManager.OnFullScreenCapture += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => CaptureAndOpenFullScreen());

                SettingsManager.OnVideoRecord += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => new VideoControlWindow().Show());

                // НОВЕ: Зв'язуємо гарячу клавішу з новим методом знімку всіх екранів

                SettingsManager.OnAllScreensCapture += () => Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => CaptureAllScreensSimultaneously());

                // Автоматична фонова перевірка оновлень через 6 секунд після запуску
                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await System.Threading.Tasks.Task.Delay(6000);
                    await UpdateWindow.CheckAndShowAsync(isManual: false);
                });

            }

            base.OnFrameworkInitializationCompleted();

        }

        private void InitializeTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)

        {

            _trayIcon = new TrayIcon

            {

                Icon = LoadTrayIcon(isRecording: false),

                ToolTipText = "ScreenShot_One"

            };

            // Червона крапка на іконці трею, поки йде запис відео
            VideoRecorderManager.OnStatusChanged += isRecording =>
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    if (_trayIcon != null) _trayIcon.Icon = LoadTrayIcon(isRecording);
                });

            var menu = new NativeMenu();

            var regionItem = new NativeMenuItem("Зробити скріншот зони");

            regionItem.Click += (s, e) => new MainWindow().Show();

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

                Avalonia.Threading.Dispatcher.UIThread.Post(() => new MainWindow().Show());
            };
            menu.Add(regionDelayedItem);

            var fullScreenItem = new NativeMenuItem("Скріншот активного екрана");
            fullScreenItem.Click += (s, e) => CaptureAndOpenFullScreen();
            menu.Add(fullScreenItem);

            // НОВЕ: Додано пункт меню в трей для всіх екранів
            var allScreensItem = new NativeMenuItem("Скріншот усіх екранів одночасно");
            allScreensItem.Click += (s, e) => CaptureAllScreensSimultaneously();
            menu.Add(allScreensItem);

            var videoPanelItem = new NativeMenuItem("Записати відео");
            videoPanelItem.Click += (s, e) => new VideoControlWindow().Show();
            menu.Add(videoPanelItem);

            menu.Add(new NativeMenuItemSeparator());

            var historyItem = new NativeMenuItem("Історія");
            historyItem.Click += (s, e) => new HistoryWindow().Show();
            menu.Add(historyItem);

            var settingsItem = new NativeMenuItem("Налаштування");
            settingsItem.Click += (s, e) => new SettingsWindow().Show();
            menu.Add(settingsItem);

            var checkUpdatesItem = new NativeMenuItem("Перевірити оновлення");
            checkUpdatesItem.Click += async (s, e) => await UpdateWindow.CheckAndShowAsync(isManual: true);
            menu.Add(checkUpdatesItem);

            menu.Add(new NativeMenuItemSeparator());

            var exitItem = new NativeMenuItem("Вихід");
            exitItem.Click += (s, e) =>
            {
                SettingsManager.UnregisterAll();
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

            string assetName = isRecording ? "tray-recording.png" : "tray.png";

            try

            {

                using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://Screenshot_One/Assets/{assetName}"));

                return new WindowIcon(stream);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Не вдалося завантажити іконку трею {assetName}: {ex.Message}");
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
                        new EditorWindow(avaloniaBitmap).Show();
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Помилка повноекранного скріншота: " + ex.Message);
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
                    new EditorWindow(avaloniaBitmap).Show();
                }
            }

            catch (Exception ex)

            {

                System.Diagnostics.Debug.WriteLine("Помилка створення скріншота всіх моніторів: " + ex.Message);

            }

        }

    }
}
