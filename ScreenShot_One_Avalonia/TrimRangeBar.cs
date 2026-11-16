using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;
using System.Globalization;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Смуга обрізки відео: уся тривалість, дві ручки (початок і кінець виділеного шматка) і позиція відтворення.
    /// «Залишити виділене» — виділене зелене, решта червона (буде видалено);
    /// «Вирізати виділене» — навпаки. Значення — відсотки 0…100, як у повзунків TrimStartSlider/TrimEndSlider.
    /// Клік по смузі поза ручками — перемотка; ручки тягнуться мишею.
    /// </summary>
    public class TrimRangeBar : Control
    {
        public static readonly StyledProperty<double> StartProperty = AvaloniaProperty.Register<TrimRangeBar, double>(nameof(Start), 0);
        public static readonly StyledProperty<double> EndProperty = AvaloniaProperty.Register<TrimRangeBar, double>(nameof(End), 100);
        public static readonly StyledProperty<double> PositionProperty = AvaloniaProperty.Register<TrimRangeBar, double>(nameof(Position), 0);
        public static readonly StyledProperty<bool> IsCutModeProperty = AvaloniaProperty.Register<TrimRangeBar, bool>(nameof(IsCutMode));
        public static readonly StyledProperty<long> DurationMsProperty = AvaloniaProperty.Register<TrimRangeBar, long>(nameof(DurationMs));

        public double Start { get => GetValue(StartProperty); set => SetValue(StartProperty, value); }
        public double End { get => GetValue(EndProperty); set => SetValue(EndProperty, value); }
        public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
        public bool IsCutMode { get => GetValue(IsCutModeProperty); set => SetValue(IsCutModeProperty, value); }
        public long DurationMs { get => GetValue(DurationMsProperty); set => SetValue(DurationMsProperty, value); }

        /// <summary>Користувач перетягнув ручку: (початок, кінець, true — рухали початок).</summary>
        public event Action<double, double, bool>? RangeChanged;
        /// <summary>Клік/перетягування по смузі поза ручками — перемотати на відсоток.</summary>
        public event Action<double>? SeekRequested;

        private enum Drag { None, Start, End, Seek }
        private Drag _drag;

        private const double TrackTop = 18, TrackHeight = 34, HandleWidth = 10, HitSlop = 12, Pad = 8;

        static TrimRangeBar()
        {
            AffectsRender<TrimRangeBar>(StartProperty, EndProperty, PositionProperty, IsCutModeProperty, DurationMsProperty);
        }

        public TrimRangeBar()
        {
            Height = TrackTop + TrackHeight + 4;
            ClipToBounds = false;
        }

        private double TrackLeft => Pad;
        private double TrackWidth => Math.Max(1, Bounds.Width - 2 * Pad);
        private double X(double percent) => TrackLeft + Math.Clamp(percent, 0, 100) / 100.0 * TrackWidth;
        private double Percent(double x) => Math.Clamp((x - TrackLeft) / TrackWidth * 100.0, 0, 100);

        private string Time(double percent)
        {
            var ts = TimeSpan.FromMilliseconds(Math.Max(0, percent / 100.0 * DurationMs));
            return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}.{ts.Milliseconds / 10:D2}";
        }

        public override void Render(DrawingContext ctx)
        {
            var keep = AppTheme.Brush(this, "AccentBrush", new SolidColorBrush(Color.Parse("#3AA882")));
            var cut = AppTheme.Brush(this, "DangerBrush", new SolidColorBrush(Color.Parse("#D9614C")));
            var track = AppTheme.Brush(this, "PanelBorderBrush", Brushes.Gray);
            var text = AppTheme.Brush(this, "ToolbarIconBrush", Brushes.White);
            var onAccent = AppTheme.Brush(this, "OnAccentBrush", Brushes.White);

            double xs = X(Math.Min(Start, End)), xe = X(Math.Max(Start, End));
            var full = new Rect(TrackLeft, TrackTop, TrackWidth, TrackHeight);
            ctx.DrawRectangle(track, null, full, 8, 8);

            // Що залишиться — суцільне, що видаляється — напівпрозоре червоне
            IBrush inside = IsCutMode ? cut : keep, outside = IsCutMode ? keep : cut;
            using (ctx.PushClip(new RoundedRect(full, 8)))
            {
                using (ctx.PushOpacity(IsCutMode ? 1.0 : 0.35))
                {
                    ctx.DrawRectangle(outside, null, new Rect(TrackLeft, TrackTop, xs - TrackLeft, TrackHeight));
                    ctx.DrawRectangle(outside, null, new Rect(xe, TrackTop, TrackLeft + TrackWidth - xe, TrackHeight));
                }
                using (ctx.PushOpacity(IsCutMode ? 0.9 : 1.0))
                    ctx.DrawRectangle(inside, null, new Rect(xs, TrackTop, xe - xs, TrackHeight));
            }

            // Підписи всередині частин (якщо влазять)
            var face = new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
            void Label(string s, double from, double to, IBrush brush)
            {
                var ft = new FormattedText(s, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, 11, brush);
                if (to - from < ft.Width + 12) return;
                ctx.DrawText(ft, new Point((from + to - ft.Width) / 2, TrackTop + (TrackHeight - ft.Height) / 2));
            }
            string remains = Loc.T("VEditor_WillRemain"), removed = Loc.T("VEditor_WillCut");
            if (IsCutMode)
            {
                Label(removed, xs, xe, onAccent);
                Label(remains, TrackLeft, xs, onAccent);
                Label(remains, xe, TrackLeft + TrackWidth, onAccent);
            }
            else
            {
                Label(remains, xs, xe, onAccent);
                Label(removed, TrackLeft, xs, text);
                Label(removed, xe, TrackLeft + TrackWidth, text);
            }

            // Ручки з часом над ними
            var handleBrush = Brushes.White;
            var handlePen = new Pen(new SolidColorBrush(Color.FromArgb(90, 0, 0, 0)), 1);
            foreach (var (x, isStart) in new[] { (xs, true), (xe, false) })
            {
                var r = new Rect(x - HandleWidth / 2, TrackTop - 3, HandleWidth, TrackHeight + 6);
                ctx.DrawRectangle(handleBrush, handlePen, r, 4, 4);
                var grip = new Pen(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), 1);
                ctx.DrawLine(grip, new Point(x - 1.5, TrackTop + 10), new Point(x - 1.5, TrackTop + TrackHeight - 10));
                ctx.DrawLine(grip, new Point(x + 1.5, TrackTop + 10), new Point(x + 1.5, TrackTop + TrackHeight - 10));

                var ft = new FormattedText(Time(isStart ? Math.Min(Start, End) : Math.Max(Start, End)), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, face, 11, text);
                double tx = Math.Clamp(x - ft.Width / 2, 0, Math.Max(0, Bounds.Width - ft.Width));
                // Мітки не накладаються: якщо ручки близько — початок лівіше, кінець правіше
                if (xe - xs < ft.Width + 8) tx = isStart ? Math.Max(0, x - ft.Width - 4) : Math.Min(Bounds.Width - ft.Width, x + 4);
                ctx.DrawText(ft, new Point(tx, 0));
            }

            // Позиція відтворення
            double xp = X(Position);
            ctx.DrawRectangle(text, null, new Rect(xp - 1, TrackTop - 2, 2, TrackHeight + 4));
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            double x = e.GetPosition(this).X;
            double xs = X(Start), xe = X(End);
            double ds = Math.Abs(x - xs), de = Math.Abs(x - xe);
            if (ds <= HitSlop || de <= HitSlop)
                _drag = ds < de || (ds == de && x < xs) ? Drag.Start : Drag.End;
            else
            {
                _drag = Drag.Seek;
                SeekRequested?.Invoke(Percent(x));
            }
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            double x = e.GetPosition(this).X;
            if (_drag == Drag.None)
            {
                bool nearHandle = Math.Abs(x - X(Start)) <= HitSlop || Math.Abs(x - X(End)) <= HitSlop;
                Cursor = new Cursor(nearHandle ? StandardCursorType.SizeWestEast : StandardCursorType.Hand);
                return;
            }
            double p = Percent(x);
            switch (_drag)
            {
                case Drag.Start:
                    RangeChanged?.Invoke(Math.Min(p, End), End, true);
                    break;
                case Drag.End:
                    RangeChanged?.Invoke(Start, Math.Max(p, Start), false);
                    break;
                case Drag.Seek:
                    SeekRequested?.Invoke(p);
                    break;
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _drag = Drag.None;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _drag = Drag.None;
        }
    }
}
