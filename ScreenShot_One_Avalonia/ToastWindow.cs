using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace ScreenShot_One_Avalonia
{
    /// <summary>Невелике сповіщення в правому нижньому куті (не забирає фокус, ховається саме).</summary>
    public sealed class ToastWindow : Window
    {
        private readonly TextBlock _text;
        private DispatcherTimer? _closeTimer;

        public ToastWindow(string message)
        {
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            MaxWidth = 420;

            _text = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
                VerticalAlignment = VerticalAlignment.Center
            };
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 24)),
                CornerRadius = new CornerRadius(12),
                BorderBrush = new SolidColorBrush(Color.FromArgb(85, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(16, 12),
                Margin = new Thickness(6),
                Child = _text
            };
        }

        public static ToastWindow ShowMessage(string message, int closeAfterMs = 0)
        {
            var toast = new ToastWindow(message);
            toast.Show();
            if (closeAfterMs > 0) toast.CloseAfter(closeAfterMs);
            return toast;
        }

        public void SetMessage(string message, int closeAfterMs = 0)
        {
            _text.Text = message;
            Dispatcher.UIThread.Post(PlaceInCorner, DispatcherPriority.Loaded);
            if (closeAfterMs > 0) CloseAfter(closeAfterMs);
        }

        public void CloseAfter(int ms)
        {
            _closeTimer?.Stop();
            _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _closeTimer.Tick += (s, e) => { _closeTimer?.Stop(); Close(); };
            _closeTimer.Start();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            Dispatcher.UIThread.Post(PlaceInCorner, DispatcherPriority.Loaded);
        }

        private void PlaceInCorner()
        {
            var screen = Screens.ScreenFromPoint(ScreenService.GetCursorPosition()) ?? Screens.Primary;
            if (screen == null) return;
            var area = screen.WorkingArea;
            double s = screen.Scaling;
            Position = new PixelPoint(
                (int)(area.Right - Bounds.Width * s - 16 * s),
                (int)(area.Bottom - Bounds.Height * s - 16 * s));
        }
    }

    /// <summary>Сценарій «виділив зону → текст у буфері обміну».</summary>
    public static class OcrFlow
    {
        public static async Task RecognizeAndCopyAsync(Bitmap bitmap)
        {
            var toast = ToastWindow.ShowMessage("Розпізнаю текст…");
            try
            {
                string text = await OcrService.RecognizeAsync(bitmap);
                if (string.IsNullOrWhiteSpace(text))
                {
                    toast.SetMessage("Текст не знайдено", 2500);
                    return;
                }

                await ClipboardHelper.CopyTextAsync(text, toast);
                string preview = text.Replace(Environment.NewLine, " ");
                if (preview.Length > 80) preview = preview.Substring(0, 80) + "…";
                toast.SetMessage($"✓ Текст скопійовано ({text.Length} симв.)\n{preview}", 3500);
            }
            catch (OcrUnavailableException ex)
            {
                toast.SetMessage(ex.Message, 8000);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "OCR");
                toast.SetMessage("Не вдалося розпізнати текст: " + ex.Message, 6000);
            }
            finally
            {
                bitmap.Dispose();
            }
        }
    }
}
