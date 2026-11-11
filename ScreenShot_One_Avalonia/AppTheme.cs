using System;
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
