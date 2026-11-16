using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Діагностика «мерехтіння» кнопок (лише для CI, SO_SELFTEST_FLICKER=1): курсор ставиться на кожну кнопку вікна й
    /// стоїть нерухомо; рахуємо входи/виходи вказівника, відкриття/закриття підказки та (де знімок екрана доступний)
    /// зміни пікселів між кадрами. Нерухомий курсор не повинен давати жодних змін — інакше це і є мерехтіння.
    /// </summary>
    internal static class FlickerProbe
    {
        [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);

        private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        [DllImport(CG)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int type, CGPoint pos, int button);
        [DllImport(CG)] private static extern void CGEventPost(int tap, IntPtr ev);
        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(IntPtr obj);
        [StructLayout(LayoutKind.Sequential)] private struct CGPoint { public double X, Y; }

        /// <summary>Рухає справжній системний курсор (з подією руху миші).</summary>
        public static void MoveCursor(PixelPoint p, double scaling)
        {
            if (OperatingSystem.IsWindows())
            {
                SetCursorPos(p.X, p.Y);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Координати CoreGraphics — точки від лівого верхнього кута головного екрана
                var pt = new CGPoint { X = p.X / scaling, Y = p.Y / scaling };
                IntPtr ev = CGEventCreateMouseEvent(IntPtr.Zero, 5 /* kCGEventMouseMoved */, pt, 0);
                if (ev != IntPtr.Zero) { CGEventPost(0 /* kCGHIDEventTap */, ev); CFRelease(ev); }
            }
        }

        private sealed class Frame
        {
            public byte[] Pixels = Array.Empty<byte>();
            public int Width, Height, Stride;
            public Bitmap? Bitmap;
        }

        private static Frame? Grab(PixelRect rect, double scaling)
        {
            var bmp = ScreenCaptureService.Instance.CaptureRect(rect, scaling);
            if (bmp == null) return null;
            var size = bmp.PixelSize;
            int stride = size.Width * 4;
            var buf = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(buf, GCHandleType.Pinned);
            try { bmp.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buf.Length, stride); }
            finally { handle.Free(); }
            return new Frame { Pixels = buf, Width = size.Width, Height = size.Height, Stride = stride, Bitmap = bmp };
        }

        /// <summary>Прямокутник відмінностей двох кадрів (null — кадри однакові) і кількість змінених пікселів.</summary>
        private static (PixelRect? Rect, int Count) Diff(Frame a, Frame b)
        {
            if (a.Width != b.Width || a.Height != b.Height) return (new PixelRect(0, 0, a.Width, a.Height), a.Width * a.Height);
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1, count = 0;
            for (int y = 0; y < a.Height; y++)
            {
                int row = y * a.Stride;
                for (int x = 0; x < a.Width; x++)
                {
                    int i = row + x * 4;
                    // Невеликий допуск — щоб не ловити шум стиснення/згладжування
                    if (Math.Abs(a.Pixels[i] - b.Pixels[i]) + Math.Abs(a.Pixels[i + 1] - b.Pixels[i + 1]) + Math.Abs(a.Pixels[i + 2] - b.Pixels[i + 2]) > 24)
                    {
                        count++;
                        if (x < minX) minX = x; if (x > maxX) maxX = x;
                        if (y < minY) minY = y; if (y > maxY) maxY = y;
                    }
                }
            }
            return maxX < 0 ? (null, 0) : (new PixelRect(minX, minY, maxX - minX + 1, maxY - minY + 1), count);
        }

        private static string Describe(Control c)
        {
            if (!string.IsNullOrEmpty(c.Name)) return c.Name!;
            var tip = ToolTip.GetTip(c)?.ToString();
            return "кнопка" + (string.IsNullOrEmpty(tip) ? "" : $"«{tip}»");
        }

        /// <summary>Проганяє всі видимі кнопки вікна. Повертає кількість кнопок, де помічено мерехтіння.</summary>
        public static async Task<int> RunAsync(Window w, string label, string? shotsDir)
        {
            var (buttons, winRect, scaling) = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var list = w.GetVisualDescendants().OfType<Button>()
                    .Where(b => b.IsEffectivelyVisible && b.IsEffectivelyEnabled && b.Bounds.Width > 4 && b.Bounds.Height > 4)
                    .Take(30).ToList();
                var tl = w.PointToScreen(new Point(0, 0));
                var br = w.PointToScreen(new Point(w.Bounds.Width, w.Bounds.Height));
                double s = w.RenderScaling > 0 ? w.RenderScaling : 1.0;
                return (list, new PixelRect(tl.X, tl.Y, br.X - tl.X, br.Y - tl.Y), s);
            });

            if (shotsDir != null) Directory.CreateDirectory(shotsDir);
            AppLog.Write($"[Flicker] {label}: кнопок {buttons.Count}, вікно {winRect}");

            // Базова лінія: курсор поза кнопками (у заголовку вікна) — чи змінюється щось само по собі
            MoveCursor(new PixelPoint(winRect.X + 8, winRect.Y + 4), scaling);
            await Task.Delay(800);
            await ProbeAsync(null, $"{label} / без наведення", winRect, scaling, shotsDir, saveIndex: 0);

            int flickering = 0, index = 1;
            foreach (var b in buttons)
            {
                var center = await Dispatcher.UIThread.InvokeAsync(() =>
                    w.PointToScreen(b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), w) ?? default));
                // Спершу трохи поруч, потім точно в центр — щоб система надіслала подію руху
                MoveCursor(new PixelPoint(center.X + 2, center.Y + 2), scaling);
                await Task.Delay(60);
                MoveCursor(center, scaling);
                if (await ProbeAsync(b, $"{label} / {Describe(b)}", winRect, scaling, shotsDir, index++)) flickering++;
            }
            return flickering;
        }

        private static async Task<bool> ProbeAsync(Button? b, string name, PixelRect winRect, double scaling, string? shotsDir, int saveIndex)
        {
            int enters = 0, exits = 0, tipChanges = 0, tipOpenSamples = 0, samples = 0;
            bool? lastTip = null;
            EventHandler<PointerEventArgs>? onEnter = null, onExit = null;
            if (b != null)
            {
                onEnter = (_, _) => enters++;
                onExit = (_, _) => exits++;
                await Dispatcher.UIThread.InvokeAsync(() => { b.PointerEntered += onEnter; b.PointerExited += onExit; });
            }

            var frames = new List<Frame>();
            var start = DateTime.UtcNow;
            // ~2 с нерухомого курсора: підказка встигає з'явитись (ShowDelay 400 мс) і, якщо щось не так, — зникнути/з'явитись знову
            while ((DateTime.UtcNow - start).TotalMilliseconds < 2000)
            {
                if (b != null)
                {
                    bool open = await Dispatcher.UIThread.InvokeAsync(() => ToolTip.GetIsOpen(b));
                    samples++;
                    if (open) tipOpenSamples++;
                    if (lastTip != null && lastTip != open) tipChanges++;
                    lastTip = open;
                }
                if ((DateTime.UtcNow - start).TotalMilliseconds > 700 && frames.Count < 10)
                {
                    var f = Grab(winRect, scaling);
                    if (f != null) frames.Add(f);
                }
                await Task.Delay(30);
            }

            if (b != null)
                await Dispatcher.UIThread.InvokeAsync(() => { b.PointerEntered -= onEnter; b.PointerExited -= onExit; });

            // Відмінності між сусідніми кадрами (курсор нерухомий — їх не має бути)
            int changed = 0, maxPixels = 0;
            PixelRect? union = null;
            int firstChanged = -1;
            for (int i = 1; i < frames.Count; i++)
            {
                var (r, n) = Diff(frames[i - 1], frames[i]);
                if (r is not PixelRect rect) continue;
                changed++;
                maxPixels = Math.Max(maxPixels, n);
                union = union is PixelRect u ? u.Union(rect) : rect;
                if (firstChanged < 0) firstChanged = i;
            }

            // Після першого входу курсор не повинен «виходити»; підказка — один раз відкритись і стояти
            bool pointerFlicker = b != null && (exits > 0 || enters > 1);
            bool tipFlicker = tipChanges > 1;
            bool pixelFlicker = changed > 0;
            bool flicker = pointerFlicker || tipFlicker || pixelFlicker;

            AppLog.Write($"[Flicker] {name}: входів {enters}, виходів {exits}, підказка відкрита {tipOpenSamples}/{samples} (змін {tipChanges}), " +
                         $"кадрів {frames.Count}, змінених {changed}" +
                         (union is PixelRect ur ? $" у {ur.X},{ur.Y} {ur.Width}×{ur.Height} (до {maxPixels} пікс.)" : "") +
                         $" — {(flicker ? "МЕРЕХТИТЬ" : "ок")}");

            if (shotsDir != null && frames.Count > 0)
            {
                try
                {
                    // Перший кадр — завжди (видно, де курсор і чи є підказка); при змінах — ще й кадр після зміни
                    string safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
                    if (safe.Length > 60) safe = safe[..60];
                    frames[0].Bitmap?.Save(Path.Combine(shotsDir, $"{saveIndex:00}_{safe}_a.png"));
                    if (firstChanged > 0) frames[firstChanged].Bitmap?.Save(Path.Combine(shotsDir, $"{saveIndex:00}_{safe}_b.png"));
                }
                catch (Exception ex) { AppLog.Warn(ex); }
            }
            foreach (var f in frames) f.Bitmap?.Dispose();
            return flicker;
        }
    }
}
