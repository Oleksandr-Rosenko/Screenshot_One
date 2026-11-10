using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using System.Diagnostics;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using SukiUI;

namespace ScreenShot_One_Avalonia
{
    public class AppSettings
    {
        public int RegionKey { get; set; } = 0x2C;
        public int RegionModifier { get; set; } = 0;

        // Змінено дефолт для одного екрана на Shift
        public int FullScreenKey { get; set; } = 0x2C;
        public int FullScreenModifier { get; set; } = 4; // Shift

        // Ctrl + Shift + PrintScreen. Раніше було Ctrl + Shift + R — воно «з'їдало» жорстке оновлення сторінки в браузерах.
        public int VideoKey { get; set; } = 0x2C;
        public int VideoModifier { get; set; } = 6; // Ctrl + Shift

        // Змінено дефолт для всіх екранів на Ctrl
        public int AllScreensKey { get; set; } = 0x2C;
        public int AllScreensModifier { get; set; } = 2; // Ctrl

        public bool CopyToClipboardOnOpen { get; set; } = false;
        public bool CopyLinkOnUpload { get; set; } = true;
        public bool StartOnBoot { get; set; } = false;
        public string Theme { get; set; } = "Dark";
        public DateTime? LastUpdateDismissedUtc { get; set; }
        public string? LastDismissedVersion { get; set; }

        // Версія схеми налаштувань — для разових міграцій (див. SettingsManager.Migrate)
        public int SettingsVersion { get; set; } = 0;
    }

    public static class SettingsManager
    {
        private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One");
        private static readonly string ConfigPath = Path.Combine(ConfigDir, "settings.json");

        public static AppSettings Current { get; set; } = new AppSettings();

        public static event Action? OnRegionCapture;
        public static event Action? OnFullScreenCapture;
        public static event Action? OnVideoRecord;
        public static event Action? OnAllScreensCapture;

        private const int CurrentSettingsVersion = 2;

        private static bool _isSuspended;

        /// <summary>
        /// true — гарячі клавіші тимчасово вимкнені (поки відкрите вікно налаштувань).
        /// При поверненні в false комбінації перечитуються з Current.
        /// </summary>
        public static bool IsSuspended
        {
            get => _isSuspended;
            set
            {
                _isSuspended = value;
                try { HotkeyService.SetSuspended(value); }
                catch (Exception ex) { Debug.WriteLine($"Помилка перемикання гарячих клавіш: {ex.Message}"); }
            }
        }

        public static void LoadAndRegister()
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);

                if (File.Exists(ConfigPath))
                {
                    try
                    {
                        string json = File.ReadAllText(ConfigPath);
                        Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Помилка читання старих налаштувань: {ex.Message}. Видаляємо битий файл.");
                        try { File.Delete(ConfigPath); } catch { }
                        Current = new AppSettings();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Критична помилка доступу до файлової системи: {ex.Message}");
                Current = new AppSettings();
            }

            if (Migrate(Current)) Save();

            // Кожен крок у своєму try: раніше на macOS виняток із віндового hook'а
            // пропускав автозапуск і застосування збереженої теми.
            try
            {
                HotkeyService.Start(OnHotkeyPressed);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Помилка підключення гарячих клавіш: {ex.Message}");
            }

            UpdateRegistryAutoStart();
            ApplyTheme(Current.Theme);
        }

        /// <summary>
        /// Разові міграції налаштувань. Повертає true, якщо щось змінилось і файл треба перезберегти.
        /// </summary>
        public static bool Migrate(AppSettings settings)
        {
            if (settings.SettingsVersion >= CurrentSettingsVersion) return false;

            if (OperatingSystem.IsMacOS())
            {
                // На Mac гарячі клавіші раніше не працювали взагалі, а віндові дефолти з PrintScreen
                // на ноутбуках Apple натиснути неможливо — ставимо маківські дефолти.
                ApplyMacDefaultHotkeys(settings);
            }
            else if (settings.VideoKey == 0x52 && settings.VideoModifier == 6)
            {
                // Старий дефолт Ctrl + Shift + R → Ctrl + Shift + PrintScreen
                settings.VideoKey = 0x2C;
                settings.VideoModifier = 6;
            }

            settings.SettingsVersion = CurrentSettingsVersion;
            return true;
        }

        private static void ApplyMacDefaultHotkeys(AppSettings settings)
        {
            // Cmd + Shift + 7 / 8 / 9 / 0. Цифри 3–6 з Cmd + Shift зайняті системними скріншотами macOS.
            const int cmdShift = HotkeyModifiers.Meta | HotkeyModifiers.Shift;
            settings.RegionKey = 0x37; settings.RegionModifier = cmdShift;
            settings.FullScreenKey = 0x38; settings.FullScreenModifier = cmdShift;
            settings.AllScreensKey = 0x39; settings.AllScreensModifier = cmdShift;
            settings.VideoKey = 0x30; settings.VideoModifier = cmdShift;
        }

        private static void OnHotkeyPressed(HotkeyAction action)
        {
            // Hook на Windows працює в окремому потоці, тому завжди переходимо в UI-потік.
            Dispatcher.UIThread.Post(() =>
            {
                switch (action)
                {
                    case HotkeyAction.Region: OnRegionCapture?.Invoke(); break;
                    case HotkeyAction.FullScreen: OnFullScreenCapture?.Invoke(); break;
                    case HotkeyAction.Video: OnVideoRecord?.Invoke(); break;
                    case HotkeyAction.AllScreens: OnAllScreensCapture?.Invoke(); break;
                }
            });
        }

        public static void SaveAndRegister()
        {
            Save();
            UpdateRegistryAutoStart();
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Помилка збереження налаштувань: {ex.Message}");
            }
        }

        public static void ApplyTheme(string? themeName)
        {
            var variant = string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            Dispatcher.UIThread.Post(() =>
            {
                try
                {
                    if (Application.Current != null)
                    {
                        Application.Current.RequestedThemeVariant = variant;
                    }
                    var sukiTheme = SukiTheme.GetInstance();
                    sukiTheme.ChangeBaseTheme(variant);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Помилка застосування теми: {ex.Message}");
                }
            });
        }

        public static void UnregisterAll()
        {
            try { HotkeyService.Stop(); }
            catch (Exception ex) { Debug.WriteLine($"Помилка відключення гарячих клавіш: {ex.Message}"); }
        }

        private static void UpdateRegistryAutoStart()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return;
                string appName = "Screenshot_One";
                string? exePath = Process.GetCurrentProcess().MainModule?.FileName;
                if (exePath == null) return;

                using (RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                    {
                        if (Current.StartOnBoot) key.SetValue(appName, $"\"{exePath}\"");
                        else key.DeleteValue(appName, false);
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Помилка роботи з реєстром (Автозавантаження): {ex.Message}");
            }
        }
    }
}