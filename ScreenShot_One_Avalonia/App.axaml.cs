using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Layout;
using System;
using System.Linq;
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

                // Самоперевірка панелі трею для CI (SO_SELFTEST_TRAY=1): «натискаємо» значок і пишемо в журнал,
                // чи панель відкрилась і не закрилась одразу
                if (Environment.GetEnvironmentVariable("SO_SELFTEST_TRAY") == "1")
                    RunTraySelfTestAsync().FireAndForget("Самоперевірка панелі трею");
                if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("SO_SELFTEST_KEYS") == "1")
                    RunKeysSelfTestAsync().FireAndForget("Самоперевірка показу клавіш");
                // Відеоредактор (Windows): кнопка «Обрізати» натискається з першого разу (шлях до тестового відео в змінній)
                if (Environment.GetEnvironmentVariable("SO_SELFTEST_VEDITOR") is { Length: > 0 } testVideo)
                    RunVideoEditorSelfTestAsync(testVideo).FireAndForget("Самоперевірка відеоредактора");
                // Оверлей виділення має накривати весь екран (на Mac — без «рідного» повноекранного режиму)
                if (Environment.GetEnvironmentVariable("SO_SELFTEST_CAPTURE") == "1")
                    RunCaptureOverlaySelfTestAsync().FireAndForget("Самоперевірка оверлея виділення");

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

        private static byte[] ReadAsset(string name)
        {
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri($"avares://Screenshot_One/Assets/{name}"));
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        private void InitializeTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)

        {

            // macOS: власний значок у рядку меню — клік відкриває панель, як на Windows
            // (TrayIcon з Avalonia на Mac події кліку не дає). Якщо не вдалося — звичайний TrayIcon з меню.
            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    MacStatusItem.Clicked += () => TrayPanelWindow.Toggle(ScreenService.GetCursorPosition());
                    MacStatusItem.Create(ReadAsset("tray.png"), "Screenshot One");
                    VideoRecorderManager.OnStatusChanged += isRecording =>
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                            MacStatusItem.SetImage(ReadAsset(isRecording ? "tray-recording.png" : "tray.png")));
                    return;
                }
                catch (Exception ex) { AppLog.Error(ex, "Mac: значок у рядку меню — повертаємось до TrayIcon"); }
            }

            _trayIcon = new TrayIcon

            {

                Icon = LoadTrayIcon(isRecording: false),

                ToolTipText = "Screenshot One"

            };

            // Windows: лівий клік — панель з плитками (TrayPanelWindow)
            _trayIcon.Clicked += (_, _) => TrayPanelWindow.Toggle(ScreenService.GetCursorPosition());
            // Правий клік: Avalonia не дає події, а одразу показує власне вікно-меню «AvaloniaTrayPopupRoot_…».
            // Перехоплюємо його появу — ховаємо меню й відкриваємо ту саму панель. Не спрацює — лишиться меню.
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => InterceptTrayMenu(w));

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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

        /// <summary>Самоперевірка показу клавіш (SO_SELFTEST_KEYS=1, Windows): програма сама «натискає» Ctrl+C.</summary>
        private static async System.Threading.Tasks.Task RunKeysSelfTestAsync()
        {
            await System.Threading.Tasks.Task.Delay(15000);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                KeystrokeOverlay.Start(ScreenService.GetPrimaryScreen()?.Bounds ?? new PixelRect(0, 0, 1920, 1080)));
            await System.Threading.Tasks.Task.Delay(500);
            const uint KEYUP = 0x2;
            keybd_event(0x11, 0, 0, UIntPtr.Zero);     // Ctrl
            keybd_event(0x43, 0, 0, UIntPtr.Zero);     // C
            keybd_event(0x43, 0, KEYUP, UIntPtr.Zero);
            keybd_event(0x11, 0, KEYUP, UIntPtr.Zero);
            await System.Threading.Tasks.Task.Delay(1000);
            string? shown = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => KeystrokeOverlay.LastShown);
            AppLog.Write($"[SelfTest] клавіші: «{shown}» = {shown == "Ctrl + C"}");

            // Знімок вікна: список вікон інших програм (CI запускає Блокнот)
            var windows = WindowLocator.GetWindows();
            AppLog.Write($"[SelfTest] вікна інших програм: {windows.Count} — {windows.Count > 0}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(KeystrokeOverlay.Stop);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool SetCursorPos(int x, int y);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);

        /// <summary>
        /// Самоперевірка відеоредактора (SO_SELFTEST_VEDITOR=шлях до відео, Windows): справжні кліки мишею по «Обрізати»
        /// після паузи, за яку з'являється підказка. Спершу зі старим розміщенням підказки (біля курсора), потім з новим (над кнопкою).
        /// </summary>
        private static async System.Threading.Tasks.Task RunVideoEditorSelfTestAsync(string videoPath)
        {
            // Windows — після інших самоперевірок; Mac (лише завантаження відео) — раніше, бо CI там чекає 25 с
            await System.Threading.Tasks.Task.Delay(OperatingSystem.IsWindows() ? 21000 : 10000);
            VideoEditorWindow? w = null;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { w = new VideoEditorWindow(videoPath); w.Show(); });
            await System.Threading.Tasks.Task.Delay(3000);
            bool loaded = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => w!.FindControl<TextBlock>("TxtLoading")?.IsVisible == false);
            AppLog.Write($"[SelfTest] відео завантажено в редактор: {loaded}");
            if (!OperatingSystem.IsWindows())
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => w!.Close());
                return;
            }

            async System.Threading.Tasks.Task<int> ClicksAsync(Avalonia.Controls.PlacementMode? placement)
            {
                int ok = 0;
                for (int i = 0; i < 3; i++)
                {
                    var (pt, before) = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var b = w!.FindControl<Avalonia.Controls.Primitives.ToggleButton>("BtnTrimMode")!;
                        if (placement is { } p) ToolTip.SetPlacement(b, p); else b.ClearValue(ToolTip.PlacementProperty);
                        var c = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), w) ?? default;
                        return (w.PointToScreen(c), b.IsChecked == true);
                    });
                    SetCursorPos(pt.X + 1, pt.Y + 1);
                    await System.Threading.Tasks.Task.Delay(100);
                    SetCursorPos(pt.X, pt.Y);
                    await System.Threading.Tasks.Task.Delay(1500); // за цей час з'являється підказка
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero); // LEFTDOWN
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero); // LEFTUP
                    await System.Threading.Tasks.Task.Delay(700);
                    bool after = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        w!.FindControl<Avalonia.Controls.Primitives.ToggleButton>("BtnTrimMode")!.IsChecked == true);
                    if (after != before) ok++;
                }
                return ok;
            }

            int oldPlacement = await ClicksAsync(Avalonia.Controls.PlacementMode.Pointer);
            int newPlacement = await ClicksAsync(null);
            AppLog.Write($"[SelfTest] кнопка обрізки: підказка біля курсора {oldPlacement}/3, над кнопкою {newPlacement}/3 — {newPlacement == 3}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => w!.Close());
        }

        /// <summary>Самоперевірка оверлея виділення (SO_SELFTEST_CAPTURE=1): відкриваємо його й звіряємо з екраном.</summary>
        private static async System.Threading.Tasks.Task RunCaptureOverlaySelfTestAsync()
        {
            await System.Threading.Tasks.Task.Delay(18000);
            MainWindow? w = null;
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { w = new MainWindow(); w.Show(); });
            await System.Threading.Tasks.Task.Delay(2000);
            string result = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => DescribeOverlay(w!));
            AppLog.Write($"[SelfTest] оверлей виділення: {result}");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => w!.Close());
        }

        private static string DescribeOverlay(Window w)
        {
            if (OperatingSystem.IsMacOS())
            {
                if (MacWindow.Describe(w) is not { } d) return "немає NSWindow — False";
                bool okMac = !d.NativeFullScreen && d.Level >= 1000 && d.Screen.Width > 0
                    && Math.Abs(d.Frame.X - d.Screen.X) < 1 && Math.Abs(d.Frame.Y - d.Screen.Y) < 1
                    && Math.Abs(d.Frame.Width - d.Screen.Width) < 1 && Math.Abs(d.Frame.Height - d.Screen.Height) < 1;
                return $"рамка {d.Frame}, екран {d.Screen}, рівень {d.Level}, рідний повноекранний {d.NativeFullScreen} — {okMac}";
            }
            var tl = w.PointToScreen(new Point(0, 0));
            var br = w.PointToScreen(new Point(w.Bounds.Width, w.Bounds.Height));
            var screen = w.Screens.ScreenFromPoint(tl);
            var b = screen?.Bounds ?? default;
            bool ok = screen != null && Math.Abs(tl.X - b.X) <= 2 && Math.Abs(tl.Y - b.Y) <= 2
                && Math.Abs(br.X - b.Right) <= 2 && Math.Abs(br.Y - b.Bottom) <= 2;
            return $"вікно {tl.X},{tl.Y}–{br.X},{br.Y}, екран {b} — {ok}";
        }

        public static bool IsTrayMenuWindow(Window w) => w.Name?.StartsWith("AvaloniaTrayPopupRoot_", StringComparison.Ordinal) == true;

        private static void InterceptTrayMenu(Window w)
        {
            if (!IsTrayMenuWindow(w)) return;
            try
            {
                w.Opacity = 0;
                Avalonia.Threading.Dispatcher.UIThread.Post(w.Close);
                AppLog.Write("[TrayPanel] правий клік — замість меню відкриваємо панель");
                TrayPanelWindow.Toggle(ScreenService.GetCursorPosition());
            }
            catch (Exception ex) { AppLog.Error(ex, "Правий клік по треєві"); }
        }

        private static async System.Threading.Tasks.Task RunTraySelfTestAsync()
        {
            await System.Threading.Tasks.Task.Delay(6000);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (OperatingSystem.IsMacOS() && MacStatusItem.IsCreated) MacStatusItem.PerformClick();
                else TrayPanelWindow.Toggle(null);
            });
            await System.Threading.Tasks.Task.Delay(2500);
            bool open = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => TrayPanelWindow.IsOpen);
            AppLog.Write($"[SelfTest] панель трею відкрита через 2,5 с: {open}");

            if (OperatingSystem.IsWindows())
            {
                // Правий клік: імітуємо вікно-меню, яке показує Avalonia, — замість нього має відкритись панель
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { if (TrayPanelWindow.IsOpen) TrayPanelWindow.Toggle(null); });
                await System.Threading.Tasks.Task.Delay(1000);
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    new Window { Name = "AvaloniaTrayPopupRoot_Screenshot One", Width = 10, Height = 10, ShowInTaskbar = false }.Show());
                await System.Threading.Tasks.Task.Delay(2500);
                bool openRight = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => TrayPanelWindow.IsOpen);
                bool menuGone = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    !(Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d && d.Windows.Any(IsTrayMenuWindow)));
                AppLog.Write($"[SelfTest] правий клік → панель: {openRight && menuGone}");
            }
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
