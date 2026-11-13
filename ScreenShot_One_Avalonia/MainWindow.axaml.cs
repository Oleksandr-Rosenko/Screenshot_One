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
        private double _currentScaling = 1.0;

        // true — режим «Розпізнати текст»: виділена зона йде в OCR, а не в редактор
        private readonly bool _ocrMode;

        // Знімок окремого вікна (2.10.0): межі вікон на момент «заморожування» екрана і вікно під курсором
        private System.Collections.Generic.List<PixelRect> _windows = new();
        private Rect? _hoverWindow;

        public MainWindow() : this(ocrMode: false) { }

        public MainWindow(bool ocrMode)
        {
            _ocrMode = ocrMode;
            FreezeScreen();

            InitializeComponent();

            this.WindowDecorations = WindowDecorations.None;

            // Windows — FullScreen; Mac — вікно без рамки над рядком меню, без окремого робочого столу й анімації
            PlatformHelper.SetupScreenOverlay(this, _currentScreenBounds, _currentScaling);

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
            // Підказка «клікніть по вікну…» — по центру вгорі
            this.Opened += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                Canvas.SetLeft(WindowHint, Math.Max(0, (Bounds.Width - WindowHint.Bounds.Width) / 2)),
                Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private void FreezeScreen()
        {
            try
            {
                var mousePos = ScreenService.GetCursorPosition();
                var currentScreen = this.Screens.ScreenFromPoint(mousePos) ?? this.Screens.Primary ?? this.Screens.All.FirstOrDefault();
                _currentScreenBounds = currentScreen?.Bounds ?? new PixelRect(0, 0, 1920, 1080);
                _currentScaling = currentScreen?.Scaling ?? 1.0;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                _avaloniaBackground = ScreenCaptureService.Instance.CaptureRect(_currentScreenBounds, _currentScaling);
                long captureMs = sw.ElapsedMilliseconds;
                _windows = WindowLocator.GetWindows();
                AppLog.Write($"[Capture] екран {_currentScreenBounds.Width}×{_currentScreenBounds.Height} (×{_currentScaling:0.##}): знімок {captureMs} мс, " +
                             $"вікон {_windows.Count} за {sw.ElapsedMilliseconds - captureMs} мс, " +
                             $"картинка {(_avaloniaBackground == null ? "немає" : $"{_avaloniaBackground.PixelSize.Width}×{_avaloniaBackground.PixelSize.Height}")}");
            }
            catch (Exception ex)
            {
                AppLog.Write($"Помилка заморозки: {ex.Message}");
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
            WindowHint.IsVisible = false;

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

            if (!_isDragging)
            {
                HighlightWindowAt(currentPoint);
                return;
            }

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

        /// <summary>Підсвічує вікно під курсором (до початку виділення).</summary>
        private void HighlightWindowAt(Point local)
        {
            _hoverWindow = null;
            var hit = WindowLocator.HitTest(_windows, this.PointToScreen(local));
            if (hit is PixelRect r)
            {
                // Межі вікна → координати оверлея, обрізані по екрану
                var tl = this.PointToClient(new PixelPoint(r.X, r.Y));
                var br = this.PointToClient(new PixelPoint(r.Right, r.Bottom));
                double x = Math.Max(0, tl.X), y = Math.Max(0, tl.Y);
                double w = Math.Min(Bounds.Width, br.X) - x, h = Math.Min(Bounds.Height, br.Y) - y;
                if (w > 5 && h > 5) _hoverWindow = new Rect(x, y, w, h);
            }

            if (_hoverWindow is Rect hr)
            {
                SelectionRectangle.IsVisible = true;
                Canvas.SetLeft(SelectionRectangle, hr.X);
                Canvas.SetTop(SelectionRectangle, hr.Y);
                SelectionRectangle.Width = hr.Width;
                SelectionRectangle.Height = hr.Height;
                UpdateCutout(hr.X, hr.Y, hr.Width, hr.Height);
            }
            else
            {
                SelectionRectangle.IsVisible = false;
                OverlayPath.Data = null;
            }
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
                // Клік без перетягування — знімок вікна під курсором
                if (_hoverWindow is Rect win)
                {
                    wpfX = win.X; wpfY = win.Y; wpfWidth = win.Width; wpfHeight = win.Height;
                    AppLog.Write($"[Window] знімок вікна {wpfWidth:0}×{wpfHeight:0}");
                }
                else
                {
                    this.Close();
                    return;
                }
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
                    // Знімок може мати інший піксельний розмір, ніж межі екрана (напр. Retina з нестандартним масштабом) —
                    // переводимо координати виділення в пікселі знімка. Беремо PixelSize, а не Size (Size залежить від DPI).
                    var bgSize = _avaloniaBackground.PixelSize;
                    double kx = _currentScreenBounds.Width > 0 ? bgSize.Width / (double)_currentScreenBounds.Width : 1.0;
                    double ky = _currentScreenBounds.Height > 0 ? bgSize.Height / (double)_currentScreenBounds.Height : 1.0;

                    int cropX = (int)Math.Round((realX - _currentScreenBounds.X) * kx);
                    int cropY = (int)Math.Round((realY - _currentScreenBounds.Y) * ky);
                    realWidth = Math.Max(1, (int)Math.Round(realWidth * kx));
                    realHeight = Math.Max(1, (int)Math.Round(realHeight * ky));

                    cropX = Math.Max(0, cropX);
                    cropY = Math.Max(0, cropY);
                    realWidth = Math.Min(realWidth, bgSize.Width - cropX);
                    realHeight = Math.Min(realHeight, bgSize.Height - cropY);

                    if (realWidth > 0 && realHeight > 0)
                    {
                        var cropped = ScreenCaptureService.Instance.CropBitmap(_avaloniaBackground, new PixelRect(cropX, cropY, realWidth, realHeight));
                        if (cropped != null)
                        {
                            if (_ocrMode) OcrFlow.RecognizeAndCopyAsync(cropped).FireAndForget("OCR зони");
                            else CaptureRouter.Deliver(cropped);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"Критична помилка: {ex.Message}");
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
