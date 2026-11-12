using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using MsBox.Avalonia;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using SukiUI.Controls;
using File = System.IO.File;
using Path = System.IO.Path;

namespace ScreenShot_One_Avalonia
{
    public enum DrawingTool { None, Select, Stamp, Rectangle, Arrow, Pencil, Text, Highlighter, Blur, Crop, Connector, Ellipse, Line, Pixelate, Spotlight }

    /// <summary>Затемнення всього полотна, крім «дірки» (інструмент «Прожектор»).</summary>
    internal sealed class SpotlightShape : Avalonia.Controls.Shapes.Path
    {
        public Rect Hole;
        protected override Type StyleKeyOverride => typeof(Avalonia.Controls.Shapes.Path);
    }

    public partial class EditorWindow : SukiWindow
    {
        private DrawingTool _activeTool = DrawingTool.None;
        private IBrush _currentBrush = Brushes.Red;
        private int _stampCounter = 1;
        private Point _rectStartPoint;

        private Rectangle? _currentRect;

        // ---- 2.6.0: нові інструменти ----
        private double _strokeWidth = 3; // товщина ліній (селектор «Товщина»)
        private Ellipse? _currentEllipse;
        private bool _isDrawingEllipse;
        private Polyline? _currentLine;
        private bool _isDrawingLine;
        private Point _lineStartPoint;
        private bool _isDrawingPixelate;
        private bool _isDrawingSpotlight;
        private Rect _dragStartHole;

        private bool _isDrawingRect = false;

        private Polyline? _currentArrow;

        private bool _isDrawingArrow = false;

        private Point _arrowStartPoint;

        private Polyline? _currentPencilPolyline;
        private readonly List<Point> _pencilPoints = new();

        private bool _isDrawingPencil = false;

        private bool _isDrawingBlur = false;

        private bool _isDrawingConnector = false;

        private Point _connectorStartPoint;

        private Canvas? _currentConnectorCanvas;

        private Line? _currentConnectorLine;
        private Ellipse? _currentConnectorStartDot;
        private Ellipse? _currentConnectorEndDot;

        private bool _isDrawingCropRect = false;
        private Point _cropStart;
        private Rect _finalCropRect;

        private Bitmap _originalImage;
        private bool _isSavedToHistory = false;

        private HistoryWindow? _historyOwner;
        private HistoryItem? _sourceHistoryItem;
        private bool _returnedToHistory = false;

        private bool _isDraggingShape = false;
        private Control? _draggedElement = null;
        private Point _dragStartMousePos;
        private Point _dragStartElementPos;

        private double _minScale = 0.1;
        private double _currentScale = 1.0;
        private string? _editingId = null;
        private IPointer? _activePointer;

        private bool _isSplitMode = false;

        public EditorWindow(Bitmap capturedImage, string? jsonPath = null, HistoryWindow? historyOwner = null, HistoryItem? sourceItem = null)
        {
            InitializeComponent();
            BuildInlineToolButtons();
            // Кнопки обрізки живуть поза полотном — переставляємо їх при прокрутці й зміні розміру вікна
            MainScrollViewer.ScrollChanged += (_, _) => PositionCropControls();
            CropControlsLayer.SizeChanged += (_, _) => PositionCropControls();
            _historyOwner = historyOwner;
            _sourceHistoryItem = sourceItem;

            if (_historyOwner != null)
            {
                BtnBackToHistory.IsVisible = true;
                SepBackToHistory.IsVisible = true;
            }

            // Редактор завжди працює з 96 DPI (як і раніше): PNG з історії читається з 96 DPI,
            // а знімок екрана тепер має DPI масштабу екрана — без нормалізації вони показувались би різним розміром.
            var normalized = BitmapPixels.WithDpi(capturedImage, BitmapPixels.Dpi96);
            if (!ReferenceEquals(normalized, capturedImage)) capturedImage.Dispose(); // редактор володіє переданим знімком
            capturedImage = normalized;
            _originalImage = capturedImage;
            UpdateCanvasImage(capturedImage);

            ColorSwatch.Background = _currentBrush;

            this.KeyDown += Window_KeyDown;
            this.KeyUp += Window_KeyUp;
            this.Opened += Window_Opened;
            this.Closing += Window_Closing;

            DragDrop.SetAllowDrop(SplitPlaceholder, true);
            SplitPlaceholder.AddHandler(DragDrop.DropEvent, SplitPlaceholder_Drop);

            if (!string.IsNullOrEmpty(jsonPath))
            {
                _editingId = Path.GetFileNameWithoutExtension(jsonPath).Replace("_meta", "");
                LoadShapesFromJson(jsonPath);
            }
            else if (_sourceHistoryItem != null)
            {
                string fn = Path.GetFileNameWithoutExtension(_sourceHistoryItem.Path);
                if (fn.EndsWith("_flattened"))
                {
                    _editingId = fn.Replace("_flattened", "");
                }
            }
        }

        private void UpdateCanvasImage(Bitmap bmp)
        {
            ScreenshotImage.Source = bmp;
            DrawingCanvas.Width = bmp.Size.Width;
            DrawingCanvas.Height = bmp.Size.Height;
            ScreenshotImage.Width = bmp.Size.Width;
            ScreenshotImage.Height = bmp.Size.Height;
        }

        private SolidColorBrush GetFreshBrush()
        {
            return new SolidColorBrush(((ISolidColorBrush)_currentBrush).Color);
        }

        private async void Window_Opened(object? sender, EventArgs e)
        {
            FitToScreen();

            if (SettingsManager.Current.CopyToClipboardOnOpen)
            {
                await CopyImageToClipboardDirectly(_originalImage);
            }
        }

        private void FitToScreen()
        {
            double w = MainScrollViewer.Bounds.Width;
            double h = MainScrollViewer.Bounds.Height;
            if (w > 0 && h > 0)
            {
                _minScale = Math.Min(w / DrawingCanvas.Width, h / DrawingCanvas.Height) * 0.98;
                if (_minScale > 1.0) _minScale = 1.0;
                _currentScale = _minScale;
                UpdateZoom();
            }
        }

        private void MainScrollViewer_PointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.Control || _activeTool == DrawingTool.Crop) return;
            e.Handled = true;
            double newScale = e.Delta.Y > 0 ? _currentScale * 1.15 : _currentScale / 1.15;
            if (newScale < _minScale) newScale = _minScale;
            if (newScale > 5.0) newScale = 5.0;
            if (Math.Abs(newScale - _currentScale) < 0.001) return;

            Point mousePos = e.GetPosition(DrawingCanvas);
            _currentScale = newScale;
            UpdateZoom();

            MainScrollViewer.UpdateLayout();
            MainScrollViewer.Offset = new Vector(
                mousePos.X * _currentScale - e.GetPosition(MainScrollViewer).X,
                mousePos.Y * _currentScale - e.GetPosition(MainScrollViewer).Y
            );
        }

        private void UpdateZoom()
        {
            if (CanvasScaleControl.LayoutTransform is ScaleTransform scaleTransform)
            {
                scaleTransform.ScaleX = _currentScale;
                scaleTransform.ScaleY = _currentScale;
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(PositionCropControls, Avalonia.Threading.DispatcherPriority.Loaded);
        }

        private void BtnSplit_Click(object? sender, RoutedEventArgs e)
        {
            if (_isSplitMode) return;
            _isSplitMode = true;
            SetTool(DrawingTool.None, BtnSplit);
            BtnImageGroup.Flyout?.Hide();
            BtnImageGroup.Classes.Set("active", true); // режим колажу — підсвічуємо групу «Додати зображення»

            double gap = 20;
            double rightX = ScreenshotImage.Width + gap;

            Canvas.SetLeft(SplitPlaceholder, rightX);
            Canvas.SetTop(SplitPlaceholder, 0);

            SplitPlaceholder.Width = ScreenshotImage.Width;
            SplitPlaceholder.Height = ScreenshotImage.Height;
            SplitPlaceholder.IsVisible = true;

            DrawingCanvas.Width = rightX + SplitPlaceholder.Width;
            DrawingCanvas.Height = ScreenshotImage.Height;

            FitToScreen();
        }

        private async void SplitPlaceholder_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Виберіть зображення для порівняння",
                AllowMultiple = false,
                FileTypeFilter = new[] { FilePickerFileTypes.ImageAll }
            });

            if (files.Count > 0)
            {
                LoadSecondImage(files[0].Path.LocalPath);
            }
        }

        private void SplitPlaceholder_Drop(object? sender, Avalonia.Input.DragEventArgs e)
        {
            try
            {
                var files = e.DataTransfer.TryGetFiles();
                if (files != null && files.Any())
                {
                    var file = files.First();
                    if (file != null)
                    {
                        LoadSecondImage(file.Path.LocalPath);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBoxManager.GetMessageBoxStandard("Помилка", $"Не вдалося обробити файл: {ex.Message}").ShowWindowAsync();
            }
        }

        private async System.Threading.Tasks.Task PasteSecondImageFromClipboardAsync()
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard == null) return;

                var bmp = await topLevel.Clipboard.TryGetBitmapAsync();
                if (bmp != null)
                {
                    SetSecondImage(bmp);
                    return;
                }

                var file = await topLevel.Clipboard.TryGetFileAsync();
                if (file != null)
                {
                    LoadSecondImage(file.Path.LocalPath);
                }
            }

            catch (Exception ex)

            {

                AppLog.Write($"Помилка вставки з буфера: {ex.Message}");

            }

        }

        private double _overlayPasteOffset = 0;

        private void BtnPasteImage_Click(object? sender, RoutedEventArgs e) => PasteOverlayImageFromClipboard();

        private void PasteOverlayImageFromClipboard()
        {
            _ = Task.Run(async () =>
            {
                var bmp = await GetImageFromClipboardAsync();
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (bmp != null)
                    {
                        AddImageOverlay(bmp);
                        ToastText.Text = "Зображення вставлено!";
                        ToastNotification.IsVisible = true;
                        _ = Task.Delay(1500).ContinueWith(_ =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => ToastNotification.IsVisible = false);
                        });
                    }
                    else
                    {
                        ToastText.Text = "У буфері обміну немає зображення";
                        ToastNotification.IsVisible = true;
                        _ = Task.Delay(1500).ContinueWith(_ =>
                        {
                            Avalonia.Threading.Dispatcher.UIThread.Post(() => ToastNotification.IsVisible = false);
                        });
                    }
                });
            });
        }

        private async Task<Bitmap?> GetImageFromClipboardAsync()
        {
            try
            {
                var topLevel = TopLevel.GetTopLevel(this);
                if (topLevel?.Clipboard != null)
                {
                    var bmp = await topLevel.Clipboard.TryGetBitmapAsync();
                    if (bmp != null) return bmp;

                    var file = await topLevel.Clipboard.TryGetFileAsync();
                    if (file != null && File.Exists(file.Path.LocalPath))
                    {
                        string ext = Path.GetExtension(file.Path.LocalPath).ToLowerInvariant();
                        if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
                        {
                            return new Bitmap(file.Path.LocalPath);
                        }
                    }
                }
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }

#if WINDOWS
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    Bitmap? winFormsBmp = null;
                    var t = new System.Threading.Thread(() =>
                    {
                        try
                        {
                            if (System.Windows.Forms.Clipboard.ContainsImage())
                            {
                                using var img = System.Windows.Forms.Clipboard.GetImage();
                                if (img != null)
                                {
                                    using var ms = new MemoryStream();
                                    img.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                                    ms.Position = 0;
                                    winFormsBmp = new Bitmap(ms);
                                }
                            }
                            else if (System.Windows.Forms.Clipboard.ContainsFileDropList())
                            {
                                var files = System.Windows.Forms.Clipboard.GetFileDropList();
                                if (files.Count > 0 && File.Exists(files[0]))
                                {
                                    string ext = Path.GetExtension(files[0]).ToLowerInvariant();
                                    if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".webp")
                                    {
                                        winFormsBmp = new Bitmap(files[0]);
                                    }
                                }
                            }
                        }
                        catch (Exception logEx) { AppLog.Warn(logEx); }
                    });
                    t.SetApartmentState(System.Threading.ApartmentState.STA);
                    t.Start();
                    t.Join(1000);
                    if (winFormsBmp != null) return winFormsBmp;
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            }
#endif

            return null;
        }

        private void AddImageOverlay(Bitmap bmp, double? x = null, double? y = null, double? width = null, double? height = null, bool addToUndo = true)
        {
            double origW = bmp.Size.Width;
            double origH = bmp.Size.Height;
            double baseW = ScreenshotImage.Width > 0 ? ScreenshotImage.Width : _originalImage.Size.Width;
            double baseH = ScreenshotImage.Height > 0 ? ScreenshotImage.Height : _originalImage.Size.Height;

            double targetW = width ?? origW;
            double targetH = height ?? origH;

            if (!width.HasValue && !height.HasValue)
            {
                if (targetW > baseW * 0.6 || targetH > baseH * 0.6)
                {
                    double scale = Math.Min((baseW * 0.5) / targetW, (baseH * 0.5) / targetH);
                    targetW = Math.Max(30, Math.Round(targetW * scale));
                    targetH = Math.Max(30, Math.Round(targetH * scale));
                }
            }

            double posX;
            double posY;

            if (x.HasValue && y.HasValue)
            {
                posX = x.Value;
                posY = y.Value;
            }
            else
            {
                double scrollX = MainScrollViewer?.Offset.X ?? 0;
                double scrollY = MainScrollViewer?.Offset.Y ?? 0;
                double viewW = MainScrollViewer?.Viewport.Width > 0 ? MainScrollViewer.Viewport.Width : baseW;
                double viewH = MainScrollViewer?.Viewport.Height > 0 ? MainScrollViewer.Viewport.Height : baseH;

                posX = Math.Max(10, scrollX + (viewW - targetW) / 2 + _overlayPasteOffset);
                posY = Math.Max(10, scrollY + (viewH - targetH) / 2 + _overlayPasteOffset);
                _overlayPasteOffset = (_overlayPasteOffset + 25) % 150;
            }

            var container = new Grid
            {
                Width = targetW,
                Height = targetH,
                Tag = ShapeType.ImageOverlay,
                ClipToBounds = false
            };

            var image = new Image
            {
                Source = bmp,
                Stretch = Stretch.Fill,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
            };
            container.Children.Add(image);

            var selectionBorder = new Border
            {
                Name = "OverlaySelectionBorder",
                BorderBrush = new SolidColorBrush(Color.FromArgb(160, 30, 144, 255)),
                BorderThickness = new Thickness(1.5),
                IsHitTestVisible = false,
                IsVisible = true
            };
            container.Children.Add(selectionBorder);

            var resizeHandle = new Border
            {
                Name = "OverlayResizeHandle",
                Width = 14,
                Height = 14,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom,
                Background = AccentBrush,
                CornerRadius = new CornerRadius(3),
                Cursor = new Cursor(StandardCursorType.BottomRightCorner),
                Margin = new Thickness(0, 0, -4, -4),
                IsVisible = true
            };

            bool isResizing = false;
            Point resizeStartMouse = default;
            double resizeStartW = targetW;
            double resizeStartH = targetH;

            resizeHandle.PointerPressed += (s, e) =>
            {
                e.Pointer.Capture(resizeHandle);
                isResizing = true;
                resizeStartMouse = e.GetPosition(DrawingCanvas);
                resizeStartW = container.Width;
                resizeStartH = container.Height;
                e.Handled = true;
            };

            resizeHandle.PointerMoved += (s, e) =>
            {
                if (isResizing)
                {
                    var currentPos = e.GetPosition(DrawingCanvas);
                    double deltaX = currentPos.X - resizeStartMouse.X;
                    double deltaY = currentPos.Y - resizeStartMouse.Y;

                    double newW = Math.Max(30, resizeStartW + deltaX);
                    double newH = Math.Max(30, resizeStartH + deltaY);

                    container.Width = newW;
                    container.Height = newH;
                    e.Handled = true;
                }
            };

            resizeHandle.PointerReleased += (s, e) =>
            {
                if (isResizing)
                {
                    isResizing = false;
                    e.Pointer.Capture(null);
                    e.Handled = true;
                }
            };

            container.Children.Add(resizeHandle);

            Canvas.SetLeft(container, posX);
            Canvas.SetTop(container, posY);

            DrawingCanvas.Children.Add(container);

            if (addToUndo)
            {
                PushAction(new AddShapeAction(container));
            }

            SetTool(DrawingTool.Select, BtnSelect);
        }

        private void LoadSecondImage(string filePath)

        {

            try

            {

                var bmp = new Bitmap(filePath);

                SetSecondImage(bmp);

            }
            catch (Exception ex)
            {
                MessageBoxManager.GetMessageBoxStandard("Помилка", $"Не вдалося завантажити зображення: {ex.Message}").ShowWindowAsync();
            }
        }

        private void SetSecondImage(Bitmap bmp)
        {
            SecondScreenshotImage.Source = bmp;
            SecondScreenshotImage.Width = bmp.Size.Width;
            SecondScreenshotImage.Height = bmp.Size.Height;

            SplitPlaceholder.IsVisible = false;
            SecondScreenshotImage.IsVisible = true;

            double gap = 20;
            double rightX = ScreenshotImage.Width + gap;
            Canvas.SetLeft(SecondScreenshotImage, rightX);
            Canvas.SetTop(SecondScreenshotImage, 0);

            DrawingCanvas.Width = rightX + bmp.Size.Width;
            DrawingCanvas.Height = Math.Max(ScreenshotImage.Height, bmp.Size.Height);

            FitToScreen();
            BtnImageGroup.Classes.Set("active", false);
        }

        private void LoadShapesFromJson(string jsonPath)
        {
            if (!File.Exists(jsonPath)) return;
            try
            {
                var shapes = JsonSerializer.Deserialize<List<ShapeData>>(File.ReadAllText(jsonPath));
                if (shapes == null) return;

                foreach (var shape in shapes)

                {

                    var brush = new SolidColorBrush(Color.Parse(shape.ColorHex ?? "#000000"));

                    if (shape.Type == ShapeType.Stamp)

                    {

                        var group = CreateStampGroup(brush, shape.StampNumber);

                        double halfW = group.Width > 0 ? group.Width / 2.0 : 14.0;
                        double halfH = group.Height > 0 ? group.Height / 2.0 : 14.0;
                        Canvas.SetLeft(group, shape.X1 - halfW); Canvas.SetTop(group, shape.Y1 - halfH);

                        DrawingCanvas.Children.Add(group);

                        _stampCounter = Math.Max(_stampCounter, shape.StampNumber + 1);

                    }

                    else if (shape.Type == ShapeType.Rectangle)

                    {

                        var rect = new Rectangle { Stroke = brush, StrokeThickness = shape.StrokeWidth > 0 ? shape.StrokeWidth : 3, Fill = Brushes.Transparent, Width = shape.X2, Height = shape.Y2 };
                        Canvas.SetLeft(rect, shape.X1); Canvas.SetTop(rect, shape.Y1); DrawingCanvas.Children.Add(rect);
                    }
                    else if (shape.Type == ShapeType.Ellipse)
                    {
                        var ell = new Ellipse { Stroke = brush, StrokeThickness = shape.StrokeWidth > 0 ? shape.StrokeWidth : 3, Fill = Brushes.Transparent, Width = shape.X2, Height = shape.Y2, Tag = ShapeType.Ellipse };
                        Canvas.SetLeft(ell, shape.X1); Canvas.SetTop(ell, shape.Y1); DrawingCanvas.Children.Add(ell);
                    }
                    else if (shape.Type == ShapeType.Pixelate) { ApplyPixelateToRegion(shape.X1, shape.Y1, shape.X2, shape.Y2); }
                    else if (shape.Type == ShapeType.Spotlight) { AddSpotlight(new Rect(shape.X1, shape.Y1, shape.X2, shape.Y2)); }
                    else if (shape.Type == ShapeType.Arrow || shape.Type == ShapeType.StatusArrow || shape.Type == ShapeType.Pencil || shape.Type == ShapeType.Highlighter || shape.Type == ShapeType.Line)
                    {
                        double thick = shape.StrokeWidth > 0 ? shape.StrokeWidth : (shape.Type == ShapeType.Highlighter ? 16 : 3);
                        var poly = new Polyline { Stroke = brush, StrokeThickness = thick, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round, Opacity = shape.Type == ShapeType.Highlighter ? 0.4 : 1.0 };

                        var pts = new Avalonia.Collections.AvaloniaList<Point>();

                        foreach (var mp in shape.PathPoints) pts.Add(new Point(mp.X, mp.Y));

                        poly.Points = pts;

                        DrawingCanvas.Children.Add(poly); poly.Tag = shape.Type;

                    }

                    else if (shape.Type == ShapeType.Text) { CreateTextBlock(shape.X1, shape.Y1, shape.TextContent ?? "", shape.FontSize, brush); }

                    else if (shape.Type == ShapeType.Blur) { ApplyBlurToRegion(shape.X1, shape.Y1, shape.X2, shape.Y2); }
                    else if (shape.Type == ShapeType.ImageOverlay && !string.IsNullOrEmpty(shape.ImageBase64))
                    {
                        try
                        {
                            byte[] bytes = Convert.FromBase64String(shape.ImageBase64);
                            using (var ms = new MemoryStream(bytes))
                            {
                                var bmp = new Bitmap(ms);
                                double w = shape.Width > 0 ? shape.Width : (shape.X2 > 0 ? shape.X2 : bmp.Size.Width);
                                double h = shape.Height > 0 ? shape.Height : (shape.Y2 > 0 ? shape.Y2 : bmp.Size.Height);
                                AddImageOverlay(bmp, shape.X1, shape.Y1, w, h, addToUndo: false);
                            }
                        }
                        catch (Exception logEx) { AppLog.Warn(logEx); }
                    }

                }

            }

            catch (Exception logEx) { AppLog.Warn(logEx); }

        }

        private Panel CreateStampGroup(IBrush brush, int number)
        {
            double width = number >= 10 ? 34 : 28;
            double height = 28;
            double radius = 14;

            Color baseColor = Colors.DeepSkyBlue;
            if (brush is ISolidColorBrush scb)
            {
                baseColor = scb.Color;
            }
            // Relative luminance (ITU-R BT.709) for auto-contrast
            double lum = (0.2126 * baseColor.R + 0.7152 * baseColor.G + 0.0722 * baseColor.B) / 255.0;
            IBrush textBrush = lum > 0.62 ? new SolidColorBrush(Color.Parse("#0F172A")) : Brushes.White;
            IBrush rimBrush = lum > 0.62
                ? new SolidColorBrush(Color.FromArgb(120, 15, 23, 42))
                : new SolidColorBrush(Color.FromArgb(210, 255, 255, 255));

            var panel = new Panel { Width = width, Height = height, Tag = ShapeType.Stamp };

            var border = new Border
            {
                Width = width,
                Height = height,
                CornerRadius = new CornerRadius(radius),
                Background = brush,
                BorderBrush = rimBrush,
                BorderThickness = new Thickness(1.5),
                BoxShadow = new BoxShadows(new BoxShadow
                {
                    OffsetX = 0,
                    OffsetY = 2,
                    Blur = 6,
                    Spread = 0,
                    Color = Color.FromArgb(70, 0, 0, 0)
                })
            };

            var textBlock = new TextBlock
            {
                Text = number.ToString(),
                Foreground = textBrush,
                FontWeight = FontWeight.Bold,
                FontSize = number >= 10 ? 12 : 13.5,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0.5)
            };

            panel.Children.Add(border);
            panel.Children.Add(textBlock);
            return panel;

        }

        private interface IEditorAction

        {

            void Undo(Canvas canvas);

            void Redo(Canvas canvas);

        }

        private class AddShapeAction : IEditorAction
        {
            private readonly Control _shape;
            public AddShapeAction(Control shape) => _shape = shape;
            public void Undo(Canvas canvas) => canvas.Children.Remove(_shape);
            public void Redo(Canvas canvas)
            {
                if (!canvas.Children.Contains(_shape))
                    canvas.Children.Add(_shape);
            }
        }

        private class RemoveShapeAction : IEditorAction
        {
            private readonly Control _shape;
            public RemoveShapeAction(Control shape) => _shape = shape;
            public void Undo(Canvas canvas)
            {
                if (!canvas.Children.Contains(_shape))
                    canvas.Children.Add(_shape);
            }
            public void Redo(Canvas canvas) => canvas.Children.Remove(_shape);
        }

        private readonly Stack<IEditorAction> _undoActions = new();
        private readonly Stack<IEditorAction> _redoActions = new();

        private void PushAction(IEditorAction action)
        {
            _undoActions.Push(action);
            _redoActions.Clear();
        }

        private void Undo()
        {
            if (_undoActions.Count > 0)
            {
                var action = _undoActions.Pop();
                action.Undo(DrawingCanvas);
                _redoActions.Push(action);
            }
            else
            {
                var lastShape = DrawingCanvas.Children.OfType<Control>().LastOrDefault(IsUserShape);
                if (lastShape != null)
                {
                    DrawingCanvas.Children.Remove(lastShape);
                    _redoActions.Push(new AddShapeAction(lastShape));
                }
            }
        }

        private void Redo()
        {
            if (_redoActions.Count > 0)
            {
                var action = _redoActions.Pop();
                action.Redo(DrawingCanvas);
                _undoActions.Push(action);
            }
        }

        private bool IsUserShape(Control c)
        {
            return c != ScreenshotImage &&
                   c != SecondScreenshotImage &&
                   c != SplitPlaceholder &&
                   c != CropSelectionRect &&
                   c != CropControlsPanel;
        }

        private void BtnUndo_Click(object? sender, RoutedEventArgs e) => Undo();
        private void BtnRedo_Click(object? sender, RoutedEventArgs e) => Redo();

        // Panning support
        private bool _isPanning = false;
        private Point _panStartPoint;
        private Vector _panStartOffset;
        private bool _isSpaceDown = false;

        private void MainScrollViewer_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            var p = e.GetCurrentPoint(this).Properties;
            if (p.IsMiddleButtonPressed || (_isSpaceDown && p.IsLeftButtonPressed))
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(this);
                _panStartOffset = MainScrollViewer.Offset;
                e.Pointer.Capture(MainScrollViewer);
                Cursor = new Cursor(StandardCursorType.SizeAll);
                e.Handled = true;
            }
        }

        private void MainScrollViewer_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (_isPanning)
            {
                Point currentPos = e.GetPosition(this);
                Vector delta = currentPos - _panStartPoint;
                MainScrollViewer.Offset = new Vector(_panStartOffset.X - delta.X, _panStartOffset.Y - delta.Y);
                e.Handled = true;
            }
        }

        private void MainScrollViewer_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                e.Pointer.Capture(null);
                Cursor = _isSpaceDown ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                e.Handled = true;
            }
        }

        private void ApplyBlurToRegion(double x, double y, double width, double height)
        {
            int ix = (int)Math.Max(0, x); int iy = (int)Math.Max(0, y);
            int iw = (int)Math.Min(DrawingCanvas.Width - ix, width); int ih = (int)Math.Min(DrawingCanvas.Height - iy, height);
            if (iw <= 0 || ih <= 0) return;

            // Раніше CroppedBitmap тримав живим повнорозмірний рендер усього полотна — на КОЖНУ область розмиття.
            // Тепер копіюємо лише потрібний фрагмент, а повний рендер одразу звільняємо.
            Bitmap? cropped;
            using (var rtb = CreateRenderedBitmap())
            {
                cropped = BitmapPixels.Crop(rtb, new PixelRect(ix, iy, iw, ih));
            }
            if (cropped == null) return;

            var blurImg = new Image { Source = cropped, Width = iw, Height = ih, Stretch = Stretch.Fill, Effect = new BlurEffect { Radius = 18 } };
            var container = new Border { Width = iw, Height = ih, Child = blurImg, ClipToBounds = true, Tag = ShapeType.Blur };

            Canvas.SetLeft(container, ix); Canvas.SetTop(container, iy);
            DrawingCanvas.Children.Add(container);
            PushAction(new AddShapeAction(container));
        }

        private void CreateTextBlock(double x, double y, string content, double size, IBrush brush)
        {
            var tb = new TextBlock { Text = content, FontSize = size, Foreground = brush, FontWeight = FontWeight.Bold, Background = Brushes.Transparent };
            Canvas.SetLeft(tb, x); Canvas.SetTop(tb, y); DrawingCanvas.Children.Add(tb);
            PushAction(new AddShapeAction(tb));
        }

        private void DrawingCanvas_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            ColorPalettePopup.IsOpen = false;

            var pProps = e.GetCurrentPoint(this).Properties;
            if (pProps.IsMiddleButtonPressed || (_isSpaceDown && pProps.IsLeftButtonPressed))
            {
                _isPanning = true;
                _panStartPoint = e.GetPosition(this);
                _panStartOffset = MainScrollViewer.Offset;
                e.Pointer.Capture(MainScrollViewer);
                Cursor = new Cursor(StandardCursorType.SizeAll);
                e.Handled = true;
                return;
            }

            Point pos = e.GetPosition(DrawingCanvas);
            var pointInfo = e.GetCurrentPoint(DrawingCanvas);
            if (!pointInfo.Properties.IsLeftButtonPressed) return;

            if (SplitPlaceholder.IsVisible && SplitPlaceholder.Bounds.Contains(pos)) return;

            if (_activeTool == DrawingTool.Crop)
            {
                if (CropControlsPanel.IsVisible && CropSelectionRect.Bounds.Contains(pos)) return;

                _isDrawingCropRect = true;
                _cropStart = pos;
                CropSelectionRect.IsVisible = true;
                Canvas.SetLeft(CropSelectionRect, pos.X); Canvas.SetTop(CropSelectionRect, pos.Y);
                CropSelectionRect.Width = 1; CropSelectionRect.Height = 1;
                CropControlsPanel.IsVisible = false;

                _activePointer = e.Pointer; _activePointer.Capture(DrawingCanvas);
                e.Handled = true; return;
            }

            if (_activeTool == DrawingTool.Select)
            {
                var hitControl = e.Source as Control;
                if (hitControl == null || hitControl == DrawingCanvas || hitControl == ScreenshotImage || hitControl == SecondScreenshotImage) return;

                var targetElement = hitControl.Parent is Canvas c && c != DrawingCanvas ? c : (hitControl.Parent is Panel p && p != DrawingCanvas ? p : hitControl);

                if (e.ClickCount == 2)
                {
                    if (targetElement is TextBlock tb) { StartEditingText(tb); return; }
                    DrawingCanvas.Children.Remove(targetElement);
                    PushAction(new RemoveShapeAction(targetElement));
                    e.Handled = true; return;
                }

                _isDraggingShape = true;
                _draggedElement = targetElement;
                _dragStartMousePos = pos;
                if (targetElement is SpotlightShape spotStart) _dragStartHole = spotStart.Hole;

                double left = double.IsNaN(Canvas.GetLeft(_draggedElement)) ? 0 : Canvas.GetLeft(_draggedElement);
                double top = double.IsNaN(Canvas.GetTop(_draggedElement)) ? 0 : Canvas.GetTop(_draggedElement);
                _dragStartElementPos = new Point(left, top);

                _activePointer = e.Pointer;
                _activePointer.Capture(DrawingCanvas);
                e.Handled = true;
                return;
            }

            try
            {
                if (_activeTool == DrawingTool.Text)
                {
                    if (ComboFontSize.SelectedItem is ComboBoxItem item && double.TryParse(item.Content?.ToString(), out double size))
                    {
                        var input = new TextBox
                        {
                            FontSize = size,
                            Foreground = GetFreshBrush(),
                            FontWeight = FontWeight.Bold,
                            MinWidth = 100,
                            MinHeight = 30,
                            Background = new SolidColorBrush(Color.FromArgb(220, 255, 255, 255)),
                            BorderBrush = AccentBrush,
                            BorderThickness = new Thickness(2),
                            Padding = new Thickness(5),
                            AcceptsReturn = true,
                            TextWrapping = TextWrapping.Wrap
                        };
                        Canvas.SetLeft(input, pos.X); Canvas.SetTop(input, pos.Y);
                        DrawingCanvas.Children.Add(input);
                        Avalonia.Threading.Dispatcher.UIThread.Post(() => input.Focus());
                        input.LostFocus += (s, args) => FinishEditingText(input);

                    }

                }

                else if (_activeTool == DrawingTool.Stamp)

                {

                    var group = CreateStampGroup(GetFreshBrush(), _stampCounter);

                    double halfW = group.Width > 0 ? group.Width / 2.0 : 14.0;
                    double halfH = group.Height > 0 ? group.Height / 2.0 : 14.0;
                    Canvas.SetLeft(group, pos.X - halfW); Canvas.SetTop(group, pos.Y - halfH);

                    DrawingCanvas.Children.Add(group); _stampCounter++;

                    PushAction(new AddShapeAction(group));

                }

                else if (_activeTool == DrawingTool.Rectangle)

                {

                    _isDrawingRect = true; _rectStartPoint = pos;
                    _currentRect = new Rectangle { Stroke = GetFreshBrush(), StrokeThickness = _strokeWidth, Fill = Brushes.Transparent, Width = 1, Height = 1 };
                    Canvas.SetLeft(_currentRect, pos.X); Canvas.SetTop(_currentRect, pos.Y);
                    DrawingCanvas.Children.Add(_currentRect);
                    _activePointer = e.Pointer; _activePointer.Capture(DrawingCanvas);
                }
                else if (_activeTool == DrawingTool.Arrow)
                {
                    _isDrawingArrow = true; _arrowStartPoint = pos;
                    _currentArrow = new Polyline { Stroke = GetFreshBrush(), StrokeThickness = _strokeWidth, StrokeJoin = PenLineJoin.Round, StrokeLineCap = PenLineCap.Round };

                    var pts = new Avalonia.Collections.AvaloniaList<Point>();
                    pts.Add(_arrowStartPoint);
                    _currentArrow.Points = pts;

                    DrawingCanvas.Children.Add(_currentArrow);
                    _activePointer = e.Pointer; _activePointer.Capture(DrawingCanvas);
                }
                else if (_activeTool == DrawingTool.Connector)
                {
                    _isDrawingConnector = true;
                    _connectorStartPoint = pos;

                    _currentConnectorCanvas = new Canvas();

                    _currentConnectorLine = new Line
                    {
                        Stroke = GetFreshBrush(),
                        StrokeThickness = _strokeWidth,
                        StartPoint = _connectorStartPoint,
                        EndPoint = pos
                    };

                    _currentConnectorStartDot = new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Fill = GetFreshBrush(),
                        Stroke = Brushes.White,
                        StrokeThickness = 1.5
                    };
                    Canvas.SetLeft(_currentConnectorStartDot, _connectorStartPoint.X - 6);
                    Canvas.SetTop(_currentConnectorStartDot, _connectorStartPoint.Y - 6);

                    _currentConnectorEndDot = new Ellipse
                    {
                        Width = 12,
                        Height = 12,
                        Fill = GetFreshBrush(),
                        Stroke = Brushes.White,
                        StrokeThickness = 1.5
                    };
                    Canvas.SetLeft(_currentConnectorEndDot, pos.X - 6);
                    Canvas.SetTop(_currentConnectorEndDot, pos.Y - 6);

                    _currentConnectorCanvas.Children.Add(_currentConnectorLine);
                    _currentConnectorCanvas.Children.Add(_currentConnectorStartDot);

                    _currentConnectorCanvas.Children.Add(_currentConnectorEndDot);

                    DrawingCanvas.Children.Add(_currentConnectorCanvas);

                    _activePointer = e.Pointer;

                    _activePointer.Capture(DrawingCanvas);

                }

                else if (_activeTool == DrawingTool.Pencil || _activeTool == DrawingTool.Highlighter)
                {
                    _isDrawingPencil = true;
                    double th = _activeTool == DrawingTool.Highlighter ? 16 * _strokeWidth / 3.0 : _strokeWidth;
                    _currentPencilPolyline = new Polyline
                    {
                        Stroke = GetFreshBrush(),
                        StrokeThickness = th,
                        StrokeJoin = PenLineJoin.Round,
                        StrokeLineCap = PenLineCap.Round,
                        Opacity = _activeTool == DrawingTool.Highlighter ? 0.4 : 1.0,
                        Tag = _activeTool == DrawingTool.Highlighter ? ShapeType.Highlighter : ShapeType.Pencil
                    };

                    _pencilPoints.Clear();
                    _pencilPoints.Add(pos);
                    _currentPencilPolyline.Points = new Avalonia.Collections.AvaloniaList<Point>(_pencilPoints);

                    DrawingCanvas.Children.Add(_currentPencilPolyline);
                    _activePointer = e.Pointer; _activePointer.Capture(DrawingCanvas);
                }
                else if (_activeTool == DrawingTool.Ellipse)
                {
                    _isDrawingEllipse = true; _rectStartPoint = pos;
                    _currentEllipse = new Ellipse { Stroke = GetFreshBrush(), StrokeThickness = _strokeWidth, Fill = Brushes.Transparent, Width = 1, Height = 1, Tag = ShapeType.Ellipse };
                    Canvas.SetLeft(_currentEllipse, pos.X); Canvas.SetTop(_currentEllipse, pos.Y);
                    DrawingCanvas.Children.Add(_currentEllipse);
                    _activePointer = e.Pointer; _activePointer.Capture(DrawingCanvas);
                }
                else if (_activeTool == DrawingTool.Line)
                {
                    _isDrawingLine = true; _lineStartPoint = pos;
                    _currentLine = new Polyline { Stroke = GetFreshBrush(), StrokeThickness = _strokeWidth, StrokeLineCap = PenLineCap.Round, Tag = ShapeType.Line };
                    _currentLine.Points = new Avalonia.Collections.AvaloniaList<Point> { pos, pos };
                    DrawingCanvas.Children.Add(_currentLine);
                    _activePointer = e.Pointer; _activePointer.Capture(DrawingCanvas);
                }
                else if (_activeTool == DrawingTool.Pixelate || _activeTool == DrawingTool.Spotlight)
                {
                    _isDrawingPixelate = _activeTool == DrawingTool.Pixelate;
                    _isDrawingSpotlight = _activeTool == DrawingTool.Spotlight;
                    _rectStartPoint = pos;
                    _currentRect = new Rectangle { Stroke = Brushes.DarkGray, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 }, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), Width = 1, Height = 1 };
                    Canvas.SetLeft(_currentRect, pos.X); Canvas.SetTop(_currentRect, pos.Y);
                    DrawingCanvas.Children.Add(_currentRect);
                    _activePointer = e.Pointer; _activePointer.Capture(DrawingCanvas);
                }
                else if (_activeTool == DrawingTool.Blur)

                {

                    _isDrawingBlur = true; _rectStartPoint = pos;

                    _currentRect = new Rectangle { Stroke = Brushes.DarkGray, StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 }, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(50, 0, 0, 0)), Width = 1, Height = 1 };

                    Canvas.SetLeft(_currentRect, pos.X); Canvas.SetTop(_currentRect, pos.Y);

                    DrawingCanvas.Children.Add(_currentRect);

                    _activePointer = e.Pointer; _activePointer.Capture(DrawingCanvas);

                }

            }

            catch (Exception logEx) { AppLog.Warn(logEx); }

        }

        private void DrawingCanvas_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (_isPanning)
            {
                Point currentPos = e.GetPosition(this);
                Vector delta = currentPos - _panStartPoint;
                MainScrollViewer.Offset = new Vector(_panStartOffset.X - delta.X, _panStartOffset.Y - delta.Y);
                e.Handled = true;
                return;
            }

            Point pos = e.GetPosition(DrawingCanvas);

            try
            {
                if (_isDrawingCropRect)
                {
                    double x = Math.Min(pos.X, _cropStart.X); double y = Math.Min(pos.Y, _cropStart.Y);
                    double w = Math.Max(1, Math.Max(pos.X, _cropStart.X) - x); double h = Math.Max(1, Math.Max(pos.Y, _cropStart.Y) - y);
                    Canvas.SetLeft(CropSelectionRect, x); Canvas.SetTop(CropSelectionRect, y);
                    CropSelectionRect.Width = w; CropSelectionRect.Height = h;
                    UpdateCropOverlayGeometry(new Rect(x, y, w, h));
                    return;
                }

                if (_activeTool == DrawingTool.Select && _isDraggingShape && _draggedElement is SpotlightShape spot)
                {
                    // Прожектор займає все полотно — рухаємо лише «дірку»
                    var d = pos - _dragStartMousePos;
                    UpdateSpotlightGeometry(spot, new Rect(_dragStartHole.X + d.X, _dragStartHole.Y + d.Y, _dragStartHole.Width, _dragStartHole.Height));
                    return;
                }
                if (_activeTool == DrawingTool.Select && _isDraggingShape && _draggedElement != null)
                {
                    double deltaX = pos.X - _dragStartMousePos.X;
                    double deltaY = pos.Y - _dragStartMousePos.Y;
                    Canvas.SetLeft(_draggedElement, _dragStartElementPos.X + deltaX);
                    Canvas.SetTop(_draggedElement, _dragStartElementPos.Y + deltaY);
                    return;
                }

                if (_isDrawingEllipse && _currentEllipse != null)
                {
                    double ex = Math.Min(pos.X, _rectStartPoint.X); double ey = Math.Min(pos.Y, _rectStartPoint.Y);
                    _currentEllipse.Width = Math.Max(1, Math.Max(pos.X, _rectStartPoint.X) - ex);
                    _currentEllipse.Height = Math.Max(1, Math.Max(pos.Y, _rectStartPoint.Y) - ey);
                    Canvas.SetLeft(_currentEllipse, ex); Canvas.SetTop(_currentEllipse, ey);
                }
                else if (_isDrawingLine && _currentLine != null)
                {
                    // Shift — рівна лінія (кратна 45°)
                    var end = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? SnapTo45(_lineStartPoint, pos) : pos;
                    _currentLine.Points = new Avalonia.Collections.AvaloniaList<Point> { _lineStartPoint, end };
                    _currentLine.InvalidateMeasure(); _currentLine.InvalidateVisual();
                }
                else if ((_isDrawingRect || _isDrawingBlur || _isDrawingPixelate || _isDrawingSpotlight) && _currentRect != null)
                {
                    double x = Math.Min(pos.X, _rectStartPoint.X); double y = Math.Min(pos.Y, _rectStartPoint.Y);
                    _currentRect.Width = Math.Max(1, Math.Max(pos.X, _rectStartPoint.X) - x); _currentRect.Height = Math.Max(1, Math.Max(pos.Y, _rectStartPoint.Y) - y);
                    Canvas.SetLeft(_currentRect, x); Canvas.SetTop(_currentRect, y);
                    _currentRect.InvalidateMeasure(); _currentRect.InvalidateVisual();
                }
                else if (_isDrawingArrow && _currentArrow != null)
                {
                    var pts = new Avalonia.Collections.AvaloniaList<Point>();
                    pts.Add(_arrowStartPoint);
                    pts.Add(pos);
                    UpdateArrowHead(pts, _arrowStartPoint, pos);
                    _currentArrow.Points = pts;
                    _currentArrow.InvalidateMeasure(); _currentArrow.InvalidateVisual();
                }
                else if (_isDrawingConnector && _currentConnectorLine != null && _currentConnectorEndDot != null)
                {
                    _currentConnectorLine.EndPoint = pos;
                    Canvas.SetLeft(_currentConnectorEndDot, pos.X - 6);
                    Canvas.SetTop(_currentConnectorEndDot, pos.Y - 6);
                    _currentConnectorLine.InvalidateMeasure();
                    _currentConnectorLine.InvalidateVisual();
                }
                else if (_isDrawingPencil && _currentPencilPolyline != null)
                {
                    if (_pencilPoints.Count > 0)
                    {
                        var lastPt = _pencilPoints[^1];
                        double dx = pos.X - lastPt.X;
                        double dy = pos.Y - lastPt.Y;
                        if (dx * dx + dy * dy >= 4.0) // At least 2px movement
                        {
                            _pencilPoints.Add(pos);
                            _currentPencilPolyline.Points = new Avalonia.Collections.AvaloniaList<Point>(_pencilPoints);
                            _currentPencilPolyline.InvalidateMeasure();
                            _currentPencilPolyline.InvalidateVisual();
                        }
                    }
                    else
                    {
                        _pencilPoints.Add(pos);
                        _currentPencilPolyline.Points = new Avalonia.Collections.AvaloniaList<Point>(_pencilPoints);
                        _currentPencilPolyline.InvalidateMeasure();
                        _currentPencilPolyline.InvalidateVisual();
                    }
                }
            }
            catch (Exception logEx) { AppLog.Warn(logEx); }
        }

        private void DrawingCanvas_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            if (_isPanning)
            {
                _isPanning = false;
                e.Pointer.Capture(null);
                Cursor = _isSpaceDown ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
                e.Handled = true;
                return;
            }

            if (_activePointer != null)
            {
                _activePointer.Capture(null);
                _activePointer = null;
            }

            Point pos = e.GetPosition(DrawingCanvas);

            if (_isDrawingCropRect)
            {
                _isDrawingCropRect = false;
                double w = CropSelectionRect.Width; double h = CropSelectionRect.Height;
                if (w < 10 || h < 10) { ResetCropUI(); return; }

                double left = Canvas.GetLeft(CropSelectionRect); double top = Canvas.GetTop(CropSelectionRect);
                _finalCropRect = new Rect(left, top, w, h);
                ShowCropControls(left, top, w, h);
                return;
            }

            if (_activeTool == DrawingTool.Select && _isDraggingShape)
            {
                _isDraggingShape = false;
                _draggedElement = null;
                return;
            }

            if (_isDrawingRect && _currentRect != null)
            {
                _isDrawingRect = false;
                if (_currentRect.Width <= 2) DrawingCanvas.Children.Remove(_currentRect);
                else PushAction(new AddShapeAction(_currentRect));
                _currentRect = null;
            }
            else if (_isDrawingArrow && _currentArrow != null)
            {
                _isDrawingArrow = false;
                if (_currentArrow.Points.Count > 1)
                {
                    _currentArrow.Tag = ShapeType.StatusArrow;
                    PushAction(new AddShapeAction(_currentArrow));
                }
                else DrawingCanvas.Children.Remove(_currentArrow);
                _currentArrow = null;
            }
            else if (_isDrawingConnector && _currentConnectorCanvas != null)
            {
                _isDrawingConnector = false;
                double dist = Math.Sqrt(Math.Pow(pos.X - _connectorStartPoint.X, 2) + Math.Pow(pos.Y - _connectorStartPoint.Y, 2));
                if (dist <= 3)
                {
                    DrawingCanvas.Children.Remove(_currentConnectorCanvas);
                }
                else
                {
                    PushAction(new AddShapeAction(_currentConnectorCanvas));
                }
                _currentConnectorCanvas = null;
                _currentConnectorLine = null;
                _currentConnectorStartDot = null;
                _currentConnectorEndDot = null;
            }
            else if (_isDrawingPencil && _currentPencilPolyline != null)
            {
                _isDrawingPencil = false;
                if (_pencilPoints.Count == 0)
                {
                    DrawingCanvas.Children.Remove(_currentPencilPolyline);
                }
                else
                {
                    if (_pencilPoints.Count == 1)
                    {
                        _pencilPoints.Add(new Point(pos.X + 0.1, pos.Y + 0.1));
                    }
                    else if (_pencilPoints[^1] != pos)
                    {
                        _pencilPoints.Add(pos);
                    }
                    _currentPencilPolyline.Points = new Avalonia.Collections.AvaloniaList<Point>(_pencilPoints);
                    _currentPencilPolyline.InvalidateMeasure();
                    _currentPencilPolyline.InvalidateVisual();
                    PushAction(new AddShapeAction(_currentPencilPolyline));
                }
                _currentPencilPolyline = null;
            }
            else if (_isDrawingEllipse && _currentEllipse != null)
            {
                _isDrawingEllipse = false;
                if (_currentEllipse.Width <= 2 && _currentEllipse.Height <= 2) DrawingCanvas.Children.Remove(_currentEllipse);
                else PushAction(new AddShapeAction(_currentEllipse));
                _currentEllipse = null;
            }
            else if (_isDrawingLine && _currentLine != null)
            {
                _isDrawingLine = false;
                var lp = _currentLine.Points;
                double len = lp.Count > 1 ? Math.Sqrt(Math.Pow(lp[1].X - lp[0].X, 2) + Math.Pow(lp[1].Y - lp[0].Y, 2)) : 0;
                if (len <= 3) DrawingCanvas.Children.Remove(_currentLine);
                else PushAction(new AddShapeAction(_currentLine));
                _currentLine = null;
            }
            else if ((_isDrawingPixelate || _isDrawingSpotlight) && _currentRect != null)
            {
                bool spotlight = _isDrawingSpotlight;
                _isDrawingPixelate = false; _isDrawingSpotlight = false;
                double w = _currentRect.Width; double h = _currentRect.Height; double left = Canvas.GetLeft(_currentRect); double top = Canvas.GetTop(_currentRect);
                DrawingCanvas.Children.Remove(_currentRect); _currentRect = null;
                if (w > 5 && h > 5)
                {
                    if (spotlight) AddSpotlight(new Rect(left, top, w, h));
                    else ApplyPixelateToRegion(left, top, w, h);
                }
            }
            else if (_isDrawingBlur && _currentRect != null)
            {
                _isDrawingBlur = false;
                double w = _currentRect.Width; double h = _currentRect.Height; double left = Canvas.GetLeft(_currentRect); double top = Canvas.GetTop(_currentRect);
                DrawingCanvas.Children.Remove(_currentRect); _currentRect = null;
                if (w > 5 && h > 5) ApplyBlurToRegion(left, top, w, h);
            }
        }

        private void UpdateCropOverlayGeometry(Rect hole)

        {

            var outer = new RectangleGeometry(new Rect(0, 0, DrawingCanvas.Width, DrawingCanvas.Height));

            var inner = new RectangleGeometry(hole);

            CropDarkOverlay.Data = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);

        }

        // Зона обрізки в координатах полотна — щоб переставляти кнопки при прокрутці й масштабуванні
        private Rect? _cropControlsRect;

        private void ShowCropControls(double x, double y, double w, double h)
        {
            _cropControlsRect = new Rect(x, y, w, h);
            CropControlsPanel.IsVisible = true;
            PositionCropControls();
        }

        /// <summary>
        /// Ставить кнопки ✓/✗ під правим нижнім кутом зони обрізки (або всередині, якщо внизу немає місця)
        /// і завжди тримає їх у видимій частині редактора. Раніше кнопки були частиною масштабованого полотна:
        /// на великому знімку вони зменшувались разом із ним, а біля краю виїжджали за межі вікна.
        /// </summary>
        private void PositionCropControls()
        {
            if (_cropControlsRect is not Rect r || !CropControlsPanel.IsVisible) return;
            var topLeft = DrawingCanvas.TranslatePoint(r.TopLeft, CropControlsLayer);
            var bottomRight = DrawingCanvas.TranslatePoint(r.BottomRight, CropControlsLayer);
            if (topLeft == null || bottomRight == null) return;

            CropControlsPanel.Measure(Size.Infinity);
            double pw = CropControlsPanel.DesiredSize.Width, ph = CropControlsPanel.DesiredSize.Height;
            double layerW = CropControlsLayer.Bounds.Width, layerH = CropControlsLayer.Bounds.Height;
            const double gap = 8;

            double left = bottomRight.Value.X - pw;
            double top = bottomRight.Value.Y + gap;
            if (top + ph > layerH - gap) top = bottomRight.Value.Y - ph - gap;   // під зоною немає місця — всередину
            if (top < topLeft.Value.Y + gap && topLeft.Value.Y - ph - gap >= gap) top = topLeft.Value.Y - ph - gap; // зона низька — над нею

            left = Math.Clamp(left, gap, Math.Max(gap, layerW - pw - gap));
            top = Math.Clamp(top, gap, Math.Max(gap, layerH - ph - gap));
            Canvas.SetLeft(CropControlsPanel, left);
            Canvas.SetTop(CropControlsPanel, top);
        }

        private void ResetCropUI()

        {

            CropSelectionRect.IsVisible = false;

            CropControlsPanel.IsVisible = false;
            _cropControlsRect = null;

            CropDarkOverlay.Data = null;

        }

        private void PerformCrop()
        {
            try
            {
                double cropX = _finalCropRect.X;
                double cropY = _finalCropRect.Y;
                double cropW = _finalCropRect.Width;
                double cropH = _finalCropRect.Height;

                if (cropW <= 0 || cropH <= 0)
                {
                    cropX = Canvas.GetLeft(CropSelectionRect);
                    cropY = Canvas.GetTop(CropSelectionRect);
                    cropW = CropSelectionRect.Width;
                    cropH = CropSelectionRect.Height;
                }

                if (double.IsNaN(cropX) || cropX < 0) cropX = 0;
                if (double.IsNaN(cropY) || cropY < 0) cropY = 0;

                var rtb = CreateRenderedBitmap();
                int maxW = rtb.PixelSize.Width;
                int maxH = rtb.PixelSize.Height;

                int intX = Math.Clamp((int)Math.Round(cropX), 0, Math.Max(0, maxW - 1));
                int intY = Math.Clamp((int)Math.Round(cropY), 0, Math.Max(0, maxH - 1));
                int intW = Math.Clamp((int)Math.Round(cropW), 1, Math.Max(1, maxW - intX));
                int intH = Math.Clamp((int)Math.Round(cropH), 1, Math.Max(1, maxH - intY));

                // Обрізка в пам'яті замість «другий RenderTargetBitmap → PNG → декодування»
                var finalBmp = BitmapPixels.Crop(rtb, new PixelRect(intX, intY, intW, intH));
                rtb.Dispose();
                if (finalBmp == null) return;

                {

                    _originalImage = finalBmp;
                    UpdateCanvasImage(finalBmp);

                    _isSplitMode = false;
                    SecondScreenshotImage.IsVisible = false;
                    SecondScreenshotImage.Source = null;
                    SplitPlaceholder.IsVisible = false;

                    var shapesToRemove = DrawingCanvas.Children
                        .Where(c => c != ScreenshotImage && c != SecondScreenshotImage && c != SplitPlaceholder)
                        .ToList();
                    foreach (var shape in shapesToRemove) DrawingCanvas.Children.Remove(shape);
                }

                ExitCropMode();

                Avalonia.Threading.Dispatcher.UIThread.Post(() => FitToScreen(), Avalonia.Threading.DispatcherPriority.Loaded);

            }

            catch (Exception ex)

            {

                MessageBoxManager.GetMessageBoxStandard("Помилка", $"Не вдалося обрізати зображення: {ex.Message}").ShowWindowAsync();

            }
        }

        private void ExitCropMode()
        {
            _activeTool = DrawingTool.None;
            BtnCrop.Classes.Set("active", false);
            CropOverlayGrid.IsVisible = false;
            MainScrollViewer.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            MainScrollViewer.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            ResetCropUI();
        }

        private void BtnCrop_Click(object? sender, RoutedEventArgs e)
        {
            if (_activeTool == DrawingTool.Crop) { ExitCropMode(); return; }

            SetTool(DrawingTool.Crop, BtnCrop);

            MainScrollViewer.HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
            MainScrollViewer.VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden;
            CropOverlayGrid.IsVisible = true;
            UpdateCropOverlayGeometry(new Rect(0, 0, 0, 0));
        }

        private void BtnConfirmCrop_Click(object? sender, RoutedEventArgs e) => PerformCrop();
        private void BtnCancelCrop_Click(object? sender, RoutedEventArgs e) => ExitCropMode();

        private void UpdateArrowHead(IList<Point> pts, Point start, Point end)
        {
            if (Math.Abs(start.X - end.X) < 1) return;
            double headLength = Math.Max(12, _strokeWidth * 4); double headAngle = 30;
            double theta = Math.Atan2(end.Y - start.Y, end.X - start.X);
            double phi1 = theta + Math.PI - headAngle * Math.PI / 180;
            double headX1 = end.X + headLength * Math.Cos(phi1);
            double headY1 = end.Y + headLength * Math.Sin(phi1);
            double phi2 = theta + Math.PI + headAngle * Math.PI / 180;
            double headX2 = end.X + headLength * Math.Cos(phi2);
            double headY2 = end.Y + headLength * Math.Sin(phi2);
            pts.Add(new Point(headX1, headY1)); pts.Add(end); pts.Add(new Point(headX2, headY2));
        }

        private void StartEditingText(TextBlock tb)
        {
            double x = Canvas.GetLeft(tb); double y = Canvas.GetTop(tb);
            var editor = new TextBox
            {
                Text = tb.Text,
                FontSize = tb.FontSize,
                Foreground = tb.Foreground,
                FontWeight = FontWeight.Bold,
                Background = Brushes.White,
                BorderBrush = AccentBrush,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                MinWidth = 50
            };
            Canvas.SetLeft(editor, x); Canvas.SetTop(editor, y);
            DrawingCanvas.Children.Remove(tb); DrawingCanvas.Children.Add(editor);

            Avalonia.Threading.Dispatcher.UIThread.Post(() => { editor.Focus(); editor.SelectionStart = 0; editor.SelectionEnd = editor.Text?.Length ?? 0; });
            editor.LostFocus += (s, e) => FinishEditingText(editor);
        }

        private void FinishEditingText(TextBox editor)
        {
            if (!DrawingCanvas.Children.Contains(editor)) return;
            string content = editor.Text ?? "";
            if (!string.IsNullOrWhiteSpace(content)) CreateTextBlock(Canvas.GetLeft(editor), Canvas.GetTop(editor), content, editor.FontSize, editor.Foreground ?? Brushes.Red);
            DrawingCanvas.Children.Remove(editor);
        }

        private List<ShapeData> ExtractShapes()
        {
            var shapes = new List<ShapeData>();
            for (int i = 0; i < DrawingCanvas.Children.Count; i++)
            {
                var child = DrawingCanvas.Children[i];

                if (child == ScreenshotImage || child == SecondScreenshotImage || child == SplitPlaceholder) continue;

                double childLeft = double.IsNaN(Canvas.GetLeft(child)) ? 0 : Canvas.GetLeft(child);

                double childTop = double.IsNaN(Canvas.GetTop(child)) ? 0 : Canvas.GetTop(child);

                if (child is Panel p && p.Tag is ShapeType st && st == ShapeType.Stamp)

                {

                    var tb = p.Children.OfType<TextBlock>().FirstOrDefault();
                    var el = p.Children.OfType<Ellipse>().FirstOrDefault();
                    var border = p.Children.OfType<Border>().FirstOrDefault();
                    if (tb != null && (el != null || border != null))
                    {
                        string colorHex = border != null && border.Background is ISolidColorBrush bscb
                            ? bscb.Color.ToString()
                            : (el != null && el.Fill is ISolidColorBrush escb ? escb.Color.ToString() : "#FF00B0FF");
                        double halfW = p.Width > 0 ? p.Width / 2.0 : 14.0;
                        double halfH = p.Height > 0 ? p.Height / 2.0 : 14.0;
                        shapes.Add(new ShapeData { Type = ShapeType.Stamp, X1 = childLeft + halfW, Y1 = childTop + halfH, ColorHex = colorHex, StampNumber = int.Parse(tb.Text ?? "0") });

                    }

                }

                else if (child is Rectangle r)

                    shapes.Add(new ShapeData { Type = ShapeType.Rectangle, X1 = childLeft, Y1 = childTop, X2 = r.Width, Y2 = r.Height, ColorHex = ((ISolidColorBrush)r.Stroke!).Color.ToString(), StrokeWidth = r.StrokeThickness });

                else if (child is Ellipse ell && ell.Tag is ShapeType tEll && tEll == ShapeType.Ellipse)
                    shapes.Add(new ShapeData { Type = ShapeType.Ellipse, X1 = childLeft, Y1 = childTop, X2 = ell.Width, Y2 = ell.Height, ColorHex = ((ISolidColorBrush)ell.Stroke!).Color.ToString(), StrokeWidth = ell.StrokeThickness });

                else if (child is SpotlightShape spot)
                    shapes.Add(new ShapeData { Type = ShapeType.Spotlight, X1 = spot.Hole.X, Y1 = spot.Hole.Y, X2 = spot.Hole.Width, Y2 = spot.Hole.Height });

                else if (child is Polyline poly)

                {
                    var adjustedPoints = poly.Points.Select(pt => new MyPoint { X = pt.X + childLeft, Y = pt.Y + childTop }).ToList();

                    shapes.Add(new ShapeData { Type = poly.Tag is ShapeType t ? t : ShapeType.StatusArrow, PathPoints = adjustedPoints, ColorHex = ((ISolidColorBrush)poly.Stroke!).Color.ToString(), StrokeWidth = poly.StrokeThickness });

                }

                else if (child is TextBlock txt)

                    shapes.Add(new ShapeData { Type = ShapeType.Text, X1 = childLeft, Y1 = childTop, TextContent = txt.Text, FontSize = txt.FontSize, ColorHex = ((ISolidColorBrush)txt.Foreground!).Color.ToString() });

                else if (child is Border b && b.Tag is ShapeType tBlur && (tBlur == ShapeType.Blur || tBlur == ShapeType.Pixelate))

                    shapes.Add(new ShapeData { Type = tBlur, X1 = childLeft, Y1 = childTop, X2 = b.Width, Y2 = b.Height });
                else if (child is Panel pImg && pImg.Tag is ShapeType stImg && stImg == ShapeType.ImageOverlay)
                {
                    var img = pImg.Children.OfType<Image>().FirstOrDefault();
                    if (img?.Source is Bitmap bmp)
                    {
                        string base64 = "";
                        using (var ms = new MemoryStream())
                        {
                            bmp.Save(ms);
                            base64 = Convert.ToBase64String(ms.ToArray());
                        }
                        shapes.Add(new ShapeData
                        {
                            Type = ShapeType.ImageOverlay,
                            X1 = childLeft,
                            Y1 = childTop,
                            Width = pImg.Width,
                            Height = pImg.Height,
                            ImageBase64 = base64
                        });
                    }
                }

            }

            return shapes;

        }

        private void BtnSelect_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Select, BtnSelect);

        private void BtnStamp_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Stamp, BtnStamp);

        private void BtnRectangle_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Rectangle, BtnRectangle);
        private void BtnArrow_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Arrow, BtnArrow);
        private void BtnConnector_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Connector, BtnConnector);
        private void BtnPencil_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Pencil, BtnPencil);
        private void BtnText_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Text, BtnText);
        private void BtnHighlighter_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Highlighter, BtnHighlighter);
        private void BtnBlur_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Blur, BtnBlur);
        private void BtnEllipse_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Ellipse, BtnEllipse);
        private void BtnLine_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Line, BtnLine);
        private void BtnSpotlight_Click(object? sender, RoutedEventArgs e) => SetTool(DrawingTool.Spotlight, BtnSpotlight);

        private void ComboStrokeWidth_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (ComboStrokeWidth?.SelectedItem is ComboBoxItem item &&
                double.TryParse(item.Tag?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double w))
                _strokeWidth = w;
        }

        /// <summary>Кінець лінії, вирівняний до найближчого кута, кратного 45° (з Shift).</summary>
        private static Point SnapTo45(Point start, Point end)
        {
            var d = end - start;
            double len = Math.Sqrt(d.X * d.X + d.Y * d.Y);
            double angle = Math.Round(Math.Atan2(d.Y, d.X) / (Math.PI / 4)) * (Math.PI / 4);
            return new Point(start.X + len * Math.Cos(angle), start.Y + len * Math.Sin(angle));
        }

        /// <summary>
        /// Піксельне розмиття: фрагмент зменшується до «квадратиків» і показується без згладжування.
        /// На відміну від звичайного розмиття, з мозаїки неможливо відновити текст (паролі, номери карток).
        /// </summary>
        private void ApplyPixelateToRegion(double x, double y, double width, double height)
        {
            int ix = (int)Math.Max(0, x); int iy = (int)Math.Max(0, y);
            int iw = (int)Math.Min(DrawingCanvas.Width - ix, width); int ih = (int)Math.Min(DrawingCanvas.Height - iy, height);
            if (iw <= 0 || ih <= 0) return;

            Bitmap? cropped;
            using (var rtb = CreateRenderedBitmap())
            {
                cropped = BitmapPixels.Crop(rtb, new PixelRect(ix, iy, iw, ih));
            }
            if (cropped == null) return;

            const int block = 12;
            // Bitmap.CreateScaledBitmap не працює з WriteableBitmap/RenderTargetBitmap — масштабуємо через BitmapPixels
            var small = BitmapPixels.Scale(cropped, new PixelSize(Math.Max(1, iw / block), Math.Max(1, ih / block)), BitmapInterpolationMode.MediumQuality);
            cropped.Dispose();

            var img = new Image { Source = small, Width = iw, Height = ih, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapInterpolationMode(img, BitmapInterpolationMode.None);
            var container = new Border { Width = iw, Height = ih, Child = img, ClipToBounds = true, Tag = ShapeType.Pixelate };

            Canvas.SetLeft(container, ix); Canvas.SetTop(container, iy);
            DrawingCanvas.Children.Add(container);
            PushAction(new AddShapeAction(container));
        }

        /// <summary>Прожектор: напівпрозоре затемнення всього полотна з «діркою» над потрібною областю.</summary>
        private void AddSpotlight(Rect hole)
        {
            var spot = new SpotlightShape { Fill = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Tag = ShapeType.Spotlight };
            UpdateSpotlightGeometry(spot, hole);
            Canvas.SetLeft(spot, 0); Canvas.SetTop(spot, 0);
            DrawingCanvas.Children.Add(spot);
            PushAction(new AddShapeAction(spot));
        }

        private void UpdateSpotlightGeometry(SpotlightShape spot, Rect hole)
        {
            spot.Hole = hole;
            double w = double.IsNaN(DrawingCanvas.Width) ? DrawingCanvas.Bounds.Width : DrawingCanvas.Width;
            double h = double.IsNaN(DrawingCanvas.Height) ? DrawingCanvas.Bounds.Height : DrawingCanvas.Height;
            var geometry = new GeometryGroup { FillRule = FillRule.EvenOdd };
            geometry.Children.Add(new RectangleGeometry(new Rect(0, 0, w, h)));
            geometry.Children.Add(new RectangleGeometry(hole));
            spot.Data = geometry;
        }

        private void SetTool(DrawingTool tool, Button btn)
        {
            if (_activeTool == DrawingTool.Crop) ExitCropMode();

            ColorPalettePopup.IsOpen = false;
            _activeTool = _activeTool == tool ? DrawingTool.None : tool;
            UpdateToolHighlight(btn);
        }

        // ---- Групи інструментів (2.7.0) ----
        // Кнопка групи показує й активує останній вибраний у ній інструмент; стрілка поруч відкриває варіанти.

        private DrawingTool _shapesTool = DrawingTool.Rectangle;
        private DrawingTool _linesTool = DrawingTool.Arrow;
        private DrawingTool _drawTool = DrawingTool.Pencil;
        private DrawingTool _hideTool = DrawingTool.Blur;

        private Button[] AllToolButtons => new[]
        {
            BtnSelect, BtnCrop, BtnStamp, BtnText, BtnImageGroup,
            BtnShapesGroup, BtnLinesGroup, BtnDrawGroup, BtnHideGroup,
            BtnRectangle, BtnEllipse, BtnArrow, BtnLine, BtnConnector, BtnPencil, BtnHighlighter, BtnBlur, BtnSpotlight
        };

        private (Button Group, Material.Icons.Avalonia.MaterialIcon Icon)? GroupFor(DrawingTool tool) => tool switch
        {
            DrawingTool.Rectangle or DrawingTool.Ellipse => (BtnShapesGroup, IcoShapesGroup),
            DrawingTool.Arrow or DrawingTool.Line or DrawingTool.Connector => (BtnLinesGroup, IcoLinesGroup),
            DrawingTool.Pencil or DrawingTool.Highlighter => (BtnDrawGroup, IcoDrawGroup),
            DrawingTool.Blur or DrawingTool.Spotlight => (BtnHideGroup, IcoHideGroup),
            _ => null
        };

        private static Material.Icons.MaterialIconKind IconFor(DrawingTool tool) => tool switch
        {
            DrawingTool.Ellipse => Material.Icons.MaterialIconKind.EllipseOutline,
            DrawingTool.Arrow => Material.Icons.MaterialIconKind.ArrowTopRight,
            DrawingTool.Line => Material.Icons.MaterialIconKind.VectorLine,
            DrawingTool.Connector => Material.Icons.MaterialIconKind.VectorPolyline,
            DrawingTool.Pencil => Material.Icons.MaterialIconKind.Pencil,
            DrawingTool.Highlighter => Material.Icons.MaterialIconKind.Marker,
            DrawingTool.Blur => Material.Icons.MaterialIconKind.Blur,
            DrawingTool.Spotlight => Material.Icons.MaterialIconKind.SpotlightBeam,
            _ => Material.Icons.MaterialIconKind.RectangleOutline
        };

        private Button ButtonFor(DrawingTool tool) => tool switch
        {
            DrawingTool.Select => BtnSelect,
            DrawingTool.Crop => BtnCrop,
            DrawingTool.Stamp => BtnStamp,
            DrawingTool.Text => BtnText,
            DrawingTool.Rectangle => BtnRectangle,
            DrawingTool.Ellipse => BtnEllipse,
            DrawingTool.Arrow => BtnArrow,
            DrawingTool.Line => BtnLine,
            DrawingTool.Connector => BtnConnector,
            DrawingTool.Pencil => BtnPencil,
            DrawingTool.Highlighter => BtnHighlighter,
            DrawingTool.Blur => BtnBlur,
            DrawingTool.Spotlight => BtnSpotlight,
            _ => BtnSelect
        };

        /// <summary>Підсвічує активний інструмент (клас "active" — стиль у App.axaml) і оновлює іконку групи.</summary>
        private void UpdateToolHighlight(Button btn)
        {
            foreach (var b in AllToolButtons) b.Classes.Set("active", false);
            foreach (var b in _inlineButtons.Values) b.Classes.Set("active", false);
            if (_inlineButtons.TryGetValue(_activeTool, out var inlineBtn)) inlineBtn.Classes.Set("active", true);
            if (_isSplitMode) BtnImageGroup.Classes.Set("active", true);
            _inlineSplitButton?.Classes.Set("active", _isSplitMode);

            foreach (var more in new[] { BtnShapesGroupMore, BtnLinesGroupMore, BtnDrawGroupMore, BtnHideGroupMore })
                more.Flyout?.Hide();

            if (_activeTool == DrawingTool.None) return;

            var group = GroupFor(_activeTool);
            if (group is { } g)
            {
                g.Group.Classes.Set("active", true);
                g.Icon.Kind = IconFor(_activeTool);
                ButtonFor(_activeTool).Classes.Set("active", true); // і пункт у меню групи
                switch (_activeTool)
                {
                    case DrawingTool.Rectangle or DrawingTool.Ellipse: _shapesTool = _activeTool; break;
                    case DrawingTool.Arrow or DrawingTool.Line or DrawingTool.Connector: _linesTool = _activeTool; break;
                    case DrawingTool.Pencil or DrawingTool.Highlighter: _drawTool = _activeTool; break;
                    case DrawingTool.Blur or DrawingTool.Spotlight: _hideTool = _activeTool; break;
                }
            }
            else
            {
                btn.Classes.Set("active", true);
            }
        }

        // ---- Розгорнута панель (2.7.2): коли є місце, інструменти груп показуються окремими кнопками ----

        // Пара «згорнутий вигляд (група зі стрілкою / кнопка з меню) — окремі кнопки»
        private readonly List<(Control Collapsed, StackPanel Inline)> _toolGroups = new();
        private readonly Dictionary<DrawingTool, Button> _inlineButtons = new();
        private Button? _inlineSplitButton;
        private double _inlineExtraWidth = -1;
        private bool _toolbarExpanded;

        private static string ToolTipFor(DrawingTool tool) => tool switch
        {
            DrawingTool.Rectangle => "Прямокутник (R)",
            DrawingTool.Ellipse => "Еліпс (E)",
            DrawingTool.Arrow => "Стрілка (A)",
            DrawingTool.Line => "Пряма лінія (L, Shift — під 45°)",
            DrawingTool.Connector => "Зв'язок (лінія з точками)",
            DrawingTool.Pencil => "Олівець (P)",
            DrawingTool.Highlighter => "Маркер (H)",
            DrawingTool.Blur => "Розмиття — для паролів і особистих даних (B)",
            DrawingTool.Spotlight => "Прожектор: затемнити все, крім області (S)",
            _ => ""
        };

        private static Button MakeInlineButton(Material.Icons.MaterialIconKind kind, string tip, Action onClick, IBrush? iconBrush = null)
        {
            var icon = new Material.Icons.Avalonia.MaterialIcon { Kind = kind };
            if (iconBrush != null) icon.Foreground = iconBrush;
            var b = new Button { Content = icon };
            b.Classes.Add("Basic");
            b.Classes.Add("tool");
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => onClick();
            return b;
        }

        private void AddInlineGroup(Control collapsed, params Button[] buttons)
        {
            if (collapsed.Parent is not Panel host) return;
            var inline = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 2, IsVisible = false };
            foreach (var b in buttons) inline.Children.Add(b);
            host.Children.Insert(host.Children.IndexOf(collapsed) + 1, inline);
            _toolGroups.Add((collapsed, inline));
        }

        /// <summary>Створює окремі кнопки для вмісту кожної групи й меню (сховані, поки місця не вистачає).</summary>
        private void BuildInlineToolButtons()
        {
            Button Tool(DrawingTool t)
            {
                var b = MakeInlineButton(IconFor(t), ToolTipFor(t), () => SetTool(t, ButtonFor(t)));
                _inlineButtons[t] = b;
                return b;
            }

            _inlineSplitButton = MakeInlineButton(Material.Icons.MaterialIconKind.ViewSplitVertical, "Додати зображення поруч (колаж)",
                () => BtnSplit_Click(this, new RoutedEventArgs()));
            AddInlineGroup(BtnImageGroup,
                MakeInlineButton(Material.Icons.MaterialIconKind.ImageAdd, "Вставити картинку поверх (Ctrl+V)", () => BtnPasteImage_Click(this, new RoutedEventArgs())),
                _inlineSplitButton);
            if (BtnShapesGroup.Parent is Control shapes) AddInlineGroup(shapes, Tool(DrawingTool.Rectangle), Tool(DrawingTool.Ellipse));
            if (BtnLinesGroup.Parent is Control lines) AddInlineGroup(lines, Tool(DrawingTool.Arrow), Tool(DrawingTool.Line), Tool(DrawingTool.Connector));
            if (BtnDrawGroup.Parent is Control draw) AddInlineGroup(draw, Tool(DrawingTool.Pencil), Tool(DrawingTool.Highlighter));
            if (BtnHideGroup.Parent is Control hide) AddInlineGroup(hide, Tool(DrawingTool.Blur), Tool(DrawingTool.Spotlight));
            AddInlineGroup(BtnShareGroup,
                MakeInlineButton(Material.Icons.MaterialIconKind.CloudUploadOutline, "Публічне посилання (catbox.moe)", () => BtnPublicUpload_Click(this, new RoutedEventArgs())),
                MakeInlineButton(Material.Icons.MaterialIconKind.GoogleDrive, "Завантажити в мій Google Drive", () => BtnDriveUpload_Click(this, new RoutedEventArgs()),
                    new SolidColorBrush(Color.Parse("#4688F4")))); // бренд Google Drive

            if (BtnSelect.Parent is Control left && left.Parent is Control grid)
            {
                left.SizeChanged += (_, _) => UpdateToolbarMode();
                grid.SizeChanged += (_, _) => UpdateToolbarMode();
            }
        }

        private static double RowWidth(Panel row) // ширина видимих кнопок рядка з урахуванням проміжків
        {
            var visible = row.Children.Where(c => c.IsVisible).ToList();
            double spacing = row is StackPanel sp ? sp.Spacing : 0;
            return visible.Sum(c => c.DesiredSize.Width) + spacing * Math.Max(0, visible.Count - 1);
        }

        /// <summary>
        /// Вирішує, чи показувати вміст груп окремими кнопками: так, якщо розгорнута панель уміщується в ширину
        /// вікна. Інакше — групи зі стрілкою й меню (як у 2.7.0, без прокрутки).
        /// </summary>
        private void UpdateToolbarMode()
        {
            if (_toolGroups.Count == 0 || BtnSelect.Parent is not StackPanel left || left.Parent is not Grid grid) return;
            var right = grid.Children.OfType<StackPanel>().FirstOrDefault(c => Grid.GetColumn(c) == 1);
            if (right == null || grid.Bounds.Width <= 0) return;

            if (_inlineExtraWidth < 0)
            {
                if (_toolbarExpanded) return;
                double toolW = BtnText.DesiredSize.Width;
                if (toolW <= 0) return;
                _inlineExtraWidth = _toolGroups.Sum(g => g.Inline.Children.Count * toolW + 2 * (g.Inline.Children.Count - 1) - g.Collapsed.DesiredSize.Width);
            }

            double current = RowWidth(left) + RowWidth(right);
            double needExpanded = current + (_toolbarExpanded ? 0 : _inlineExtraWidth);
            bool expand = needExpanded <= grid.Bounds.Width - 16;
            if (expand == _toolbarExpanded) return;

            _toolbarExpanded = expand;
            foreach (var (collapsed, inline) in _toolGroups)
            {
                collapsed.IsVisible = !expand;
                inline.IsVisible = expand;
            }
        }

        private void BtnShapesGroup_Click(object? sender, RoutedEventArgs e) => SetTool(_shapesTool, ButtonFor(_shapesTool));
        private void BtnLinesGroup_Click(object? sender, RoutedEventArgs e) => SetTool(_linesTool, ButtonFor(_linesTool));
        private void BtnDrawGroup_Click(object? sender, RoutedEventArgs e) => SetTool(_drawTool, ButtonFor(_drawTool));
        private void BtnHideGroup_Click(object? sender, RoutedEventArgs e) => SetTool(_hideTool, ButtonFor(_hideTool));

        /// <summary>Гарячі клавіші інструментів (без модифікаторів і не під час введення тексту).</summary>
        private bool HandleToolShortcut(Key key)
        {
            DrawingTool? tool = key switch
            {
                Key.V => DrawingTool.Select,
                Key.C => DrawingTool.Crop,
                Key.N => DrawingTool.Stamp,
                Key.R => DrawingTool.Rectangle,
                Key.E => DrawingTool.Ellipse,
                Key.A => DrawingTool.Arrow,
                Key.L => DrawingTool.Line,
                Key.P => DrawingTool.Pencil,
                Key.H => DrawingTool.Highlighter,
                Key.T => DrawingTool.Text,
                Key.B => DrawingTool.Blur,
                Key.S => DrawingTool.Spotlight,
                _ => null
            };
            if (tool is not { } t) return false;
            if (t == DrawingTool.Crop) { BtnCrop_Click(BtnCrop, new RoutedEventArgs()); return true; }
            if (_activeTool == t) return true; // клавіша не вимикає вже вибраний інструмент
            SetTool(t, ButtonFor(t));
            return true;
        }

        // Ctrl на Windows, Cmd на Mac (раніше Cmd+Z / Cmd+V на Mac не працювали — перевірявся лише Ctrl)
        // Акцент палітри для службових рамок (редагування тексту, виділення вставленої картинки)
        private IBrush AccentBrush => AppTheme.Brush(this, "AccentBrush", Brushes.SeaGreen);

        private static KeyModifiers PrimaryModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        private void Window_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.KeyModifiers == KeyModifiers.None && FocusManager?.GetFocusedElement() is not TextBox && !_isSpaceDown
                && HandleToolShortcut(e.Key))
            {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.S && e.KeyModifiers == PrimaryModifier)
            {
                SaveAsFileAsync().FireAndForget("Зберегти як файл");
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && _activeTool == DrawingTool.Crop) { ExitCropMode(); e.Handled = true; return; }
            if (e.Key == Key.Enter && _activeTool == DrawingTool.Crop && CropControlsPanel.IsVisible) { PerformCrop(); e.Handled = true; return; }

            if (e.Key == Key.Space && !_isSpaceDown && (FocusManager?.GetFocusedElement() is not TextBox))
            {

                _isSpaceDown = true;

                Cursor = new Cursor(StandardCursorType.Hand);

                e.Handled = true;

                return;

            }

            if (e.Key == Key.V && e.KeyModifiers == PrimaryModifier)
            {
                if (_isSplitMode)
                {
                    PasteSecondImageFromClipboardAsync().FireAndForget("Вставка другого зображення");
                }
                else
                {
                    PasteOverlayImageFromClipboard();
                }
                e.Handled = true;
                return;
            }

            if ((e.Key == Key.Delete || e.Key == Key.Back) && _activeTool == DrawingTool.Select && _draggedElement != null)
            {
                var elem = _draggedElement;
                _draggedElement = null;
                DrawingCanvas.Children.Remove(elem);
                PushAction(new RemoveShapeAction(elem));
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Z && e.KeyModifiers == PrimaryModifier)

            {

                Undo();

                e.Handled = true;

                return;

            }

            if ((e.Key == Key.Y && e.KeyModifiers == PrimaryModifier) ||
                (e.Key == Key.Z && (e.KeyModifiers == (PrimaryModifier | KeyModifiers.Shift))))
            {
                Redo();
                e.Handled = true;
                return;
            }
        }

        private void Window_KeyUp(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                _isSpaceDown = false;

                if (!_isPanning) Cursor = Cursor.Default;

            }

        }

        private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)

        {

            if (!_isSavedToHistory)
            {
                SaveToHistoryDatabase();
            }

        }

        private RenderTargetBitmap CreateRenderedBitmap()
        {
            // Зум реалізовано через батьківський LayoutTransformControl, а RenderTargetBitmap.Render(DrawingCanvas)
            // трансформацій предків не враховує — тож перемикати зум на 1.0 і двічі перебудовувати розмітку
            // (від цього полотно блимало й підвисало на кожному «Копіювати»/«Зберегти») не потрібно.
            int w = (int)DrawingCanvas.Width; int h = (int)DrawingCanvas.Height;
            if (w <= 0) w = 1;
            if (h <= 0) h = 1;

            var rtb = new RenderTargetBitmap(new PixelSize(w, h), new Vector(96, 96));

            var overlayDecorations = DrawingCanvas.Children
                .OfType<Panel>()
                .Where(p => p.Tag is ShapeType st && st == ShapeType.ImageOverlay)
                .SelectMany(p => p.Children.OfType<Border>().Where(b => b.Name == "OverlayResizeHandle" || b.Name == "OverlaySelectionBorder"))
                .ToList();
            foreach (var d in overlayDecorations) d.IsVisible = false;

            rtb.Render(DrawingCanvas);

            foreach (var d in overlayDecorations) d.IsVisible = true;

            return rtb;
        }

        // ФІКС 1: Використовуємо вже створену картинку для збереження історії, щоб не рендерити двічі!

        // Останнє фонове збереження в історію: при закритті редактора чекаємо його, перш ніж повертатися в історію
        private Task _pendingHistorySave = Task.CompletedTask;

        private void SaveToHistoryDatabase(RenderTargetBitmap? rtb = null)
        {
            try
            {
                bool ownsRender = rtb == null;
                var finalRtb = rtb ?? CreateRenderedBitmap();

                // Копія пікселів у звичайну пам'ять (швидко, у UI-потоці): RenderTargetBitmap може бути на GPU
                // і ним ще користується викликач (буфер обміну, завантаження) — у фоновий потік його не віддаємо.
                var snapshot = BitmapPixels.Crop(finalRtb, new PixelRect(0, 0, finalRtb.PixelSize.Width, finalRtb.PixelSize.Height));
                if (ownsRender) finalRtb.Dispose();
                if (snapshot == null) throw new InvalidOperationException("Порожнє зображення");

                bool isNew = string.IsNullOrEmpty(_editingId);
                _editingId = HistoryManager.ReserveId(_editingId);
                var task = HistoryManager.SaveScreenshotInBackground(_editingId, isNew, _originalImage, snapshot, ExtractShapes(), snapshot);
                _isSavedToHistory = true;
                _pendingHistorySave = task;
                _ = ReportHistorySaveErrorAsync(task);
            }
            catch (Exception ex)
            {
                // Раніше тут був порожній catch: якщо диск повний або файл зайнятий,
                // скріншот мовчки не потрапляв в історію. Тепер — лог і повідомлення.
                AppLog.Error(ex, "Збереження в історію");
                MessageBoxManager.GetMessageBoxStandard("Помилка", $"Не вдалося зберегти скріншот в історію: {ex.Message}").ShowWindowAsync();
            }
        }

        private async Task ReportHistorySaveErrorAsync(Task saveTask)
        {
            try
            {
                await saveTask;
            }
            catch (Exception ex)
            {
                _isSavedToHistory = false;
                await MessageBoxManager.GetMessageBoxStandard("Помилка", $"Не вдалося зберегти скріншот в історію: {ex.Message}").ShowWindowAsync();
            }
        }

        // ФІКС 2: ПОВЕРНУЛИ ПЕРЕВІРЕНИЙ КОД WINFORMS ДЛЯ WINDOWS
        private async Task CopyImageToClipboardDirectly(Bitmap? sourceBitmap = null)
        {
            var rtb = sourceBitmap ?? CreateRenderedBitmap();
            try
            {
                await ClipboardHelper.CopyImageAsync(rtb, TopLevel.GetTopLevel(this));
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Копіювання в буфер обміну");
                var box = MessageBoxManager.GetMessageBoxStandard("Помилка Буфера", $"Помилка: {ex.Message}\n\nСтек: {ex.StackTrace}");
                await box.ShowWindowDialogAsync(this);
            }
            finally
            {
                // Рендер, створений тут, далі не потрібен — звільняємо одразу
                if (sourceBitmap == null) rtb.Dispose();
            }
        }

        private async void BtnDriveUpload_Click(object? sender, RoutedEventArgs e)
        {
            if (!await UploadConsent.ConfirmAsync(this)) return;
            string? tempPath = null;
            try
            {
                ToastNotification.IsVisible = true;
                ToastText.Text = "Завантаження на Google Drive...";

                var rtb = CreateRenderedBitmap();
                if (!_isSavedToHistory) SaveToHistoryDatabase(rtb);

                tempPath = Path.Combine(Path.GetTempPath(), $"upload_{Guid.NewGuid()}.png");
                using (var fs = new FileStream(tempPath, FileMode.Create)) { rtb.Save(fs); }
                rtb.Dispose();

                string webLink = await GoogleDriveManager.UploadImageAsync(tempPath);

                if (SettingsManager.Current.CopyLinkOnUpload)
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null) await clipboard.SetTextAsync(webLink);
                    ToastText.Text = "Посилання (Drive) скопійовано!";
                }
                else
                {
                    ToastText.Text = "Успішно завантажено!";
                }

                await System.Threading.Tasks.Task.Delay(1500);
                ToastNotification.IsVisible = false;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Завантаження в хмару");
                var box = MessageBoxManager.GetMessageBoxStandard("Помилка", $"Помилка завантаження: {ex.Message}");
                await box.ShowWindowDialogAsync(this);
                ToastNotification.IsVisible = false;
            }
            finally
            {
                // Раніше тимчасовий PNG видалявся лише при успіху — після кожної помилки лишався в %TEMP%.
                if (tempPath != null)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception logEx) { AppLog.Warn(logEx); }
                }
            }
        }

        private async void BtnPublicUpload_Click(object? sender, RoutedEventArgs e)
        {
            if (!await UploadConsent.ConfirmAsync(this)) return;
            string? tempPath = null;
            try
            {
                ToastNotification.IsVisible = true;
                ToastText.Text = "Створення публічного посилання...";

                var rtb = CreateRenderedBitmap();
                if (!_isSavedToHistory) SaveToHistoryDatabase(rtb);

                tempPath = Path.Combine(Path.GetTempPath(), $"upload_{Guid.NewGuid()}.png");
                using (var fs = new FileStream(tempPath, FileMode.Create)) { rtb.Save(fs); }
                rtb.Dispose();

                string webLink = await PublicCloudManager.UploadImageAsync(tempPath);

                if (SettingsManager.Current.CopyLinkOnUpload)
                {
                    var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                    if (clipboard != null) await clipboard.SetTextAsync(webLink);
                    ToastText.Text = "Публічне посилання скопійовано!";
                }
                else
                {
                    ToastText.Text = "Успішно завантажено!";
                }

                await System.Threading.Tasks.Task.Delay(1500);
                ToastNotification.IsVisible = false;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Завантаження в хмару");
                var box = MessageBoxManager.GetMessageBoxStandard("Помилка", $"Помилка завантаження: {ex.Message}");
                await box.ShowWindowDialogAsync(this);
                ToastNotification.IsVisible = false;
            }
            finally
            {
                // Раніше тимчасовий PNG видалявся лише при успіху — після кожної помилки лишався в %TEMP%.
                if (tempPath != null)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch (Exception logEx) { AppLog.Warn(logEx); }
                }
            }
        }

        private async void BtnOcr_Click(object? sender, RoutedEventArgs e)
        {
            BtnOcr.IsEnabled = false;
            try
            {
                ToastText.Text = "Розпізнаю текст…";
                ToastNotification.IsVisible = true;

                string text;
                using (var rtb = CreateRenderedBitmap())
                {
                    text = await OcrService.RecognizeAsync(rtb);
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    ToastText.Text = "Текст не знайдено";
                }
                else
                {
                    await ClipboardHelper.CopyTextAsync(text, TopLevel.GetTopLevel(this));
                    ToastText.Text = $"Текст скопійовано ({text.Length} симв.)";
                }
                await Task.Delay(1800);
                ToastNotification.IsVisible = false;
            }
            catch (Exception ex)
            {
                ToastNotification.IsVisible = false;
                if (ex is not OcrUnavailableException) AppLog.Error(ex, "OCR у редакторі");
                await MessageBoxManager.GetMessageBoxStandard("Розпізнавання тексту", ex.Message).ShowWindowDialogAsync(this);
            }
            finally
            {
                BtnOcr.IsEnabled = true;
            }
        }

        private void BtnSaveAs_Click(object? sender, RoutedEventArgs e)
        {
            SaveAsFileAsync().FireAndForget("Зберегти як файл");
        }

        /// <summary>«Зберегти як…»: діалог вибору файлу (PNG / JPG), стартова папка — з налаштувань.</summary>
        private async Task SaveAsFileAsync()
        {
            try
            {
                string ext = ImageExport.PreferredExtension;
                var png = new FilePickerFileType("PNG") { Patterns = new[] { "*.png" }, MimeTypes = new[] { "image/png" } };
                var jpg = new FilePickerFileType("JPEG") { Patterns = new[] { "*.jpg", "*.jpeg" }, MimeTypes = new[] { "image/jpeg" } };

                var options = new FilePickerSaveOptions
                {
                    Title = "Зберегти скріншот",
                    SuggestedFileName = ImageExport.BuildFileName(DateTime.Now, ext),
                    DefaultExtension = ext,
                    FileTypeChoices = ext == "jpg" ? new[] { jpg, png } : new[] { png, jpg },
                    ShowOverwritePrompt = true
                };
                try
                {
                    System.IO.Directory.CreateDirectory(ImageExport.SaveFolder);
                    var start = await StorageProvider.TryGetFolderFromPathAsync(ImageExport.SaveFolder);
                    if (start != null) options.SuggestedStartLocation = start;
                }
                catch (Exception ex) { AppLog.Warn(ex); }

                var file = await StorageProvider.SaveFilePickerAsync(options);
                if (file == null) return;
                string? path = file.TryGetLocalPath();
                if (string.IsNullOrEmpty(path)) return;

                using (var rtb = CreateRenderedBitmap())
                {
                    if (!_isSavedToHistory) SaveToHistoryDatabase(rtb);
                    ImageExport.Save(rtb, path);
                }

                ToastText.Text = $"Збережено: {System.IO.Path.GetFileName(path)}";
                ToastNotification.IsVisible = true;
                await Task.Delay(1500);
                ToastNotification.IsVisible = false;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Зберегти як файл");
                await MessageBoxManager.GetMessageBoxStandard("Помилка", $"Не вдалося зберегти файл: {ex.Message}").ShowWindowDialogAsync(this);
            }
        }

        private async void BtnSave_Click(object? sender, RoutedEventArgs e)
        {
            BtnSave.IsEnabled = false;

            try
            {
                // Робимо єдиний зліпок екрану
                var rtb = CreateRenderedBitmap();

                SaveToHistoryDatabase(rtb);

                // Викликаємо відремонтований буфер
                await CopyImageToClipboardDirectly(rtb);

                // Автозбереження у файл (налаштування «Автоматично зберігати кожен скріншот»)
                if (SettingsManager.Current.AutoSaveToFolder)
                {
                    try { ImageExport.SaveToFolder(rtb); }
                    catch (Exception ex) { AppLog.Error(ex, "Автозбереження у файл"); }
                }
                rtb.Dispose();

                ToastText.Text = "Картинку скопійовано в буфер!";
                ToastNotification.IsVisible = true;
                await Task.Delay(800);
            }
            finally
            {
                this.Close();
            }
        }

        private void BtnCurrentColorIndicator_Click(object? sender, RoutedEventArgs e) { ColorPalettePopup.IsOpen = !ColorPalettePopup.IsOpen; }
        private void BtnColorVariant_Click(object? sender, RoutedEventArgs e) { if (sender is Button b && b.Background is ISolidColorBrush br) { _currentBrush = br; ColorSwatch.Background = br; ColorPalettePopup.IsOpen = false; } }

        private void ReturnToHistoryIfNeeded()
        {
            if (_returnedToHistory) return;
            _returnedToHistory = true;
            _historyOwner?.ReturnFromEditor(_sourceHistoryItem, isVideo: false);
        }

        private void BtnBackToHistory_Click(object? sender, RoutedEventArgs e)
        {
            this.Close();
        }

        protected override async void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                // Історія перечитує файли з диска — чекаємо, поки фонове збереження допише їх
                try { await _pendingHistorySave; } catch (Exception ex) { AppLog.Warn(ex); }
                ReturnToHistoryIfNeeded();
            }
            finally
            {
                ReleaseImages();
            }
        }

        /// <summary>
        /// Звільняє повнорозмірні бітмапи (нативна пам'ять Skia, десятки МБ на 4K-скріншот).
        /// Раніше вони жили до прибирання сміття, і програма в треї поступово «товстішала».
        /// </summary>
        private void ReleaseImages()
        {
            try
            {
                ScreenshotImage.Source = null;
                SecondScreenshotImage.Source = null;
                _originalImage?.Dispose();
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }
}
}
