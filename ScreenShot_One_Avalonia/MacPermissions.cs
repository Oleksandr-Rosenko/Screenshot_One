using System;
using System.Threading.Tasks;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// macOS: дозвіл «Конфіденційність і безпека → Запис екрана».
    /// Без нього система мовчки віддає знімок лише зі шпалерами (без вікон) — користувач бачить «порожній» скріншот.
    /// Попередження показується максимум раз за сесію; CGPreflightScreenCaptureAccess кешує стан до перезапуску програми,
    /// тому після надання дозволу програму треба перезапустити.
    /// </summary>
    public static class MacPermissions
    {
        private static bool _warnedThisSession;

        /// <summary>Перевіряє дозвіл і, якщо його немає, пояснює, що робити. Нічого не блокує.</summary>
        public static async Task WarnIfScreenCaptureDeniedAsync()
        {
            if (!OperatingSystem.IsMacOS() || _warnedThisSession) return;
            if (MacCoreGraphics.HasScreenCaptureAccess()) return;

            _warnedThisSession = true;
            AppLog.Write("macOS: немає дозволу «Запис екрана»");

            // Додає програму до списку в Системних налаштуваннях (системний запит показується один раз)
            MacCoreGraphics.RequestScreenCaptureAccess();

            try
            {
                var box = MessageBoxManager.GetMessageBoxStandard(
                    Loc.T("Mac_PermissionTitle"),
                    Loc.T("Mac_PermissionText"),
                    ButtonEnum.YesNo, Icon.Warning);

                if (await box.ShowWindowAsync() == ButtonResult.Yes)
                    MacCoreGraphics.OpenScreenCaptureSettings();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
            }
        }
    }
}
