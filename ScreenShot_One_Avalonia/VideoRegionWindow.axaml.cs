using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Linq;

namespace ScreenShot_One_Avalonia
{
    public partial class VideoRegionWindow : Window
    {
        private Point _startPoint;
        private bool _isDragging = false;
        private bool _isLocked = false;
        private Action<int, int, int, int> _onRegionSelected;
        private Action _onCancel;
        private RectangleGeometry _cutoutGeometry;

        public VideoRegionWindow(Action<int, int, int, int> onRegionSelected, Action onCancel, string monitorDeviceName)
        {
            InitializeComponent();
            _onRegionSelected = onRegionSelected;
            _onCancel = onCancel;

            this.Cursor = new Cursor(StandardCursorType.Cross);
            this.WindowDecorations = WindowDecorations.None;
            this.Topmost = true;
            this.ShowInTaskbar = false;

            try { this.TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent }; } catch { }

            this.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

            try
            {
                // Раніше тут порівнювали Screen.ToString() з DeviceName — збігу не було ніколи, і зона завжди відкривалась на основному моніторі
                var targetScreen = ScreenService.FindScreenByDeviceName(this.Screens, monitorDeviceName) ?? this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                var bounds = targetScreen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
                this.Position = new PixelPoint(bounds.X, bounds.Y);
            }
            catch { }

            this.WindowState = WindowState.FullScreen;

            _cutoutGeometry = new RectangleGeometry(new Rect(0, 0, 0, 0));
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(new RectangleGeometry(new Rect(-10000, -10000, 30000, 30000)));
            group.Children.Add(_cutoutGeometry);

            if (OverlayPath != null)
            {
                OverlayPath.Data = group;
                OverlayPath.Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
            }

            this.KeyDown += (s, e) => { if (e.Key == Key.Escape && !_isLocked) { _onCancel?.Invoke(); this.Close(); } };

            CaptureScreenForMagnifier(monitorDeviceName);
        }

        private void CaptureScreenForMagnifier(string monitorDeviceName)
        {
            try
            {
                var screen = ScreenService.FindScreenByDeviceName(this.Screens, monitorDeviceName) ?? this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                if (screen != null)
                {
                    var avaloniaBitmap = ScreenCaptureService.Instance.CaptureScreen(screen);
                    var img = this.FindControl<Image>("MagnifierImage");
                    if (img != null && avaloniaBitmap != null) img.Source = avaloniaBitmap;
                }
            }
            catch { }
        }

        private void UpdateMagnifier(Point pos)
        {
            var magnifier = this.FindControl<Border>("MagnifierBorder");
            var magImage = this.FindControl<Image>("MagnifierImage");
            var magText = this.FindControl<TextBlock>("MagnifierText");

            if (magnifier == null || magImage == null || magText == null) return;

            magnifier.IsVisible = true;

            double offset = 25;
            double magX = pos.X + offset;
            double magY = pos.Y + offset;

            if (magX + magnifier.Width > this.Bounds.Width) magX = pos.X - magnifier.Width - offset;
            if (magY + magnifier.Height > this.Bounds.Height) magY = pos.Y - magnifier.Height - offset;

            Canvas.SetLeft(magnifier, magX);
            Canvas.SetTop(magnifier, magY);

            double zoom = 3.0;
            Canvas.SetLeft(magImage, 75 - (pos.X * zoom));
            Canvas.SetTop(magImage, 75 - (pos.Y * zoom));

            if (_isDragging && SelectionRectangle != null)
            {
                magText.Text = $"{(int)SelectionRectangle.Width} x {(int)SelectionRectangle.Height}";
            }
            else
            {
                magText.Text = $"X: {(int)pos.X} Y: {(int)pos.Y}";
            }
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            PlatformHelper.HideWindowFromScreenCapture(this);
        }

        private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_isLocked) return;

            if (OverlayCanvas == null || SelectionRectangle == null) return;
            if (!e.GetCurrentPoint(OverlayCanvas).Properties.IsLeftButtonPressed) return;

            _isDragging = true;
            _startPoint = e.GetPosition(OverlayCanvas);

            SelectionRectangle.IsVisible = true;
            Canvas.SetLeft(SelectionRectangle, _startPoint.X);
            Canvas.SetTop(SelectionRectangle, _startPoint.Y);
            SelectionRectangle.Width = 0;
            SelectionRectangle.Height = 0;
            _cutoutGeometry.Rect = new Rect(_startPoint.X, _startPoint.Y, 0, 0);
        }

        private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (_isLocked || OverlayCanvas == null) return;

            var pos = e.GetPosition(OverlayCanvas);

            UpdateMagnifier(pos);

            if (!_isDragging || SelectionRectangle == null) return;

            var x = Math.Min(pos.X, _startPoint.X);
            var y = Math.Min(pos.Y, _startPoint.Y);
            var w = Math.Max(pos.X, _startPoint.X) - x;
            var h = Math.Max(pos.Y, _startPoint.Y) - y;

            Canvas.SetLeft(SelectionRectangle, x);
            Canvas.SetTop(SelectionRectangle, y);
            SelectionRectangle.Width = w;
            SelectionRectangle.Height = h;
            _cutoutGeometry.Rect = new Rect(x, y, w, h);
        }

        private void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isLocked || !_isDragging || SelectionRectangle == null || OverlayCanvas == null) return;
            _isDragging = false;

            var magnifier = this.FindControl<Border>("MagnifierBorder");
            if (magnifier != null) magnifier.IsVisible = false;

            double wpfX = Canvas.GetLeft(SelectionRectangle);
            double wpfY = Canvas.GetTop(SelectionRectangle);
            double wpfW = SelectionRectangle.Width;
            double wpfH = SelectionRectangle.Height;

            if (wpfW <= 10 || wpfH <= 10)
            {
                _onCancel?.Invoke();
                this.Close();
                return;
            }

            _isLocked = true;
            this.Cursor = new Cursor(StandardCursorType.Arrow);

            PlatformHelper.MakeWindowClickThrough(this);

            // Закріплюємо вікно зони поверх звичайних вікон
            this.Topmost = false;
            this.Topmost = true;

            PixelPoint topLeft = this.PointToScreen(new Point(wpfX, wpfY));
            PixelPoint bottomRight = this.PointToScreen(new Point(wpfX + wpfW, wpfY + wpfH));

            // Спочатку закріпили рамку, а потім викликаємо callback — панель керування перехопить статус Topmost і стане ПОВЕРХ цієї рамки!
            _onRegionSelected?.Invoke(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        }
    }
}
