using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;

namespace ScreenShot_One_Avalonia
{
    public enum HotkeyAction
    {
        Region = 1,
        FullScreen = 2,
        Video = 3,
        AllScreens = 4,
        Ocr = 5
    }

    /// <summary>
    /// Біти модифікаторів, як вони зберігаються в settings.json.
    /// </summary>
    public static class HotkeyModifiers
    {
        public const int Alt = 1;   // Option на Mac
        public const int Ctrl = 2;
        public const int Shift = 4;
        public const int Meta = 8;  // Win на Windows, Cmd на Mac
    }

    /// <summary>
    /// Глобальні гарячі клавіші.
    /// Windows: low-level hook клавіатури в окремому потоці з власним циклом повідомлень.
    ///   Hook (а не RegisterHotKey) лишається, бо на Windows 11 клавішу PrintScreen часто займає «Ножиці»,
    ///   і RegisterHotKey для неї просто не реєструється. Окремий потік потрібен, щоб Windows не знімала hook,
    ///   коли UI-потік зайнятий (раніше через це гарячі клавіші «раптом» переставали працювати).
    /// macOS: системна реєстрація через Carbon RegisterEventHotKey (не потребує дозволу «Універсальний доступ»).
    /// </summary>
    public static class HotkeyService
    {
        private static Action<HotkeyAction>? _onPressed;
        private static bool _started;

        public static void Start(Action<HotkeyAction> onPressed)
        {
            _onPressed = onPressed;
            if (_started) return;

            if (OperatingSystem.IsWindows())
            {
                WindowsHook.Start();
                _started = true;
            }
            else if (OperatingSystem.IsMacOS())
            {
                MacCarbonHotkeys.Start();
                _started = true;
            }
        }

        public static void Stop()
        {
            if (!_started) return;
            _started = false;

            if (OperatingSystem.IsWindows()) WindowsHook.Stop();
            else if (OperatingSystem.IsMacOS()) MacCarbonHotkeys.Stop();
        }

        /// <summary>
        /// Тимчасово вимикає гарячі клавіші (наприклад, поки відкрите вікно налаштувань і користувач вводить нову комбінацію).
        /// Після відновлення комбінації перечитуються з SettingsManager.Current.
        /// </summary>
        public static void SetSuspended(bool suspended)
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsHook.Suspended = suspended;
            }
            else if (OperatingSystem.IsMacOS() && _started)
            {
                if (suspended) MacCarbonHotkeys.UnregisterAll();
                else MacCarbonHotkeys.RegisterAll();
            }
        }

        private static void Raise(HotkeyAction action)
        {
            try { _onPressed?.Invoke(action); }
            catch (Exception ex) { AppLog.Write($"[Hotkeys] Помилка обробника {action}: {ex.Message}"); }
        }

        private static IEnumerable<(HotkeyAction Action, int Key, int Modifiers)> GetConfiguredHotkeys()
        {
            var s = SettingsManager.Current;
            yield return (HotkeyAction.Region, s.RegionKey, s.RegionModifier);
            yield return (HotkeyAction.FullScreen, s.FullScreenKey, s.FullScreenModifier);
            yield return (HotkeyAction.Video, s.VideoKey, s.VideoModifier);
            yield return (HotkeyAction.AllScreens, s.AllScreensKey, s.AllScreensModifier);
            yield return (HotkeyAction.Ocr, s.OcrKey, s.OcrModifier);
        }

        private static HotkeyAction? Match(int vkCode, int modifiers)
        {
            foreach (var (action, key, mods) in GetConfiguredHotkeys())
            {
                if (key != 0 && key == vkCode && mods == modifiers) return action;
            }
            return null;
        }

        // ───────────────────────────── Windows ─────────────────────────────

        [SupportedOSPlatform("windows")]
        private static class WindowsHook
        {
            private const int WH_KEYBOARD_LL = 13;
            private const int WM_KEYDOWN = 0x0100;
            private const int WM_SYSKEYDOWN = 0x0104;
            private const uint WM_QUIT = 0x0012;
            private const uint PM_NOREMOVE = 0x0000;

            private const int VK_SHIFT = 0x10;
            private const int VK_CONTROL = 0x11;
            private const int VK_MENU = 0x12; // Alt
            private const int VK_LWIN = 0x5B;
            private const int VK_RWIN = 0x5C;

            private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

            // Тримаємо посилання на делегат, щоб GC не зібрав його, поки hook активний.
            private static readonly LowLevelKeyboardProc _proc = HookCallback;

            private static IntPtr _hookId = IntPtr.Zero;
            private static Thread? _thread;
            private static uint _threadId;

            internal static volatile bool Suspended;

            [StructLayout(LayoutKind.Sequential)]
            private struct MSG
            {
                public IntPtr hwnd;
                public uint message;
                public IntPtr wParam;
                public IntPtr lParam;
                public uint time;
                public int ptX;
                public int ptY;
            }

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool UnhookWindowsHookEx(IntPtr hhk);

            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr GetModuleHandle(string? lpModuleName);

            [DllImport("user32.dll")]
            private static extern short GetAsyncKeyState(int vKey);

            [DllImport("user32.dll")]
            private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool TranslateMessage(ref MSG lpMsg);

            [DllImport("user32.dll")]
            private static extern IntPtr DispatchMessage(ref MSG lpMsg);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentThreadId();

            internal static void Start()
            {
                if (_thread != null) return;

                var ready = new ManualResetEventSlim(false);
                _thread = new Thread(() =>
                {
                    _threadId = GetCurrentThreadId();

                    // Створюємо чергу повідомлень потоку до того, як хтось захоче надіслати йому WM_QUIT.
                    PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

                    _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                    if (_hookId == IntPtr.Zero)
                    {
                        AppLog.Write($"[Hotkeys] SetWindowsHookEx не вдався, код {Marshal.GetLastWin32Error()}");
                    }
                    ready.Set();

                    while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }

                    if (_hookId != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_hookId);
                        _hookId = IntPtr.Zero;
                    }
                })
                {
                    IsBackground = true,
                    Name = "Screenshot_One hotkeys"
                };
                _thread.Start();
                ready.Wait(TimeSpan.FromSeconds(2));
            }

            internal static void Stop()
            {
                if (_threadId != 0)
                {
                    PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
                }
                _thread = null;
                _threadId = 0;
            }

            private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

            private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode >= 0 && !Suspended && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
                {
                    try
                    {
                        int vkCode = Marshal.ReadInt32(lParam);

                        int modifiers = 0;
                        if (IsDown(VK_MENU)) modifiers |= HotkeyModifiers.Alt;
                        if (IsDown(VK_CONTROL)) modifiers |= HotkeyModifiers.Ctrl;
                        if (IsDown(VK_SHIFT)) modifiers |= HotkeyModifiers.Shift;
                        if (IsDown(VK_LWIN) || IsDown(VK_RWIN)) modifiers |= HotkeyModifiers.Meta;

                        var action = Match(vkCode, modifiers);
                        if (action != null)
                        {
                            // Обробник лише ставить задачу в UI-потік, тому hook повертається миттєво.
                            Raise(action.Value);
                            return (IntPtr)1; // «з'їдаємо» натискання, щоб воно не пішло далі
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"[Hotkeys] Помилка в hook: {ex.Message}");
                    }
                }

                return CallNextHookEx(_hookId, nCode, wParam, lParam);
            }
        }

        // ───────────────────────────── macOS ─────────────────────────────

        [SupportedOSPlatform("macos")]
        private static class MacCarbonHotkeys
        {
            private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";

            private const uint kEventClassKeyboard = 0x6B657962;      // 'keyb'
            private const uint kEventHotKeyPressed = 5;
            private const uint kEventParamDirectObject = 0x2D2D2D2D;  // '----'
            private const uint typeEventHotKeyID = 0x686B6964;        // 'hkid'
            private const uint HotkeySignature = 0x53534F4E;          // 'SSON'
            private const int eventNotHandledErr = -9874;

            private const uint cmdKey = 0x0100;
            private const uint shiftKey = 0x0200;
            private const uint optionKey = 0x0800;
            private const uint controlKey = 0x1000;

            [StructLayout(LayoutKind.Sequential)]
            private struct EventHotKeyID
            {
                public uint Signature;
                public uint Id;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct EventTypeSpec
            {
                public uint EventClass;
                public uint EventKind;
            }

            [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
            private delegate int EventHandlerProc(IntPtr handlerCallRef, IntPtr eventRef, IntPtr userData);

            [DllImport(Carbon)]
            private static extern IntPtr GetApplicationEventTarget();

            [DllImport(Carbon)]
            private static extern int InstallEventHandler(IntPtr target, EventHandlerProc handler, nuint numTypes,
                EventTypeSpec[] typeList, IntPtr userData, out IntPtr handlerRef);

            [DllImport(Carbon)]
            private static extern int RemoveEventHandler(IntPtr handlerRef);

            [DllImport(Carbon)]
            private static extern int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID hotKeyId,
                IntPtr target, uint options, out IntPtr hotKeyRef);

            [DllImport(Carbon)]
            private static extern int UnregisterEventHotKey(IntPtr hotKeyRef);

            [DllImport(Carbon)]
            private static extern int GetEventParameter(IntPtr eventRef, uint name, uint desiredType, IntPtr actualType,
                nuint bufferSize, IntPtr actualSize, out EventHotKeyID data);

            // Тримаємо посилання на делегат, щоб GC не зібрав його.
            private static readonly EventHandlerProc _handler = OnHotKeyEvent;
            private static IntPtr _handlerRef = IntPtr.Zero;
            private static readonly List<IntPtr> _registered = new();

            internal static void Start()
            {
                try
                {
                    if (_handlerRef == IntPtr.Zero)
                    {
                        var spec = new[] { new EventTypeSpec { EventClass = kEventClassKeyboard, EventKind = kEventHotKeyPressed } };
                        int status = InstallEventHandler(GetApplicationEventTarget(), _handler, (nuint)spec.Length, spec, IntPtr.Zero, out _handlerRef);
                        if (status != 0) AppLog.Write($"[Hotkeys] InstallEventHandler: {status}");
                    }
                    RegisterAll();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[Hotkeys] Не вдалося підключити гарячі клавіші macOS: {ex.Message}");
                }
            }

            internal static void Stop()
            {
                UnregisterAll();
                try
                {
                    if (_handlerRef != IntPtr.Zero)
                    {
                        RemoveEventHandler(_handlerRef);
                        _handlerRef = IntPtr.Zero;
                    }
                }
                catch (Exception logEx) { AppLog.Warn(logEx); }
            }

            internal static void RegisterAll()
            {
                UnregisterAll();
                foreach (var (action, key, mods) in GetConfiguredHotkeys())
                {
                    if (!VkToMacKeyCode.TryGetValue(key, out uint macKey))
                    {
                        AppLog.Write($"[Hotkeys] Клавіша {key} для {action} не підтримується на macOS");
                        continue;
                    }

                    uint carbonMods = 0;
                    if ((mods & HotkeyModifiers.Meta) != 0) carbonMods |= cmdKey;
                    if ((mods & HotkeyModifiers.Shift) != 0) carbonMods |= shiftKey;
                    if ((mods & HotkeyModifiers.Alt) != 0) carbonMods |= optionKey;
                    if ((mods & HotkeyModifiers.Ctrl) != 0) carbonMods |= controlKey;

                    try
                    {
                        var id = new EventHotKeyID { Signature = HotkeySignature, Id = (uint)action };
                        int status = RegisterEventHotKey(macKey, carbonMods, id, GetApplicationEventTarget(), 0, out IntPtr hotKeyRef);
                        if (status == 0 && hotKeyRef != IntPtr.Zero) _registered.Add(hotKeyRef);
                        else AppLog.Write($"[Hotkeys] RegisterEventHotKey {action}: {status}");
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"[Hotkeys] RegisterEventHotKey {action}: {ex.Message}");
                    }
                }
            }

            internal static void UnregisterAll()
            {
                foreach (var hotKeyRef in _registered)
                {
                    try { UnregisterEventHotKey(hotKeyRef); } catch (Exception logEx) { AppLog.Warn(logEx); }
                }
                _registered.Clear();
            }

            private static int OnHotKeyEvent(IntPtr handlerCallRef, IntPtr eventRef, IntPtr userData)
            {
                try
                {
                    int status = GetEventParameter(eventRef, kEventParamDirectObject, typeEventHotKeyID, IntPtr.Zero,
                        (nuint)Marshal.SizeOf<EventHotKeyID>(), IntPtr.Zero, out EventHotKeyID id);

                    if (status == 0 && id.Signature == HotkeySignature && Enum.IsDefined(typeof(HotkeyAction), (int)id.Id))
                    {
                        Raise((HotkeyAction)(int)id.Id);
                        return 0;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"[Hotkeys] Помилка обробки події macOS: {ex.Message}");
                }
                return eventNotHandledErr;
            }

            // Windows virtual-key → macOS virtual keycode (kVK_*, Carbon/HIToolbox Events.h)
            private static readonly Dictionary<int, uint> VkToMacKeyCode = new()
            {
                // Літери A–Z
                [0x41] = 0x00, [0x42] = 0x0B, [0x43] = 0x08, [0x44] = 0x02, [0x45] = 0x0E, [0x46] = 0x03,
                [0x47] = 0x05, [0x48] = 0x04, [0x49] = 0x22, [0x4A] = 0x26, [0x4B] = 0x28, [0x4C] = 0x25,
                [0x4D] = 0x2E, [0x4E] = 0x2D, [0x4F] = 0x1F, [0x50] = 0x23, [0x51] = 0x0C, [0x52] = 0x0F,
                [0x53] = 0x01, [0x54] = 0x11, [0x55] = 0x20, [0x56] = 0x09, [0x57] = 0x0D, [0x58] = 0x07,
                [0x59] = 0x10, [0x5A] = 0x06,

                // Цифри 0–9 (верхній ряд)
                [0x30] = 0x1D, [0x31] = 0x12, [0x32] = 0x13, [0x33] = 0x14, [0x34] = 0x15,
                [0x35] = 0x17, [0x36] = 0x16, [0x37] = 0x1A, [0x38] = 0x1C, [0x39] = 0x19,

                // Цифровий блок 0–9
                [0x60] = 0x52, [0x61] = 0x53, [0x62] = 0x54, [0x63] = 0x55, [0x64] = 0x56,
                [0x65] = 0x57, [0x66] = 0x58, [0x67] = 0x59, [0x68] = 0x5B, [0x69] = 0x5C,

                // F1–F20
                [0x70] = 0x7A, [0x71] = 0x78, [0x72] = 0x63, [0x73] = 0x76, [0x74] = 0x60, [0x75] = 0x61,
                [0x76] = 0x62, [0x77] = 0x64, [0x78] = 0x65, [0x79] = 0x6D, [0x7A] = 0x67, [0x7B] = 0x6F,
                [0x7C] = 0x69, [0x7D] = 0x6B, [0x7E] = 0x71, [0x7F] = 0x6A, [0x80] = 0x40, [0x81] = 0x4F,
                [0x82] = 0x50, [0x83] = 0x5A,

                // Службові
                [0x2C] = 0x69, // PrintScreen → F13 (на зовнішніх клавіатурах Apple він на місці PrtSc)
                [0x2D] = 0x72, // Insert → Help
                [0x2E] = 0x75, // Delete → Forward Delete
                [0x24] = 0x73, // Home
                [0x23] = 0x77, // End
                [0x21] = 0x74, // Page Up
                [0x22] = 0x79, // Page Down
                [0x1B] = 0x35, // Escape
                [0x20] = 0x31, // Space
                [0x09] = 0x30, // Tab
                [0x0D] = 0x24, // Enter → Return
                [0x08] = 0x33, // Backspace → Delete
                [0x25] = 0x7B, // ←
                [0x27] = 0x7C, // →
                [0x28] = 0x7D, // ↓
                [0x26] = 0x7E, // ↑
                [0xC0] = 0x32, // ~ (`)
            };
        }
    }
}
