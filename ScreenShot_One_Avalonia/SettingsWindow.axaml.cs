using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Win32.Input;
using System;
// using System.Windows.Forms; (removed for cross-platform)
using SukiUI.Controls;
using Velopack;

namespace ScreenShot_One_Avalonia
{
    public partial class SettingsWindow : SukiWindow
    {
        private string _initialTheme = "Dark";
        private bool _isSaved = false;
        public SettingsWindow()
        {
            InitializeComponent();

            UpdateVersionText();

            SettingsManager.IsSuspended = true;
            LoadCurrentSettings();
        }

        private void UpdateVersionText()
        {
            var assemblyVersion = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            string currentVersionStr = assemblyVersion != null
                ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}"
                : Loc.T("Settings_UnknownVersion");

            try
            {
                string repoUrl = "https://github.com/Oleksandr-Rosenko/Screenshot_One";
                var source = new Velopack.Sources.GithubSource(repoUrl, accessToken: null, prerelease: false);
                var updateManager = new Velopack.UpdateManager(source);

                if (updateManager.IsInstalled)
                {
                    currentVersionStr = updateManager.CurrentVersion?.ToString() ?? currentVersionStr;
                }
                else
                {
                    if (currentVersionStr != Loc.T("Settings_UnknownVersion"))
                    {
                        currentVersionStr += Loc.T("Settings_DevMode");
                    }
                }
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }

            TxtCurrentVersion.Text = Loc.F("Settings_CurrentVersion", currentVersionStr);
        }

        private void LoadCurrentSettings()
        {
            ChkStartOnBoot.IsChecked = SettingsManager.Current.StartOnBoot;
            ChkCopyToClipboard.IsChecked = SettingsManager.Current.CopyToClipboardOnOpen;
            ChkCopyLink.IsChecked = SettingsManager.Current.CopyLinkOnUpload;

            _initialTheme = SettingsManager.Current.Theme ?? "Dark";
            ComboTheme.SelectedIndex = string.Equals(_initialTheme, "Light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;

            _initialLanguage = SettingsManager.Current.Language ?? "auto";
            _languageReady = false;
            ComboLanguage.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageCodes, _initialLanguage));
            _languageReady = true;

            TxtRegionKey.Text = FormatHotkey(SettingsManager.Current.RegionModifier, SettingsManager.Current.RegionKey);
            TxtFullScreenKey.Text = FormatHotkey(SettingsManager.Current.FullScreenModifier, SettingsManager.Current.FullScreenKey);
            TxtVideoKey.Text = FormatHotkey(SettingsManager.Current.VideoModifier, SettingsManager.Current.VideoKey);

            // НОВЕ: Завантажуємо нову клавішу в текстове поле
            TxtAllScreensKey.Text = FormatHotkey(SettingsManager.Current.AllScreensModifier, SettingsManager.Current.AllScreensKey);
            TxtOcrKey.Text = FormatHotkey(SettingsManager.Current.OcrModifier, SettingsManager.Current.OcrKey);

            ComboAfterCapture.SelectedIndex = SettingsManager.Current.AfterCapture == "QuickAccess" ? 1 : 0;
            ComboSaveFormat.SelectedIndex = ImageExport.PreferredExtension == "jpg" ? 1 : 0;
            ChkAutoSave.IsChecked = SettingsManager.Current.AutoSaveToFolder;
            _saveFolder = SettingsManager.Current.SaveFolder;
            TxtSaveFolder.Text = ImageExport.SaveFolder;
        }

        // null — папка за замовчуванням
        private string? _saveFolder;

        private async void BtnChooseFolder_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var options = new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = Loc.T("Settings_ChooseFolderTitle"),
                    AllowMultiple = false
                };
                var start = await StorageProvider.TryGetFolderFromPathAsync(TxtSaveFolder.Text ?? ImageExport.DefaultFolder);
                if (start != null) options.SuggestedStartLocation = start;

                var folders = await StorageProvider.OpenFolderPickerAsync(options);
                if (folders.Count == 0) return;

                string? path = folders[0].TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) return;
                _saveFolder = path;
                TxtSaveFolder.Text = path;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Вибір папки");
            }
        }

        private string FormatHotkey(int modifier, int vkCode)
        {
            bool isMac = OperatingSystem.IsMacOS();
            string text = "";
            if ((modifier & HotkeyModifiers.Ctrl) != 0) text += "Ctrl + ";
            if ((modifier & HotkeyModifiers.Alt) != 0) text += isMac ? "Option + " : "Alt + ";
            if ((modifier & HotkeyModifiers.Shift) != 0) text += "Shift + ";
            if ((modifier & HotkeyModifiers.Meta) != 0) text += isMac ? "Cmd + " : "Win + ";

            if (vkCode == 0x2C) text += "PrintScreen";
            else text += ConvertFromVirtualKey(vkCode);

            return text;
        }

        private string ConvertFromVirtualKey(int vkCode)
        {
            if (vkCode >= 0x41 && vkCode <= 0x5A) return ((char)vkCode).ToString();
            if (vkCode >= 0x30 && vkCode <= 0x39) return ((char)vkCode).ToString();
            if (vkCode >= 0x70 && vkCode <= 0x87) return $"F{vkCode - 0x70 + 1}";
            if (vkCode >= 0x60 && vkCode <= 0x69) return $"NumPad{vkCode - 0x60}";

            return vkCode switch
            {
                0x2C => "PrintScreen",
                0x2E => "Delete",
                0x24 => "Home",
                0x23 => "End",
                0x21 => "PageUp",
                0x22 => "PageDown",
                0x2D => "Insert",
                0x1B => "Escape",
                0x20 => "Space",
                0x09 => "Tab",
                0x0D => "Enter",
                0x08 => "Back",
                0x26 => "Up",
                0x28 => "Down",
                0x25 => "Left",
                0x27 => "Right",
                0xC0 => "~",
                _ => $"Key({vkCode})"
            };
        }

        private void Hotkey_KeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            e.Handled = true;

            if (e.Key == Avalonia.Input.Key.LeftCtrl || e.Key == Avalonia.Input.Key.RightCtrl ||
                e.Key == Avalonia.Input.Key.LeftShift || e.Key == Avalonia.Input.Key.RightShift ||
                e.Key == Avalonia.Input.Key.LeftAlt || e.Key == Avalonia.Input.Key.RightAlt ||
                e.Key == Avalonia.Input.Key.LWin || e.Key == Avalonia.Input.Key.RWin)
                return;

            ProcessHotkey(sender as Avalonia.Controls.TextBox, e.Key, e.KeyModifiers);
        }

        private void Hotkey_KeyUp(object? sender, Avalonia.Input.KeyEventArgs e)
        {
            if (e.Key == Avalonia.Input.Key.Snapshot)
            {
                ProcessHotkey(sender as Avalonia.Controls.TextBox, e.Key, e.KeyModifiers);
            }
        }

        private void ProcessHotkey(Avalonia.Controls.TextBox? textBox, Avalonia.Input.Key avaloniaKey, Avalonia.Input.KeyModifiers modifiers)
        {
            if (textBox == null) return;

            int vkCode = ConvertToVirtualKey(avaloniaKey);
            if (vkCode == 0) return;

            int modCode = 0;
            if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Alt)) modCode |= HotkeyModifiers.Alt;
            if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control)) modCode |= HotkeyModifiers.Ctrl;
            if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift)) modCode |= HotkeyModifiers.Shift;
            if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Meta)) modCode |= HotkeyModifiers.Meta; // Win / Cmd

            if (textBox.Name == "TxtRegionKey")
            {
                SettingsManager.Current.RegionKey = vkCode;
                SettingsManager.Current.RegionModifier = modCode;
            }
            else if (textBox.Name == "TxtFullScreenKey")
            {
                SettingsManager.Current.FullScreenKey = vkCode;
                SettingsManager.Current.FullScreenModifier = modCode;
            }
            else if (textBox.Name == "TxtVideoKey")
            {
                SettingsManager.Current.VideoKey = vkCode;
                SettingsManager.Current.VideoModifier = modCode;
            }
            else if (textBox.Name == "TxtAllScreensKey") // НОВЕ: Обробка зміни нової клавіші
            {
                SettingsManager.Current.AllScreensKey = vkCode;
                SettingsManager.Current.AllScreensModifier = modCode;
            }
            else if (textBox.Name == "TxtOcrKey")
            {
                SettingsManager.Current.OcrKey = vkCode;
                SettingsManager.Current.OcrModifier = modCode;
            }

            // Той самий формат, що й при відкритті вікна (раніше цифри показувались як «D7»)
            textBox.Text = FormatHotkey(modCode, vkCode);
        }

        private int ConvertToVirtualKey(Avalonia.Input.Key key)
        {
            if (key >= Avalonia.Input.Key.A && key <= Avalonia.Input.Key.Z) return (int)key - (int)Avalonia.Input.Key.A + 0x41;
            if (key >= Avalonia.Input.Key.D0 && key <= Avalonia.Input.Key.D9) return (int)key - (int)Avalonia.Input.Key.D0 + 0x30;
            if (key >= Avalonia.Input.Key.F1 && key <= Avalonia.Input.Key.F24) return (int)key - (int)Avalonia.Input.Key.F1 + 0x70;
            if (key >= Avalonia.Input.Key.NumPad0 && key <= Avalonia.Input.Key.NumPad9) return (int)key - (int)Avalonia.Input.Key.NumPad0 + 0x60;

            switch (key)
            {
                case Avalonia.Input.Key.Snapshot: return 0x2C;
                case Avalonia.Input.Key.Delete: return 0x2E;
                case Avalonia.Input.Key.Home: return 0x24;
                case Avalonia.Input.Key.End: return 0x23;
                case Avalonia.Input.Key.PageUp: return 0x21;
                case Avalonia.Input.Key.PageDown: return 0x22;
                case Avalonia.Input.Key.Insert: return 0x2D;
                case Avalonia.Input.Key.Escape: return 0x1B;
                case Avalonia.Input.Key.Space: return 0x20;
                case Avalonia.Input.Key.Tab: return 0x09;
                case Avalonia.Input.Key.Enter: return 0x0D;
                case Avalonia.Input.Key.Back: return 0x08;
                case Avalonia.Input.Key.Up: return 0x26;
                case Avalonia.Input.Key.Down: return 0x28;
                case Avalonia.Input.Key.Left: return 0x25;
                case Avalonia.Input.Key.Right: return 0x27;
                case Avalonia.Input.Key.OemTilde: return 0xC0;
            }
            return 0;
        }

        // Порядок пунктів ComboLanguage
        private static readonly string[] LanguageCodes = { "auto", "uk", "en", "ru" };
        private string _initialLanguage = "auto";
        private bool _languageReady;

        private string SelectedLanguage => ComboLanguage.SelectedIndex >= 0 && ComboLanguage.SelectedIndex < LanguageCodes.Length
            ? LanguageCodes[ComboLanguage.SelectedIndex] : "auto";

        /// <summary>Мова міняється одразу (як тема); якщо закрити без «Зберегти» — повертається попередня.</summary>
        private void ComboLanguage_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ComboLanguage == null || !_languageReady) return;
            Loc.Apply(SelectedLanguage);
            UpdateVersionText();
        }

        private void ComboTheme_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ComboTheme == null) return;
            string theme = ComboTheme.SelectedIndex == 1 ? "Light" : "Dark";
            SettingsManager.ApplyTheme(theme);
        }

        /// <summary>
        /// Вікно не повинне виходити за межі екрана (раніше на 1080p з масштабом 150% шапка з кнопкою
        /// «закрити» опинялась за верхнім краєм). Висота обмежується робочою областю екрана, решта прокручується.
        /// </summary>
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            try
            {
                var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
                if (screen == null) return;
                var area = screen.WorkingArea;
                double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;
                double maxHeight = area.Height / scaling - 40;
                if (Height > maxHeight) Height = Math.Max(MinHeight, maxHeight);

                int w = (int)(Width * scaling), h = (int)(Height * scaling);
                Position = new Avalonia.PixelPoint(area.X + (area.Width - w) / 2, area.Y + Math.Max(0, (area.Height - h) / 2));
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            if (!_isSaved)
            {
                SettingsManager.ApplyTheme(_initialTheme);
                if (SelectedLanguage != _initialLanguage) Loc.Apply(_initialLanguage);
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            SettingsManager.IsSuspended = false;
            base.OnClosed(e);
        }

        private void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            SettingsManager.Current.StartOnBoot = ChkStartOnBoot.IsChecked == true;
            SettingsManager.Current.CopyToClipboardOnOpen = ChkCopyToClipboard.IsChecked == true;
            SettingsManager.Current.CopyLinkOnUpload = ChkCopyLink.IsChecked == true;
            SettingsManager.Current.Theme = ComboTheme.SelectedIndex == 1 ? "Light" : "Dark";
            SettingsManager.Current.Language = SelectedLanguage;
            SettingsManager.Current.AfterCapture = ComboAfterCapture.SelectedIndex == 1 ? "QuickAccess" : "Editor";
            SettingsManager.Current.SaveFormat = ComboSaveFormat.SelectedIndex == 1 ? "jpg" : "png";
            SettingsManager.Current.AutoSaveToFolder = ChkAutoSave.IsChecked == true;
            // Папку за замовчуванням не записуємо явно, щоб вона й далі йшла за системною «Зображення»
            SettingsManager.Current.SaveFolder =
                string.IsNullOrWhiteSpace(_saveFolder) || _saveFolder == ImageExport.DefaultFolder ? null : _saveFolder;
            _isSaved = true;

            SettingsManager.SaveAndRegister();
            this.Close();
        }
    }
}
