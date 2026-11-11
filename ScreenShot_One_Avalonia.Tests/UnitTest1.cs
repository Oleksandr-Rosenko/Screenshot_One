using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media.Imaging;
using ScreenShot_One_Avalonia;
using Xunit;

namespace ScreenShot_One_Avalonia.Tests
{
    public class OptimizationAndFeatureTests
    {
        private static string GetSourceFilePath(string fileName)
        {
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "ScreenShot_One_Avalonia", fileName));
        }

        [Fact]
        public void HistoryItem_PropertyChanged_FiresWhenImageSourceChanges()
        {
            var item = new HistoryItem
            {
                FileName = "test_screenshot.png",
                Path = "C:\\test\\test_screenshot.png",
                DateString = "03.11.2026 12:00"
            };

            string? changedProp = null;
            item.PropertyChanged += (s, e) => changedProp = e.PropertyName;

            item.ImageSource = null;

            Assert.Equal(nameof(HistoryItem.ImageSource), changedProp);
        }

        [Fact]
        public void ShapeData_SerializationAndDeserialization_PreservesAllShapeTypes()
        {
            var shapes = new List<ShapeData>
            {
                new ShapeData
                {
                    Type = ShapeType.Rectangle,
                    X1 = 10, Y1 = 20, X2 = 100, Y2 = 200,
                    ColorHex = "#FFFF0000"
                },
                new ShapeData
                {
                    Type = ShapeType.Stamp,
                    X1 = 50, Y1 = 60,
                    StampNumber = 1,
                    ColorHex = "#FF00FF00"
                },
                new ShapeData
                {
                    Type = ShapeType.Text,
                    X1 = 30, Y1 = 40,
                    TextContent = "Test annotation",
                    FontSize = 18,
                    ColorHex = "#FFFFFFFF"
                },
                new ShapeData
                {
                    Type = ShapeType.Blur,
                    X1 = 5, Y1 = 5, X2 = 50, Y2 = 50
                },
                new ShapeData
                {
                    Type = ShapeType.Pencil,
                    PathPoints = new List<MyPoint> { new MyPoint { X = 1, Y = 1 }, new MyPoint { X = 5, Y = 5 } },
                    ColorHex = "#FF0000FF"
                }
            };

            string json = JsonSerializer.Serialize(shapes);
            var deserialized = JsonSerializer.Deserialize<List<ShapeData>>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(5, deserialized.Count);
            Assert.Equal(ShapeType.Rectangle, deserialized[0].Type);
            Assert.Equal(100, deserialized[0].X2);
            Assert.Equal(ShapeType.Stamp, deserialized[1].Type);
            Assert.Equal(1, deserialized[1].StampNumber);
            Assert.Equal("Test annotation", deserialized[2].TextContent);
            Assert.Equal(ShapeType.Blur, deserialized[3].Type);
            Assert.Equal(ShapeType.Pencil, deserialized[4].Type);
            Assert.Equal(2, deserialized[4].PathPoints.Count);
        }

        [Fact]
        public void UndoRedo_StackLogic_HandlesAdditionAndRemovalCorrectly()
        {
            var undoStack = new Stack<string>();
            var redoStack = new Stack<string>();

            undoStack.Push("ShapeA");
            redoStack.Clear();

            undoStack.Push("ShapeB");
            redoStack.Clear();

            Assert.Equal(2, undoStack.Count);
            Assert.Empty(redoStack);

            var undone = undoStack.Pop();
            redoStack.Push(undone);

            Assert.Equal("ShapeB", undone);
            Assert.Single(undoStack);
            Assert.Single(redoStack);

            var redone = redoStack.Pop();
            undoStack.Push(redone);

            Assert.Equal("ShapeB", redone);
            Assert.Equal(2, undoStack.Count);
            Assert.Empty(redoStack);
        }

        [Fact]
        public void VideoRecorder_HardwareEncoding_IsEnabledInCode()
        {
            string recorderPath = GetSourceFilePath("VideoRecorderManager.cs");
            Assert.True(File.Exists(recorderPath), $"File should exist at {recorderPath}");
            string code = File.ReadAllText(recorderPath);
            Assert.Contains("IsHardwareEncodingEnabled = true", code);
            Assert.Contains("IsLowLatencyEnabled = true", code);
            Assert.Contains("IsThrottlingDisabled = true", code);
            Assert.DoesNotContain("IsHardwareEncodingEnabled = false", code);
        }

        [Fact]
        public void VideoTrimming_BundledFFmpeg_ExistsInProjectDirectory()
        {
            string ffmpegPath = GetSourceFilePath("ffmpeg.exe");
            Assert.True(File.Exists(ffmpegPath), $"Bundled ffmpeg.exe should exist at {ffmpegPath}");
            var fileInfo = new FileInfo(ffmpegPath);
            Assert.True(fileInfo.Length > 10_000_000, "ffmpeg.exe should be a valid binary > 10MB");
        }

        [Fact]
        public void PublicCloudManager_UsesStaticHttpClientAndNoObsoleteProtocols()
        {
            string managerPath = GetSourceFilePath("PublicCloudManager.cs");
            Assert.True(File.Exists(managerPath), $"File should exist at {managerPath}");
            string code = File.ReadAllText(managerPath);
            Assert.Contains("private static readonly HttpClient _httpClient = new HttpClient();", code);
            Assert.DoesNotContain("ServicePointManager.SecurityProtocol", code);
        }

        [Fact]
        public void EditorWindow_Axaml_ContainsUndoRedoButtonsAndScrollViewer()
        {
            string axamlPath = GetSourceFilePath("EditorWindow.axaml");
            Assert.True(File.Exists(axamlPath), $"File should exist at {axamlPath}");
            string xaml = File.ReadAllText(axamlPath);
            Assert.Contains("Name=\"BtnUndo\"", xaml);
            Assert.Contains("Name=\"BtnRedo\"", xaml);
            Assert.Contains("Name=\"BtnBackToHistory\"", xaml);
            Assert.Contains("ScrollViewer", xaml);
        }

        [Fact]
        public void VideoEditorWindow_Axaml_ContainsTopToolbarWithBackToHistoryAndRangeTimeline()
        {
            string axamlPath = GetSourceFilePath("VideoEditorWindow.axaml");
            Assert.True(File.Exists(axamlPath), $"File should exist at {axamlPath}");
            string xaml = File.ReadAllText(axamlPath);
            Assert.Contains("Name=\"BtnBackToHistory\"", xaml);
            Assert.Contains("Name=\"TxtVideoTitle\"", xaml);
            Assert.Contains("Name=\"TrimHighlightGrid\"", xaml);
            Assert.Contains("Name=\"TrimPanel\"", xaml);
            Assert.Contains("Name=\"BtnPreviewTrim\"", xaml);
            Assert.Contains("Name=\"BtnRewind5\"", xaml);
            Assert.Contains("Name=\"BtnForward5\"", xaml);
        }

        [Theory]
        [InlineData(0, 100, 0, 100)]
        [InlineData(50, 40, 40, 41)]
        [InlineData(-10, 150, 0, 100)]
        public void VideoEditor_TrimRangeCalculation_EnforcesSafeBoundaries(double inStart, double inEnd, double expectedMinStart, double expectedMinEnd)
        {
            double start = Math.Clamp(inStart, 0, 100);
            double end = Math.Clamp(inEnd, 0, 100);

            if (start >= end - 1.0)
            {
                end = Math.Min(100, start + 1.0);
            }

            Assert.True(start <= end);
            Assert.True(start >= expectedMinStart);
            Assert.True(end >= expectedMinEnd);
        }

        [Fact]
        public void PencilAndHighlighter_PolylinePointAssignment_UpdatesPointsCollection()
        {
            // Verify that assigning a new AvaloniaList<Point> invalidates polyline geometry and updates points correctly
            var polyline = new Polyline();
            var pointsList = new List<Point>
            {
                new Point(10, 10),
                new Point(20, 25)
            };

            polyline.Points = new AvaloniaList<Point>(pointsList);

            Assert.NotNull(polyline.Points);
            Assert.Equal(2, polyline.Points.Count);
            Assert.Equal(new Point(10, 10), polyline.Points[0]);
            Assert.Equal(new Point(20, 25), polyline.Points[1]);

            // Append another point and reassign collection (the fix for real-time pencil/highlighter drawing)
            pointsList.Add(new Point(35, 40));
            polyline.Points = new AvaloniaList<Point>(pointsList);

            Assert.Equal(3, polyline.Points.Count);
            Assert.Equal(new Point(35, 40), polyline.Points[2]);
        }

        [Fact]
        public void EditorWindow_Code_EnsuresPencilAndHighlighterReassignPointsCollection()
        {
            string editorPath = GetSourceFilePath("EditorWindow.axaml.cs");
            Assert.True(File.Exists(editorPath), $"File should exist at {editorPath}");
            string code = File.ReadAllText(editorPath);

            // Verify that the code assigns a new AvaloniaList on point addition so Avalonia invalidates geometry
            Assert.Contains("_currentPencilPolyline.Points = new Avalonia.Collections.AvaloniaList<Point>(_pencilPoints);", code);
            Assert.Contains("_currentPencilPolyline.InvalidateVisual();", code);
        }

        [Fact]
        public void PencilAndHighlighter_ShapeData_SerializationPreservesAllPointsAndColor()
        {
            var freehandShape = new ShapeData
            {
                Type = ShapeType.Pencil,
                ColorHex = "#80FFFF00", // Semi-transparent yellow highlighter
                PathPoints = new List<MyPoint>
                {
                    new MyPoint { X = 5.5, Y = 12.3 },
                    new MyPoint { X = 15.0, Y = 22.1 },
                    new MyPoint { X = 45.8, Y = 80.0 }
                }
            };

            string json = JsonSerializer.Serialize(freehandShape);
            var restored = JsonSerializer.Deserialize<ShapeData>(json);

            Assert.NotNull(restored);
            Assert.Equal(ShapeType.Pencil, restored.Type);
            Assert.Equal("#80FFFF00", restored.ColorHex);
            Assert.Equal(3, restored.PathPoints.Count);
            Assert.Equal(5.5, restored.PathPoints[0].X);
            Assert.Equal(80.0, restored.PathPoints[2].Y);
        }

        [Fact]
        public void HistoryDecoding_Code_UsesDecodeToWidthForMemoryOptimization()
        {
            string historyPath = GetSourceFilePath("HistoryWindow.axaml.cs");
            Assert.True(File.Exists(historyPath), $"File should exist at {historyPath}");
            string code = File.ReadAllText(historyPath);

            // Verify that history thumbnails are decoded to a fixed width (280) to save RAM
            Assert.Contains("Bitmap.DecodeToWidth", code);
            Assert.Contains("280", code);
        }

        [Fact]
        public void VideoEditor_Code_EnforcesDraggingSuppressesTimerUpdate()
        {
            string videoEditorPath = GetSourceFilePath("VideoEditorWindow.axaml.cs");
            Assert.True(File.Exists(videoEditorPath), $"File should exist at {videoEditorPath}");
            string code = File.ReadAllText(videoEditorPath);

            // Verify that Timer_Tick checks _isDraggingSlider to prevent timeline jitter
            Assert.Contains("if (_isDraggingSlider) return;", code);
        }

        [Fact]
        public void VideoEditor_StepTrimCalculations_ClampsAccurately()
        {
            long totalMs = 30000; // 30-second video
            long startMs = 5000;
            long endMs = 25000;

            // Step Start forward +500ms
            long deltaMs = 500;
            long maxAllowed = endMs - 500;
            long newStartMs = Math.Clamp(startMs + deltaMs, 0, Math.Max(0, maxAllowed));
            Assert.Equal(5500, newStartMs);

            // Step Start past End limit
            newStartMs = Math.Clamp(startMs + 50000, 0, Math.Max(0, maxAllowed));
            Assert.Equal(24500, newStartMs); // Clamped so start < end

            // Step End backward -500ms
            long minAllowed = startMs + 500;
            long newEndMs = Math.Clamp(endMs - deltaMs, Math.Min(totalMs, minAllowed), totalMs);
            Assert.Equal(24500, newEndMs);

            // Step End backward past Start limit
            newEndMs = Math.Clamp(0, Math.Min(totalMs, minAllowed), totalMs);
            Assert.Equal(5500, newEndMs); // Clamped so end > start
        }

        [Fact]
        public void VideoEditor_Code_HandlesEndReachedAndReplaySafely()
        {
            string videoEditorPath = GetSourceFilePath("VideoEditorWindow.axaml.cs");
            Assert.True(File.Exists(videoEditorPath), $"File should exist at {videoEditorPath}");
            string code = File.ReadAllText(videoEditorPath);

            // Verify safe replay and end-of-media handling
            Assert.Contains("EnsurePlayerReadyForPlayback", code);
            Assert.Contains("current >= total - 150", code);
            Assert.Contains("MainPlayIcon.Kind = Material.Icons.MaterialIconKind.Play;", code);
        }

        [Fact]
        public void ScreenService_VirtualBoundsAndCursor_ProvideSafeCrossPlatformDefaults()
        {
            var bounds = ScreenService.GetVirtualScreenBounds();
            Assert.True(bounds.Width > 0, "Virtual bounds width must be positive");
            Assert.True(bounds.Height > 0, "Virtual bounds height must be positive");

            // Verify GetCursorPosition does not throw on any platform
            var cursor = ScreenService.GetCursorPosition();
            Assert.False(string.IsNullOrEmpty(cursor.ToString()));
        }

        [Fact]
        public void ScreenCaptureService_ProvidesPlatformInstance_AndAllowsMocking()
        {
            var initialService = ScreenCaptureService.Instance;
            Assert.NotNull(initialService);

            // Test mocking / injection capability for unit tests
            var mock = new WindowsScreenCaptureService();
            ScreenCaptureService.Instance = mock;
            Assert.Same(mock, ScreenCaptureService.Instance);

            // Restore original
            ScreenCaptureService.Instance = initialService;
        }

        [Fact]
        public void ScreenCaptureService_Architecture_IsUsedAcrossAllCaptureCallSites()
        {
            string appCode = File.ReadAllText(GetSourceFilePath("App.axaml.cs"));
            string mainCode = File.ReadAllText(GetSourceFilePath("MainWindow.axaml.cs"));
            string vrCode = File.ReadAllText(GetSourceFilePath("VideoRegionWindow.axaml.cs"));

            Assert.Contains("ScreenCaptureService.Instance.CaptureScreen", appCode);
            Assert.Contains("ScreenCaptureService.Instance.CaptureAllScreens", appCode);
            Assert.Contains("ScreenCaptureService.Instance.CaptureRect", mainCode);
            Assert.Contains("ScreenCaptureService.Instance.CaptureScreen", vrCode);
        }

        [Fact]
        public void VideoRecorderManager_Service_ProvidesPlatformInstance_AndAllowsMocking()
        {
            var initial = VideoRecorderManager.Instance;
            Assert.NotNull(initial);

            var displays = VideoRecorderManager.GetDisplays();
            Assert.NotNull(displays);

            var fallback = new FallbackVideoRecorderService();
            VideoRecorderManager.Instance = fallback;
            Assert.Same(fallback, VideoRecorderManager.Instance);
            Assert.False(VideoRecorderManager.IsRecording);

            // Restore original
            VideoRecorderManager.Instance = initial;
        }

        [Fact]
        public void VideoControlWindow_Code_IsDecoupledFromDirectRecorder()
        {
            string vcCode = File.ReadAllText(GetSourceFilePath("VideoControlWindow.axaml.cs"));
            Assert.Contains("VideoRecorderManager.GetDisplays()", vcCode);
            Assert.DoesNotContain("Recorder.GetDisplays()", vcCode);
        }

        [Fact]
        public void EditorWindow_PerformCrop_UsesFinalCropRectAndPreventsZeroSize()
        {
            string editorCode = File.ReadAllText(GetSourceFilePath("EditorWindow.axaml.cs"));
            Assert.DoesNotContain("_cropPixelRect", editorCode);
            Assert.Contains("_finalCropRect", editorCode);
            Assert.Contains("Math.Clamp((int)Math.Round(cropW), 1", editorCode);
            Assert.Contains("Math.Clamp((int)Math.Round(cropH), 1", editorCode);
        }

        [Fact]
        public void HistoryManager_SaveScreenshot_ReturnsId_AndEditorAvoidsDuplicateSaves()
        {
            string historyCode = File.ReadAllText(GetSourceFilePath("HistoryManager.cs"));
            Assert.Contains("public static string SaveScreenshot", historyCode);
            Assert.Contains("return id;", historyCode);

            string editorCode = File.ReadAllText(GetSourceFilePath("EditorWindow.axaml.cs"));
            // Редактор резервує ID синхронно, а сам запис іде у фоні (HistoryManager.SaveScreenshotInBackground)
            Assert.Contains("_editingId = HistoryManager.ReserveId(_editingId);", editorCode);
            Assert.Contains("HistoryManager.SaveScreenshotInBackground(_editingId", editorCode);
            Assert.Contains("if (!_isSavedToHistory)", editorCode);
            Assert.Contains("SaveToHistoryDatabase(rtb);", editorCode);
        }

        [Fact]
        public void ShapeData_Supports_ImageOverlay_WithBase64AndDimensions()
        {
            var shape = new ShapeData
            {
                Type = ShapeType.ImageOverlay,
                X1 = 150,
                Y1 = 200,
                Width = 320,
                Height = 240,
                ImageBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg=="
            };

            string json = JsonSerializer.Serialize(shape);
            var deserialized = JsonSerializer.Deserialize<ShapeData>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(ShapeType.ImageOverlay, deserialized.Type);
            Assert.Equal(150, deserialized.X1);
            Assert.Equal(200, deserialized.Y1);
            Assert.Equal(320, deserialized.Width);
            Assert.Equal(240, deserialized.Height);
            Assert.Equal(shape.ImageBase64, deserialized.ImageBase64);
        }

        [Fact]
        public void EditorWindow_Contains_OverlayPasteFeature()
        {
            string xamlCode = File.ReadAllText(GetSourceFilePath("EditorWindow.axaml"));
            Assert.Contains("Name=\"BtnPasteImage\"", xamlCode);
            Assert.Contains("Click=\"BtnPasteImage_Click\"", xamlCode);
            Assert.Contains("Ctrl+V", xamlCode);
            Assert.Contains("ImageAdd", xamlCode);

            string editorCode = File.ReadAllText(GetSourceFilePath("EditorWindow.axaml.cs"));
            Assert.Contains("PasteOverlayImageFromClipboard()", editorCode);
            Assert.Contains("GetImageFromClipboardAsync()", editorCode);
            Assert.Contains("AddImageOverlay(", editorCode);
            Assert.Contains("OverlayResizeHandle", editorCode);
            Assert.Contains("OverlaySelectionBorder", editorCode);
        }

        [Fact]
        public void AppSettings_UpdateDismissalFields_SerializeCorrectly()
        {
            var settings = new AppSettings
            {
                LastUpdateDismissedUtc = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc),
                LastDismissedVersion = "2.4.1"
            };

            string json = JsonSerializer.Serialize(settings);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal(settings.LastUpdateDismissedUtc, deserialized.LastUpdateDismissedUtc);
            Assert.Equal("2.4.1", deserialized.LastDismissedVersion);
        }

        [Fact]
        public void UpdateWindow_Architecture_And_SafetyGate_AreImplemented()
        {
            string axaml = File.ReadAllText(GetSourceFilePath("UpdateWindow.axaml"));
            Assert.Contains("RocketLaunch", axaml);
            Assert.Contains("ProgressBarUpdate", axaml);
            Assert.Contains("TxtChangelog", axaml);
            Assert.Contains("BtnLater", axaml);
            Assert.Contains("BtnAction", axaml);
            Assert.Contains("GlassCard", axaml);

            string code = File.ReadAllText(GetSourceFilePath("UpdateWindow.axaml.cs"));
            Assert.Contains("desktop.Windows.Any(w => w is EditorWindow || w is VideoEditorWindow)", code);
            Assert.Contains("CheckAndShowAsync(bool isManual = true)", code);
            Assert.Contains("TotalHours < 24", code);
            Assert.Contains("LoadReleaseNotesAsync", code);
            Assert.Contains("ApplyUpdatesAndRestart", code);
        }

        [Fact]
        public void App_Contains_BackgroundUpdateCheck_And_TrayMenuItem()
        {
            string appCode = File.ReadAllText(GetSourceFilePath("App.axaml.cs"));
            Assert.Contains("UpdateWindow.CheckAndShowAsync(isManual: false)", appCode);
            Assert.Contains("UpdateWindow.CheckAndShowAsync(isManual: true)", appCode);
            Assert.Contains("NativeMenuItem(\"Перевірити оновлення\")", appCode);
        }

        [Fact]
        public void AppSettings_Theme_Property_DefaultsToDark_And_SerializesCorrectly()
        {
            var defaultSettings = new AppSettings();
            Assert.Equal("Dark", defaultSettings.Theme);

            var lightSettings = new AppSettings { Theme = "Light" };
            string json = JsonSerializer.Serialize(lightSettings);
            var deserialized = JsonSerializer.Deserialize<AppSettings>(json);

            Assert.NotNull(deserialized);
            Assert.Equal("Light", deserialized.Theme);
        }

        [Fact]
        public void SettingsWindow_Contains_ThemeSelection_And_RollbackSupport()
        {
            string xaml = File.ReadAllText(GetSourceFilePath("SettingsWindow.axaml"));
            Assert.Contains("Name=\"ComboTheme\"", xaml);
            Assert.Contains("SelectionChanged=\"ComboTheme_SelectionChanged\"", xaml);
            Assert.Contains("Тема оформлення:", xaml);

            string code = File.ReadAllText(GetSourceFilePath("SettingsWindow.axaml.cs"));
            Assert.Contains("_initialTheme", code);
            Assert.Contains("ComboTheme_SelectionChanged", code);
            Assert.Contains("SettingsManager.ApplyTheme", code);
            Assert.Contains("protected override void OnClosing", code);
        }

        [Fact]

        public void App_And_Editor_Implement_AdaptiveCanvasTheming_And_SemanticTokens()

        {

            string appXaml = File.ReadAllText(GetSourceFilePath("App.axaml"));

            Assert.Contains("CanvasBackdropBrush", appXaml);

            Assert.Contains("Color=\"#18181B\"", appXaml);

            Assert.Contains("Color=\"#E2E8F0\"", appXaml);
            Assert.Contains("ToolbarIconBrush", appXaml);
            Assert.Contains("Color=\"#F4F4F5\"", appXaml);
            Assert.Contains("Color=\"#1E293B\"", appXaml);

            string editorXaml = File.ReadAllText(GetSourceFilePath("EditorWindow.axaml"));
            Assert.Contains("Foreground=\"{DynamicResource ToolbarIconBrush}\"", editorXaml);
            Assert.Contains("Foreground=\"#D97706\"", editorXaml);

            Assert.Contains("Background=\"{DynamicResource CanvasBackdropBrush}\"", editorXaml);

            Assert.Contains("Stroke=\"{DynamicResource SukiBorderBrush}\"", editorXaml);

            string videoEditorXaml = File.ReadAllText(GetSourceFilePath("VideoEditorWindow.axaml"));

            Assert.Contains("Foreground=\"{DynamicResource SukiTextLow}\"", videoEditorXaml);

            Assert.DoesNotContain("Foreground=\"#88FFFFFF\"", videoEditorXaml);

        }

        [Fact]
        public void UpdateWindow_VersionBadges_DoNotContainPrefixV_And_ChangelogHasThemeNotes()
        {
            string axaml = File.ReadAllText(GetSourceFilePath("UpdateWindow.axaml"));
            Assert.Contains("Text=\"2.4.0\"", axaml);
            Assert.Contains("Text=\"2.4.5\"", axaml);
            Assert.DoesNotContain("Text=\"v2.4.0\"", axaml);
            Assert.DoesNotContain("Text=\"v2.4.5\"", axaml);

            string code = File.ReadAllText(GetSourceFilePath("UpdateWindow.axaml.cs"));

            Assert.Contains("TrimStart('v', 'V')", code);

            Assert.Contains("cleanVersion", code);

            Assert.Contains("Світла тема", code);

            Assert.Contains("Live Preview", code);

            Assert.Contains("SetCheckingState", code);
            Assert.Contains("SetErrorState", code);
            Assert.Contains("StartCheckAsync", code);
            Assert.Contains("TimeSpan.FromSeconds(30)", code); // таймаут перевірки оновлень збільшено з 8 до 30 с (504 від GitHub)

            string deployCode = File.ReadAllText(GetSourceFilePath("Deploy.ps1"));

            Assert.Contains("publish_notes.py", deployCode);

            Assert.Contains("Update Release Notes on GitHub", deployCode);

            string publishPy = File.ReadAllText(GetSourceFilePath("publish_notes.py"));

            Assert.Contains("ReleaseNotes.md", publishPy);

        }

        [Fact]
        public void StampDesign_AutoContrastAndPillShape_Implemented()
        {
            string editorCode = File.ReadAllText(GetSourceFilePath("EditorWindow.axaml.cs"));
            Assert.Contains("number >= 10 ? 34 : 28", editorCode);
            Assert.Contains("0.2126 * baseColor.R + 0.7152 * baseColor.G + 0.0722 * baseColor.B", editorCode);
            Assert.Contains("#0F172A", editorCode);
            Assert.Contains("CornerRadius(radius)", editorCode);
            Assert.Contains("BoxShadow", editorCode);
        }

        [Fact]
        public void VideoEditor_CutoutModeAndFilterComplex_Implemented()
        {
            string axaml = File.ReadAllText(GetSourceFilePath("VideoEditorWindow.axaml"));
            Assert.Contains("BtnTrimModeKeep", axaml);
            Assert.Contains("BtnTrimModeCut", axaml);
            Assert.Contains("TxtTrimHeader", axaml);
            Assert.Contains("TrimMaskLeft", axaml);
            Assert.Contains("TrimMaskActive", axaml);
            Assert.Contains("TrimMaskRight", axaml);

            string cs = File.ReadAllText(GetSourceFilePath("VideoEditorWindow.axaml.cs"));
            Assert.Contains("_isCutoutMode", cs);
            Assert.Contains("SetTrimMode", cs);
            Assert.Contains("BtnTrimModeKeep_Click", cs);
            Assert.Contains("BtnTrimModeCut_Click", cs);
            Assert.Contains("filter_complex", cs);
            Assert.Contains("concat=n=2:v=1", cs);
            Assert.Contains("Audio:", cs);
        }

        [Fact]
        public void AppSettings_PublicUploadWarning_DefaultsToFalse_And_SerializesCorrectly()
        {
            Assert.False(new AppSettings().PublicUploadWarningAccepted);

            var settings = new AppSettings { PublicUploadWarningAccepted = true };
            var deserialized = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings));

            Assert.NotNull(deserialized);
            Assert.True(deserialized.PublicUploadWarningAccepted);

            // Старий settings.json без цього поля має читатися з безпечним значенням за замовчуванням
            var legacy = JsonSerializer.Deserialize<AppSettings>("{\"Theme\":\"Light\"}");
            Assert.NotNull(legacy);
            Assert.False(legacy.PublicUploadWarningAccepted);
        }

        [Fact]
        public void AppLog_WritesMessagesAndExceptionsToLogFile()
        {
            string marker = "test-marker-" + Guid.NewGuid();
            AppLog.Write(marker);
            AppLog.Warn(new InvalidOperationException(marker + "-warn"));

            Assert.True(File.Exists(AppLog.LogFilePath), $"Log file should exist at {AppLog.LogFilePath}");
            string content;
            using (var fs = new FileStream(AppLog.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(fs))
                content = reader.ReadToEnd();

            Assert.Contains(marker, content);
            Assert.Contains(marker + "-warn", content);
            Assert.Contains("[WARN]", content);
        }

        [Fact]
        public void HistoryManager_GeneratesMillisecondPrecisionIds()
        {
            var method = typeof(HistoryManager).GetMethod("GenerateUniqueId",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.NotNull(method);

            string id = (string)method.Invoke(null, null)!;
            // yyyyMMdd_HHmmss_fff — сумісний префікс зі старими ID, але з мілісекундами
            Assert.Matches(@"^\d{8}_\d{6}_\d{3}(-\d+)?$", id);
        }
    }
}
