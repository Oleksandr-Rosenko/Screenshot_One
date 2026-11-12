using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using SukiUI;
using SukiUI.Models;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Акцент палітри «Хвоя і м'ята» для контролів SukiUI (кнопки Primary, перемикачі, виділення).
    /// SukiColorTheme один на обидві теми, а наш м'ятний акцент для темної й світлої різний
    /// (світліший на темному тлі, глибший на світлому), тому при зміні теми міняємо й колірну тему.
    /// Решта кольорів — у App.axaml (ThemeDictionaries).
    /// </summary>
    public static class AppTheme
    {
        public static readonly Color AccentDark = Color.Parse("#62C9A0");
        public static readonly Color AccentLight = Color.Parse("#2E8F6F");

        private static readonly SukiColorTheme MintDark = new SukiColorTheme("MintDark", AccentDark, AccentDark);
        private static readonly SukiColorTheme MintLight = new SukiColorTheme("MintLight", AccentLight, AccentLight);
        private static bool _registered;

        /// <summary>
        /// Колір-токен з App.axaml для поточної теми. Звичайний FindResource до показу вікна не знає теми
        /// й не бачить ThemeDictionaries (у 2.7.0 через це кнопка Google Диска у відеоредакторі ставала сірою
        /// з невидимим текстом), тому тема береться явно: вікна, а якщо її ще немає — програми.
        /// </summary>
        public static IBrush Brush(Avalonia.StyledElement element, string key, IBrush fallback)
        {
            try
            {
                var app = Avalonia.Application.Current;
                ThemeVariant? variant = element.ActualThemeVariant;
                if (variant == null || variant == ThemeVariant.Default) variant = app?.ActualThemeVariant;
                object? value = null;
                if (!element.TryFindResource(key, variant, out value) && app != null)
                    app.TryFindResource(key, variant ?? app.ActualThemeVariant, out value);
                return value switch
                {
                    IBrush brush => brush,
                    Color color => new SolidColorBrush(color),
                    _ => fallback
                };
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
                return fallback;
            }
        }

        public static void ApplyAccent(ThemeVariant variant)
        {
            try
            {
                var suki = SukiTheme.GetInstance();
                if (!_registered)
                {
                    suki.AddColorThemes(new[] { MintDark, MintLight });
                    _registered = true;
                }
                suki.ChangeColorTheme(variant == ThemeVariant.Light ? MintLight : MintDark);
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
            }
        }
    }
}
