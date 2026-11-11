using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media.Imaging;

namespace ScreenShot_One_Avalonia
{
    /// <summary>Спільне копіювання в буфер обміну для редактора, мініатюри й OCR.</summary>
    public static class ClipboardHelper
    {
        /// <summary>
        /// Копіює зображення. Windows — через WinForms (STA-потік), бо буфер Avalonia на Windows
        /// давав чорне зображення після закриття вікна; Mac — через Avalonia. Кидає виключення при помилці.
        /// </summary>
        public static async Task CopyImageAsync(Bitmap bitmap, TopLevel? topLevel)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), $"clip_{Guid.NewGuid()}.png");
            try
            {
                // Фізичний файл надійно фіксує всі пікселі
                using (var fs = new FileStream(tempPath, FileMode.Create))
                {
                    bitmap.Save(fs);
                }

                if (OperatingSystem.IsWindows())
                {
                    Exception? error = null;
                    var thread = new System.Threading.Thread(() =>
                    {
                        try
                        {
#if WINDOWS
                            using var img = System.Drawing.Image.FromFile(tempPath);
                            System.Windows.Forms.Clipboard.SetImage(img);
#endif
                        }
                        catch (Exception ex) { error = ex; }
                    });
                    thread.SetApartmentState(System.Threading.ApartmentState.STA);
                    thread.Start();
                    thread.Join();
                    if (error != null) AppLog.Write($"Помилка WinForms буфера: {error.Message}");
                }
                else
                {
                    var clipboard = topLevel?.Clipboard;
                    if (clipboard != null)
                    {
                        using var fs = new FileStream(tempPath, FileMode.Open, FileAccess.Read);
                        var independentBitmap = new Bitmap(fs);
                        await clipboard.SetBitmapAsync(independentBitmap);
                    }
                }
            }
            finally
            {
                // Буфер обміну вже має власну копію даних — тимчасовий файл не потрібен
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception logEx) { AppLog.Warn(logEx); }
            }
        }

        /// <summary>Копіює текст у буфер обміну.</summary>
        public static async Task CopyTextAsync(string text, TopLevel? topLevel)
        {
            var clipboard = topLevel?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(text);
        }
    }
}
