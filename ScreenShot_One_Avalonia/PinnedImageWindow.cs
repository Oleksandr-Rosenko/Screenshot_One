using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Material.Icons;
using Material.Icons.Avalonia;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// «Стікер» (2.10.0): скріншот закріплено поверх усіх вікон — як шпаргалка під рукою.
    /// Перетягування мишею, коліщатко — масштаб, Ctrl/Cmd + коліщатко — прозорість, подвійний клік або Esc — закрити.
    /// Кнопки (копіювати, закрити) з'являються при наведенні; правий клік — меню.
    /// </summary>
    public sealed class PinnedImageWindow : Window
    {
        private readonly Bitmap _bitmap;
        private readonly Image _image;
        private readonly Border _actions;
        private double _zoom = 1.0;
        private readonly Size _baseSize;

        /// <summary>Закріплює копію зображення (оригінал можна звільняти).</summary>
        public static void Pin(Bitmap source, PixelPoint? near = null)
        {
            var copy = BitmapPixels.Crop(source, new PixelRect(0, 0, source.PixelSize.Width, source.PixelSize.Height));
            if (copy == null) return;
            var w = new PinnedImageWindow(copy);
            w.Show();
            if (near.HasValue) w.Position = near.Value;
            AppLog.Write($"[Pin] закріплено {source.PixelSize.Width}×{source.PixelSize.Height}");
        }

        private PinnedImageWindow(Bitmap bitmap)
        {
            _bitmap = bitmap;
            Title = Loc.T("Pin_Title");
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true;
            ShowInTaskbar = false;
            CanResize = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // Логічний розмір = пікселі / DPI-масштаб знімка; не більше 60% екрана
            double dpiScale = bitmap.Dpi.X > 0 ? bitmap.Dpi.X / 96.0 : 1.0;
            double w = bitmap.PixelSize.Width / dpiScale, h = bitmap.PixelSize.Height / dpiScale;
            var screen = Screens.Primary;
            if (screen != null)
            {
                double maxW = screen.WorkingArea.Width / screen.Scaling * 0.6, maxH = screen.WorkingArea.Height / screen.Scaling * 0.6;
                double k = Math.Min(1.0, Math.Min(maxW / w, maxH / h));
                w *= k; h *= k;
            }
            _baseSize = new Size(Math.Max(40, w), Math.Max(40, h));

            _image = new Image { Source = bitmap, Stretch = Stretch.Uniform, Width = _baseSize.Width, Height = _baseSize.Height };
            RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

            Button ActionButton(MaterialIconKind kind, string tip, Action onClick)
            {
                var b = new Button
                {
                    Content = new MaterialIcon { Kind = kind, Width = 16, Height = 16, Foreground = new SolidColorBrush(Color.Parse("#E2EDEA")) },
                    Padding = new Thickness(6), CornerRadius = new CornerRadius(8), Background = Brushes.Transparent, BorderThickness = new Thickness(0)
                };
                ToolTip.SetTip(b, tip);
                b.Click += (_, _) => onClick();
                return b;
            }

            _actions = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#E6111816")), CornerRadius = new CornerRadius(10), Padding = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8),
                Opacity = 0,
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        ActionButton(MaterialIconKind.ContentCopy, Loc.T("Quick_Copy"), () => CopyAsync().FireAndForget("Стікер: копіювання")),
                        ActionButton(MaterialIconKind.Close, Loc.T("Common_Close"), Close)
                    }
                }
            };

            var frame = new Border
            {
                Margin = new Thickness(10), CornerRadius = new CornerRadius(6), ClipToBounds = true,
                BorderBrush = new SolidColorBrush(Color.Parse("#8062C9A0")), BorderThickness = new Thickness(1),
                BoxShadow = BoxShadows.Parse("0 6 20 0 #66000000"),
                Child = new Panel { Children = { _image, _actions } }
            };
            Content = frame;

            var menu = new ContextMenu();
            MenuItem Item(string key, Action a) { var mi = new MenuItem { Header = Loc.T(key) }; mi.Click += (_, _) => a(); return mi; }
            menu.Items.Add(Item("Quick_Copy", () => CopyAsync().FireAndForget("Стікер: копіювання")));
            menu.Items.Add(Item("Pin_Opacity100", () => Opacity = 1.0));
            menu.Items.Add(Item("Pin_Opacity60", () => Opacity = 0.6));
            menu.Items.Add(Item("Pin_ResetSize", () => SetZoom(1.0)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Common_Close", Close));
            frame.ContextMenu = menu;

            frame.PointerEntered += (_, _) => _actions.Opacity = 1;
            frame.PointerExited += (_, _) => _actions.Opacity = 0;
            frame.PointerPressed += (_, e) =>
            {
                var props = e.GetCurrentPoint(this).Properties;
                if (!props.IsLeftButtonPressed) return;
                if (e.ClickCount == 2) { Close(); return; }
                if (e.Source is Visual v && IsInside<Button>(v)) return;
                BeginMoveDrag(e);
            };
            PointerWheelChanged += (_, e) =>
            {
                bool opacityMode = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
                if (opacityMode) Opacity = Math.Clamp(Opacity + (e.Delta.Y > 0 ? 0.1 : -0.1), 0.2, 1.0);
                else SetZoom(_zoom * (e.Delta.Y > 0 ? 1.1 : 1 / 1.1));
                e.Handled = true;
            };
            KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        }

        private static bool IsInside<T>(Visual v) where T : Visual
        {
            for (Visual? cur = v; cur != null; cur = cur.GetVisualParentSafe())
                if (cur is T) return true;
            return false;
        }

        private void SetZoom(double zoom)
        {
            _zoom = Math.Clamp(zoom, 0.2, 4.0);
            _image.Width = _baseSize.Width * _zoom;
            _image.Height = _baseSize.Height * _zoom;
        }

        private async System.Threading.Tasks.Task CopyAsync()
        {
            await ClipboardHelper.CopyImageAsync(_bitmap, TopLevel.GetTopLevel(this));
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _image.Source = null;
            _bitmap.Dispose();
        }
    }

    internal static class VisualParentExtensions
    {
        public static Visual? GetVisualParentSafe(this Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualParent(v);
    }
}
