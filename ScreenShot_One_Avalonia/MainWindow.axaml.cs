using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace ScreenShot_One_Avalonia
{
    public partial class MainWindow : Window
    {
        private Avalonia.Point _startPoint;
        private bool _isDragging = false;

        private Avalonia.Media.Imaging.Bitmap? _avaloniaBackground;
        private PixelRect _currentScreenBounds;

        public MainWindow()
        {
            FreezeScreen();

            InitializeComponent();

            this.WindowDecorations = WindowDecorations.None;

            this.Position = new PixelPoint(_currentScreenBounds.X, _currentScreenBounds.Y);
            this.WindowState = WindowState.FullScreen;

            this.Topmost = true;
            this.ShowInTaskbar = false;

            if (_avaloniaBackground != null)
            {
                this.Background = new ImageBrush(_avaloniaBackground)
                {
                    Stretch = Stretch.Fill
                };

                var magImage = this.FindControl<Image>("MagnifierImage");
                if (magImage != null) magImage.Source = _avaloniaBackground;
            }

            this.KeyDown += (s, e) => { if (e.Key == Key.Escape) this.Close(); };
        }

        private void FreezeScreen()
        {
            try
            {
                var mousePos = ScreenService.GetCursorPosition();
                var currentScreen = this.Screens.ScreenFromPoint(mousePos) ?? this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                _currentScreenBounds = currentScreen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);

                _avaloniaBackground = ScreenCaptureService.Instance.CaptureRect(_currentScreenBounds);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Помилка заморозки: {ex.Message}");
            }
        }

        private void UpdateMagnifier(Avalonia.Point pos)
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

        // ВИПРАВЛЕНО: Використовуємо StartPoint та EndPoint замість X1/Y1 для Avalonia!
        private void UpdateCrosshair(Avalonia.Point pos)
        {
            if (CrosshairH != null)
            {
                CrosshairH.StartPoint = new Avalonia.Point(0, pos.Y);
                CrosshairH.EndPoint = new Avalonia.Point(this.Bounds.Width, pos.Y);
            }

            if (CrosshairV != null)
            {
                CrosshairV.StartPoint = new Avalonia.Point(pos.X, 0);
                CrosshairV.EndPoint = new Avalonia.Point(pos.X, this.Bounds.Height);
            }
        }

        private void Canvas_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(OverlayCanvas).Properties.IsLeftButtonPressed) return;

            _isDragging = true;
            _startPoint = e.GetPosition(OverlayCanvas);

            SelectionRectangle.IsVisible = true;
            Canvas.SetLeft(SelectionRectangle, _startPoint.X);
            Canvas.SetTop(SelectionRectangle, _startPoint.Y);
            SelectionRectangle.Width = 0;
            SelectionRectangle.Height = 0;

            UpdateCutout(_startPoint.X, _startPoint.Y, 0, 0);
        }

        private void Canvas_PointerMoved(object? sender, PointerEventArgs e)
        {
            var currentPoint = e.GetPosition(OverlayCanvas);

            // Рухаємо кастомний приціл та лупу
            UpdateCrosshair(currentPoint);
            UpdateMagnifier(currentPoint);

            if (!_isDragging) return;

            var x = Math.Min(currentPoint.X, _startPoint.X);
            var y = Math.Min(currentPoint.Y, _startPoint.Y);
            var width = Math.Max(currentPoint.X, _startPoint.X) - x;
            var height = Math.Max(currentPoint.Y, _startPoint.Y) - y;

            Canvas.SetLeft(SelectionRectangle, x);
            Canvas.SetTop(SelectionRectangle, y);
            SelectionRectangle.Width = width;
            SelectionRectangle.Height = height;

            UpdateCutout(x, y, width, height);
        }

        private void UpdateCutout(double x, double y, double w, double h)
        {
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(new RectangleGeometry(new Rect(-10000, -10000, 30000, 30000)));
            group.Children.Add(new RectangleGeometry(new Rect(x, y, w, h)));
            OverlayPath.Data = group;
        }

        private void Canvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            var magnifier = this.FindControl<Border>("MagnifierBorder");
            if (magnifier != null) magnifier.IsVisible = false;

            if (e.InitialPressMouseButton != MouseButton.Left) return;
            _isDragging = false;

            double wpfX = Canvas.GetLeft(SelectionRectangle);
            double wpfY = Canvas.GetTop(SelectionRectangle);
            double wpfWidth = SelectionRectangle.Width;
            double wpfHeight = SelectionRectangle.Height;

            if (wpfWidth <= 5 || wpfHeight <= 5)
            {
                this.Close();
                return;
            }

            var screenPointLeft = this.PointToScreen(new Avalonia.Point(wpfX, wpfY));
            var screenPointRight = this.PointToScreen(new Avalonia.Point(wpfX + wpfWidth, wpfY + wpfHeight));

            int realX = screenPointLeft.X;
            int realY = screenPointLeft.Y;
            int realWidth = Math.Max(1, screenPointRight.X - screenPointLeft.X);
            int realHeight = Math.Max(1, screenPointRight.Y - screenPointLeft.Y);

            this.Hide();

            try
            {
                if (_avaloniaBackground != null)
                {
                    int cropX = realX - _currentScreenBounds.X;
                    int cropY = realY - _currentScreenBounds.Y;

                    cropX = Math.Max(0, cropX);
                    cropY = Math.Max(0, cropY);
                    realWidth = Math.Min(realWidth, (int)_avaloniaBackground.Size.Width - cropX);
                    realHeight = Math.Min(realHeight, (int)_avaloniaBackground.Size.Height - cropY);

                    if (realWidth > 0 && realHeight > 0)
                    {
                        var cropped = ScreenCaptureService.Instance.CropBitmap(_avaloniaBackground, new PixelRect(cropX, cropY, realWidth, realHeight));
                        if (cropped != null)
                        {
                            new EditorWindow(cropped).Show();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Критична помилка: {ex.Message}");
            }

            this.Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            _avaloniaBackground?.Dispose();
            base.OnClosed(e);
        }
    }
}
