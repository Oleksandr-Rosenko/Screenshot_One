using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace ScreenShot_One_Avalonia
{
    /// <summary>Одне натискання для показу: текст і чи це «звичайний друк» (його склеюємо в одне слово).</summary>
    public readonly record struct KeyStroke(string Text, bool IsTyping);

    /// <summary>Перетворює код клавіші + модифікатори на підпис («Ctrl + C», «⌘⇧S», «Enter»). Публічний для тестів.</summary>
    public static class KeystrokeFormatter
    {
        private static readonly Dictionary<int, string> WinSpecial = new()
        {
            [0x08] = "⌫", [0x09] = "Tab", [0x0D] = "Enter", [0x1B] = "Esc", [0x20] = "Space",
            [0x21] = "PgUp", [0x22] = "PgDn", [0x23] = "End", [0x24] = "Home",
            [0x25] = "←", [0x26] = "↑", [0x27] = "→", [0x28] = "↓", [0x2C] = "PrtSc", [0x2D] = "Ins", [0x2E] = "Del",
        };
        private static readonly Dictionary<int, char> WinOem = new()
        {
            [0xBA] = ';', [0xBB] = '=', [0xBC] = ',', [0xBD] = '-', [0xBE] = '.', [0xBF] = '/', [0xC0] = '`',
            [0xDB] = '[', [0xDC] = '\\', [0xDD] = ']', [0xDE] = '\'',
        };
        private static readonly HashSet<int> WinModifierKeys = new() { 0x10, 0x11, 0x12, 0x14, 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 };

        // macOS: віртуальні коди клавіш (ANSI-розкладка)
        private static readonly Dictionary<int, string> MacKeys = new()
        {
            [0] = "A", [1] = "S", [2] = "D", [3] = "F", [4] = "H", [5] = "G", [6] = "Z", [7] = "X", [8] = "C", [9] = "V",
            [11] = "B", [12] = "Q", [13] = "W", [14] = "E", [15] = "R", [16] = "Y", [17] = "T", [18] = "1", [19] = "2",
            [20] = "3", [21] = "4", [22] = "6", [23] = "5", [24] = "=", [25] = "9", [26] = "7", [27] = "-", [28] = "8",
            [29] = "0", [30] = "]", [31] = "O", [32] = "U", [33] = "[", [34] = "I", [35] = "P", [37] = "L", [38] = "J",
            [39] = "'", [40] = "K", [41] = ";", [42] = "\\", [43] = ",", [44] = "/", [45] = "N", [46] = "M", [47] = ".",
            [50] = "`",
        };
        private static readonly Dictionary<int, string> MacSpecial = new()
        {
            [36] = "Return", [48] = "Tab", [49] = "Space", [51] = "⌫", [53] = "Esc", [117] = "Del",
            [115] = "Home", [119] = "End", [116] = "PgUp", [121] = "PgDn",
            [123] = "←", [124] = "→", [125] = "↓", [126] = "↑",
            [122] = "F1", [120] = "F2", [99] = "F3", [118] = "F4", [96] = "F5", [97] = "F6", [98] = "F7",
            [100] = "F8", [101] = "F9", [109] = "F10", [103] = "F11", [111] = "F12",
        };

        /// <summary>Windows: VK-код + модифікатори (HotkeyModifiers). Null — сама клавіша-модифікатор, нічого не показуємо.</summary>
        public static KeyStroke? FromWindows(int vk, int modifiers)
        {
            if (WinModifierKeys.Contains(vk)) return null;
            string? key = null; char? ch = null;
            if (vk >= 0x41 && vk <= 0x5A) ch = (char)vk;
            else if (vk >= 0x30 && vk <= 0x39) ch = (char)vk;
            else if (vk >= 0x60 && vk <= 0x69) ch = (char)('0' + vk - 0x60);
            else if (WinOem.TryGetValue(vk, out var o)) ch = o;
            else if (vk >= 0x70 && vk <= 0x87) key = $"F{vk - 0x70 + 1}";
            else if (WinSpecial.TryGetValue(vk, out var sp)) key = sp;
            else return null;
            return Build(key ?? ch.ToString()!, ch, modifiers, mac: false);
        }

        /// <summary>macOS: код клавіші CGEvent + модифікатори (HotkeyModifiers).</summary>
        public static KeyStroke? FromMac(int keyCode, int modifiers)
        {
            if (MacKeys.TryGetValue(keyCode, out var k)) return Build(k, k.Length == 1 ? k[0] : null, modifiers, mac: true);
            if (MacSpecial.TryGetValue(keyCode, out var sp)) return Build(sp, null, modifiers, mac: true);
            return null;
        }

        private static KeyStroke Build(string key, char? ch, int modifiers, bool mac)
        {
            bool ctrl = (modifiers & HotkeyModifiers.Ctrl) != 0, alt = (modifiers & HotkeyModifiers.Alt) != 0;
            bool shift = (modifiers & HotkeyModifiers.Shift) != 0, meta = (modifiers & HotkeyModifiers.Meta) != 0;

            // Звичайний друк (буква/цифра/знак, хіба що з Shift) — склеюємо в слово
            if (ch.HasValue && !ctrl && !alt && !meta)
                return new KeyStroke(shift ? ch.Value.ToString().ToUpperInvariant() : ch.Value.ToString().ToLowerInvariant(), true);

            if (mac)
            {
                string prefix = (ctrl ? "⌃" : "") + (alt ? "⌥" : "") + (shift ? "⇧" : "") + (meta ? "⌘" : "");
                return new KeyStroke(prefix + key, false);
            }
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl");
            if (alt) parts.Add("Alt");
            if (shift) parts.Add("Shift");
            if (meta) parts.Add("Win");
            parts.Add(key);
            return new KeyStroke(string.Join(" + ", parts), false);
        }
    }

    /// <summary>
    /// Накладка з натиснутими клавішами внизу області запису. Вікно видиме для запису (його ловить відео),
    /// не забирає фокус і не перехоплює мишу. Windows — через hook HotkeyService; macOS — CGEventTap
    /// (потрібен дозвіл «Моніторинг введення»).
    /// </summary>
    public static class KeystrokeOverlay
    {
        private static KeystrokeOverlayWindow? _window;

        /// <summary>Останній показаний підпис (для самоперевірки в CI).</summary>
        public static string? LastShown { get; private set; }

        public static void Start(PixelRect area)
        {
            Stop();
            _window = new KeystrokeOverlayWindow(area);
            HotkeyService.KeyObserved += OnWindowsKey;
            if (OperatingSystem.IsWindows()) HotkeyService.ObserveKeys = true;
            if (OperatingSystem.IsMacOS() && !MacKeyTap.Start(OnMacKey))
            {
                ToastWindow.ShowMessage(Loc.T("Video_KeysPermission"), 7000);
            }
            AppLog.Write("[Keys] показ клавіш увімкнено");
        }

        public static void Stop()
        {
            HotkeyService.ObserveKeys = false;
            HotkeyService.KeyObserved -= OnWindowsKey;
            if (OperatingSystem.IsMacOS()) MacKeyTap.Stop();
            _window?.Close();
            _window = null;
        }

        private static void OnWindowsKey(int vk, int modifiers) => Show(KeystrokeFormatter.FromWindows(vk, modifiers));
        private static void OnMacKey(int keyCode, int modifiers) => Show(KeystrokeFormatter.FromMac(keyCode, modifiers));

        private static void Show(KeyStroke? stroke)
        {
            if (stroke == null) return;
            Dispatcher.UIThread.Post(() => { _window?.Add(stroke.Value); LastShown = stroke.Value.Text; });
        }
    }

    internal sealed class KeystrokeOverlayWindow : Window
    {
        private const int MaxChips = 4;
        private readonly PixelRect _area;
        private readonly StackPanel _row = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
        private readonly DispatcherTimer _clear;
        private TextBlock? _typing;

        public KeystrokeOverlayWindow(PixelRect area)
        {
            _area = area;
            WindowDecorations = WindowDecorations.None;
            Background = Brushes.Transparent;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false;
            CanResize = false;
            IsHitTestVisible = false;
            SizeToContent = SizeToContent.WidthAndHeight;
            Content = new Border { Padding = new Thickness(4), Child = _row };
            Opacity = 0;

            _clear = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _clear.Tick += (_, _) => { _clear.Stop(); _row.Children.Clear(); _typing = null; Opacity = 0; };
            SizeChanged += (_, _) => Place();
            Show();
            if (OperatingSystem.IsWindows()) MakeClickThrough();
        }

        public void Add(KeyStroke stroke)
        {
            if (stroke.IsTyping && _typing != null)
            {
                _typing.Text += stroke.Text;
                if (_typing.Text!.Length > 24) _typing.Text = "…" + _typing.Text[^23..];
            }
            else
            {
                var text = new TextBlock
                {
                    Text = stroke.Text, FontSize = 22, FontWeight = FontWeight.SemiBold,
                    Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
                };
                _row.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#E6111816")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#5962C9A0")),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                    Padding = new Thickness(16, 8), Child = text
                });
                _typing = stroke.IsTyping ? text : null;
                while (_row.Children.Count > MaxChips) _row.Children.RemoveAt(0);
            }
            Opacity = 1;
            _clear.Stop();
            _clear.Start();
        }

        /// <summary>Унизу по центру області запису — так клавіші потрапляють у кадр.</summary>
        private void Place()
        {
            double scale = RenderScaling > 0 ? RenderScaling : 1.0;
            int w = (int)(Bounds.Width * scale), h = (int)(Bounds.Height * scale);
            Position = new PixelPoint(_area.X + (_area.Width - w) / 2, _area.Bottom - h - (int)(48 * scale));
        }

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

        [SupportedOSPlatform("windows")]
        private void MakeClickThrough()
        {
            try
            {
                var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return;
                const int GWL_EXSTYLE = -20;
                const long WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x80;
                long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }
    }

    /// <summary>macOS: глобальне спостереження за клавіатурою через CGEventTap (лише читання).</summary>
    [SupportedOSPlatform("macos")]
    internal static unsafe class MacKeyTap
    {
        private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

        [DllImport(CG)] private static extern IntPtr CGEventTapCreate(int tap, int place, int options, ulong eventsOfInterest, IntPtr callback, IntPtr userInfo);
        [DllImport(CG)] private static extern void CGEventTapEnable(IntPtr tap, [MarshalAs(UnmanagedType.I1)] bool enable);
        [DllImport(CG)] private static extern long CGEventGetIntegerValueField(IntPtr ev, int field);
        [DllImport(CG)] private static extern ulong CGEventGetFlags(IntPtr ev);
        [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestListenEventAccess();
        [DllImport(CF)] private static extern IntPtr CFMachPortCreateRunLoopSource(IntPtr allocator, IntPtr port, long order);
        [DllImport(CF)] private static extern IntPtr CFRunLoopGetMain();
        [DllImport(CF)] private static extern void CFRunLoopAddSource(IntPtr rl, IntPtr source, IntPtr mode);
        [DllImport(CF)] private static extern void CFRunLoopRemoveSource(IntPtr rl, IntPtr source, IntPtr mode);
        [DllImport(CF)] private static extern void CFMachPortInvalidate(IntPtr port);
        [DllImport(CF)] private static extern void CFRelease(IntPtr obj);

        private const int kCGSessionEventTap = 1, kCGHeadInsertEventTap = 0, kCGEventTapOptionListenOnly = 1;
        private const int kCGEventKeyDown = 10, kCGEventTapDisabledByTimeout = -2;
        private const int kCGKeyboardEventKeycode = 9;
        private const ulong FlagShift = 0x20000, FlagControl = 0x40000, FlagAlt = 0x80000, FlagCommand = 0x100000;

        private static IntPtr _tap, _source;
        private static Action<int, int>? _onKey;

        private static IntPtr CommonModes()
        {
            var lib = NativeLibrary.Load(CF);
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, "kCFRunLoopCommonModes"));
        }

        [UnmanagedCallersOnly]
        private static IntPtr OnEvent(IntPtr proxy, int type, IntPtr ev, IntPtr userInfo)
        {
            try
            {
                if (type == kCGEventTapDisabledByTimeout && _tap != IntPtr.Zero) { CGEventTapEnable(_tap, true); return ev; }
                if (type != kCGEventKeyDown) return ev;
                int keyCode = (int)CGEventGetIntegerValueField(ev, kCGKeyboardEventKeycode);
                ulong f = CGEventGetFlags(ev);
                int mods = 0;
                if ((f & FlagShift) != 0) mods |= HotkeyModifiers.Shift;
                if ((f & FlagControl) != 0) mods |= HotkeyModifiers.Ctrl;
                if ((f & FlagAlt) != 0) mods |= HotkeyModifiers.Alt;
                if ((f & FlagCommand) != 0) mods |= HotkeyModifiers.Meta;
                _onKey?.Invoke(keyCode, mods);
            }
            catch (Exception ex) { AppLog.Warn(ex); }
            return ev;
        }

        /// <summary>False — немає дозволу «Моніторинг введення» (система покаже запит).</summary>
        public static bool Start(Action<int, int> onKey)
        {
            Stop();
            _onKey = onKey;
            try
            {
                delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, IntPtr> cb = &OnEvent;
                _tap = CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap, kCGEventTapOptionListenOnly,
                    1UL << kCGEventKeyDown, (IntPtr)cb, IntPtr.Zero);
                if (_tap == IntPtr.Zero)
                {
                    AppLog.Write("[Keys] CGEventTap недоступний — просимо дозвіл «Моніторинг введення»");
                    CGRequestListenEventAccess();
                    return false;
                }
                _source = CFMachPortCreateRunLoopSource(IntPtr.Zero, _tap, 0);
                CFRunLoopAddSource(CFRunLoopGetMain(), _source, CommonModes());
                CGEventTapEnable(_tap, true);
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Mac: показ клавіш");
                return false;
            }
        }

        public static void Stop()
        {
            try
            {
                if (_source != IntPtr.Zero) { CFRunLoopRemoveSource(CFRunLoopGetMain(), _source, CommonModes()); CFRelease(_source); }
                if (_tap != IntPtr.Zero) { CGEventTapEnable(_tap, false); CFMachPortInvalidate(_tap); CFRelease(_tap); }
            }
            catch (Exception ex) { AppLog.Warn(ex); }
            _tap = _source = IntPtr.Zero;
            _onKey = null;
        }
    }
}
