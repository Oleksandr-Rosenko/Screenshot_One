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
    ///   3.3.1: + запасна реєстрація RegisterHotKey — Windows (UIPI) не викликає hook, коли активне вікно
    ///   запущене від адміністратора (термінал, PowerShell); hook перевстановлюється при зміні активного вікна
    ///   (віртуальні машини ставлять власний hook і стають першими в черзі) і раз на хвилину (Windows тихо
    ///   знімає hook, який не встиг відповісти).
    ///   Hook (а не RegisterHotKey) лишається, бо на Windows 11 клавішу PrintScreen часто займає «Ножиці»,
    ///   і RegisterHotKey для неї просто не реєструється. Окремий потік потрібен, щоб Windows не знімала hook,
    ///   коли UI-потік зайнятий (раніше через це гарячі клавіші «раптом» переставали працювати).
    /// macOS: системна реєстрація через Carbon RegisterEventHotKey (не потребує дозволу «Універсальний доступ»).
    /// </summary>
    public static class HotkeyService
    {
        private static Action<HotkeyAction>? _onPressed;
        private static bool _started;

        /// <summary>
        /// Показ натиснутих клавіш під час запису (2.9.0): коли true, Windows-hook повідомляє про кожне
        /// натискання (код VK + модифікатори). Подія приходить з потоку hook — обробник має бути швидким.
        /// </summary>
        public static volatile bool ObserveKeys;
        public static event Action<int, int>? KeyObserved;

        internal static void RaiseKeyObserved(int vk, int modifiers)
        {
            try { KeyObserved?.Invoke(vk, modifiers); }
            catch (Exception ex) { AppLog.Write($"[Hotkeys] Помилка показу клавіш: {ex.Message}"); }
        }

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
                WindowsHook.RequestSync(); // запасні RegisterHotKey: зняти на час вводу комбінації, потім — нові
            }
            else if (OperatingSystem.IsMacOS() && _started)
            {
                if (suspended) MacCarbonHotkeys.UnregisterAll();
                else MacCarbonHotkeys.RegisterAll();
            }
        }

        /// <summary>Самоперевірка: hook не розпізнає комбінації (перевіряємо запасну реєстрацію).</summary>
        internal static volatile bool TestBypassHook;
        /// <summary>Самоперевірка: замість дії — повідомити, що і яким шляхом спрацювало.</summary>
        internal static Action<HotkeyAction, string>? TestObserver;

        private static void Raise(HotkeyAction action, string via = "hook")
        {
            var observer = TestObserver;
            if (observer != null) { observer(action, via); return; }
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
            private const uint WM_TIMER = 0x0113;
            private const uint WM_HOTKEY = 0x0312;
            private const uint WM_APP_SYNC = 0x8001;
            private const uint PM_NOREMOVE = 0x0000;
            private const uint MOD_NOREPEAT = 0x4000;
            private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
            private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

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

            private delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
            private static readonly WinEventProc _foregroundProc = OnForegroundChanged;
            private static IntPtr _winEventHook = IntPtr.Zero;
            private static UIntPtr _reinstallTimer = UIntPtr.Zero;
            private static UIntPtr _watchTimer = UIntPtr.Zero;
            private static readonly HashSet<int> _registeredIds = new();
            // Остання дія з hook — щоб та сама натиснута клавіша не спрацювала двічі (hook + RegisterHotKey)
            private static HotkeyAction? _lastHookAction;
            private static long _lastHookTicks;

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

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

            [DllImport("user32.dll")]
            private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventProc proc, uint idProcess, uint idThread, uint flags);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

            [DllImport("user32.dll")]
            private static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool KillTimer(IntPtr hWnd, UIntPtr uIDEvent);

            /// <summary>Перечитати комбінації для запасної реєстрації (виконується в потоці hook).</summary>
            internal static void RequestSync()
            {
                if (_threadId != 0) PostThreadMessage(_threadId, WM_APP_SYNC, IntPtr.Zero, IntPtr.Zero);
            }

            private static void InstallHook()
            {
                IntPtr old = _hookId;
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
                if (_hookId == IntPtr.Zero)
                {
                    AppLog.Write($"[Hotkeys] SetWindowsHookEx не вдався, код {Marshal.GetLastWin32Error()}");
                    _hookId = old; // лишаємо попередній, якщо він ще живий
                    return;
                }
                // Новий hook стоїть першим у черзі; старий знімаємо вже після цього, щоб не було «дірки»
                if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
            }

            private static void OnForegroundChanged(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
            {
                // Віртуальна машина ставить свій hook, коли отримує фокус, — перевстановлюємо свій трохи згодом
                if (_reinstallTimer == UIntPtr.Zero) _reinstallTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 700, IntPtr.Zero);
            }

            /// <summary>Модифікатори settings.json → MOD_* для RegisterHotKey (публічний для тестів).</summary>
            internal static uint ToWinModifiers(int mods)
            {
                uint m = 0;
                if ((mods & HotkeyModifiers.Alt) != 0) m |= 0x0001;
                if ((mods & HotkeyModifiers.Ctrl) != 0) m |= 0x0002;
                if ((mods & HotkeyModifiers.Shift) != 0) m |= 0x0004;
                if ((mods & HotkeyModifiers.Meta) != 0) m |= 0x0008;
                return m;
            }

            private static void SyncRegistered()
            {
                foreach (int id in _registeredIds) UnregisterHotKey(IntPtr.Zero, id);
                _registeredIds.Clear();
                if (Suspended) return;
                foreach (var (action, key, mods) in GetConfiguredHotkeys())
                {
                    if (key == 0) continue;
                    if (RegisterHotKey(IntPtr.Zero, (int)action, ToWinModifiers(mods) | MOD_NOREPEAT, (uint)key))
                        _registeredIds.Add((int)action);
                    else
                        AppLog.Write($"[Hotkeys] запасна реєстрація {action} не вдалась (комбінацію зайняла інша програма, напр. «Ножиці» — PrintScreen); hook працює як і раніше");
                }
            }

            private static void OnRegisteredHotkey(int id)
            {
                if (Suspended || !Enum.IsDefined(typeof(HotkeyAction), id)) return;
                var action = (HotkeyAction)id;
                // Hook уже обробив це натискання — не дублюємо
                if (_lastHookAction == action && Environment.TickCount64 - Interlocked.Read(ref _lastHookTicks) < 400) return;
                AppLog.Write($"[Hotkeys] {action} — через RegisterHotKey (hook не отримав натискання)");
                Raise(action, "RegisterHotKey");
            }

            internal static void Start()
            {
                if (_thread != null) return;

                var ready = new ManualResetEventSlim(false);
                _thread = new Thread(() =>
                {
                    _threadId = GetCurrentThreadId();

                    // Створюємо чергу повідомлень потоку до того, як хтось захоче надіслати йому WM_QUIT.
                    PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

                    InstallHook();
                    SyncRegistered();
                    _winEventHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _foregroundProc, 0, 0, WINEVENT_OUTOFCONTEXT);
                    _watchTimer = SetTimer(IntPtr.Zero, UIntPtr.Zero, 60_000, IntPtr.Zero);
                    ready.Set();

                    while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
                    {
                        if (msg.hwnd == IntPtr.Zero)
                        {
                            if (msg.message == WM_HOTKEY) { OnRegisteredHotkey((int)msg.wParam); continue; }
                            if (msg.message == WM_APP_SYNC) { SyncRegistered(); continue; }
                            if (msg.message == WM_TIMER)
                            {
                                var id = (UIntPtr)(ulong)msg.wParam;
                                bool afterForeground = id == _reinstallTimer;
                                if (afterForeground) { KillTimer(IntPtr.Zero, id); _reinstallTimer = UIntPtr.Zero; }
                                if (afterForeground || id == _watchTimer) InstallHook();
                                continue;
                            }
                        }
                        TranslateMessage(ref msg);
                        DispatchMessage(ref msg);
                    }

                    foreach (int id in _registeredIds) UnregisterHotKey(IntPtr.Zero, id);
                    _registeredIds.Clear();
                    if (_winEventHook != IntPtr.Zero) { UnhookWinEvent(_winEventHook); _winEventHook = IntPtr.Zero; }
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

                        if (ObserveKeys) RaiseKeyObserved(vkCode, modifiers);

                        var action = TestBypassHook ? null : Match(vkCode, modifiers);
                        if (action != null)
                        {
                            _lastHookAction = action;
                            Interlocked.Exchange(ref _lastHookTicks, Environment.TickCount64);
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
