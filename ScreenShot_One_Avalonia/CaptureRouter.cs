using Avalonia.Media.Imaging;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Куди йде щойно зроблений знімок: у редактор (як раніше) або в плаваючу мініатюру —
    /// залежно від налаштування «Після знімка». Отримувач стає власником бітмапа.
    /// </summary>
    public static class CaptureRouter
    {
        public static void Deliver(Bitmap bitmap)
        {
            if (SettingsManager.Current.AfterCapture == "QuickAccess")
                QuickAccessWindow.ShowFor(bitmap);
            else
                new EditorWindow(bitmap).Show();
        }
    }
}
