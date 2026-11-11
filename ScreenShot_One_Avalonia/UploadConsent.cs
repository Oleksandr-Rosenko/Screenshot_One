using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Одноразове попередження перед першим завантаженням у хмару.
    /// І Google Drive (доступ «усі, хто має посилання»), і catbox.moe роблять файл публічним —
    /// користувач має знати про це до того, як поділиться скріншотом із чутливими даними.
    /// </summary>
    public static class UploadConsent
    {
        public static async Task<bool> ConfirmAsync(Window owner)
        {
            if (SettingsManager.Current.PublicUploadWarningAccepted) return true;

            var box = MessageBoxManager.GetMessageBoxStandard(
                "Публічне посилання",
                "Завантажений файл буде доступний УСІМ, хто має посилання " +
                "(Google Drive — «усі, хто має посилання»; catbox.moe — повністю публічний хостинг).\n\n" +
                "Не завантажуйте скріншоти з паролями, особистими чи робочими даними.\n\n" +
                "Продовжити? (Це попередження більше не показуватиметься.)",
                ButtonEnum.YesNo, Icon.Warning);

            ButtonResult result;
            try { result = await box.ShowWindowDialogAsync(owner); }
            catch (Exception ex) { AppLog.Warn(ex); result = await box.ShowWindowAsync(); }

            if (result != ButtonResult.Yes) return false;

            SettingsManager.Current.PublicUploadWarningAccepted = true;
            try { SettingsManager.Save(); } catch (Exception ex) { AppLog.Warn(ex); }
            return true;
        }
    }
}
