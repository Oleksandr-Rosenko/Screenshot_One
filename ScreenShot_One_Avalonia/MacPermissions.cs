using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MsBox.Avalonia;
using MsBox.Avalonia.Enums;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// macOS: дозвіл «Конфіденційність і безпека → Запис екрана».
    /// Без нього система мовчки віддає знімок лише зі шпалерами (без вікон) — користувач бачить «порожній» скріншот.
    /// Попередження показується максимум раз за сесію; CGPreflightScreenCaptureAccess кешує стан до перезапуску програми,
    /// тому після надання дозволу програму треба перезапустити.
    /// </summary>
    public enum MacPermissionKind { ScreenRecording, Microphone, InputMonitoring }
    public enum MacPermissionState { Granted, Denied, NotDetermined }

    public static class MacPermissions
    {
        private static bool _warnedThisSession;

        /// <summary>Перевіряє дозвіл і, якщо його немає, пояснює, що робити. Нічого не блокує.</summary>
        public static async Task WarnIfScreenCaptureDeniedAsync()
        {
            if (!OperatingSystem.IsMacOS() || _warnedThisSession) return;
            if (MacCoreGraphics.HasScreenCaptureAccess()) return;

            _warnedThisSession = true;
            AppLog.Write("macOS: немає дозволу «Запис екрана»");

            // Додає програму до списку в Системних налаштуваннях (системний запит показується один раз)
            MacCoreGraphics.RequestScreenCaptureAccess();

            try
            {
                var box = MessageBoxManager.GetMessageBoxStandard(
                    Loc.T("Mac_PermissionTitle"),
                    Loc.T("Mac_PermissionText"),
                    ButtonEnum.YesNo, Icon.Warning);

                if (await box.ShowWindowAsync() == ButtonResult.Yes)
                    MacCoreGraphics.OpenScreenCaptureSettings();
            }
            catch (Exception ex)
            {
                AppLog.Warn(ex);
            }
        }
        // ---- Розділ «Дозволи macOS» у налаштуваннях ----

        private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string AVFoundation = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

        [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightListenEventAccess();
        [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRequestListenEventAccess();
        [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
        [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendRetNInt(IntPtr receiver, IntPtr sel, IntPtr arg);
        [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void Send2(IntPtr receiver, IntPtr sel, IntPtr arg1, IntPtr arg2);

        /// <summary>Подія: стан дозволу змінився (відповідь на системний запит мікрофона приходить асинхронно).</summary>
        public static event Action? Changed;

        public static MacPermissionState GetState(MacPermissionKind kind)
        {
            if (!OperatingSystem.IsMacOS()) return MacPermissionState.Granted;
            try
            {
                switch (kind)
                {
                    case MacPermissionKind.ScreenRecording:
                        // CGPreflightScreenCaptureAccess не розрізняє «відмовлено» і «ще не питали»
                        return MacCoreGraphics.HasScreenCaptureAccess() ? MacPermissionState.Granted : MacPermissionState.Denied;
                    case MacPermissionKind.InputMonitoring:
                        return CGPreflightListenEventAccess() ? MacPermissionState.Granted : MacPermissionState.Denied;
                    case MacPermissionKind.Microphone:
                        // AVAuthorizationStatus: 0 — ще не питали, 1 — обмежено, 2 — відмовлено, 3 — дозволено
                        nint status = SendRetNInt(objc_getClass("AVCaptureDevice"), sel_registerName("authorizationStatusForMediaType:"), AudioMediaType());
                        return status switch { 3 => MacPermissionState.Granted, 0 => MacPermissionState.NotDetermined, _ => MacPermissionState.Denied };
                }
            }
            catch (Exception ex) { AppLog.Warn(ex); }
            return MacPermissionState.NotDetermined;
        }

        /// <summary>
        /// «Дозволити»: системний запит, якщо його ще не показували, інакше — потрібна сторінка «Конфіденційність і безпека».
        /// Мікрофон, про який ще не питали, у списку системних налаштувань відсутній — тому для нього лише запит.
        /// </summary>
        public static void Request(MacPermissionKind kind)
        {
            if (!OperatingSystem.IsMacOS()) return;
            try
            {
                switch (kind)
                {
                    case MacPermissionKind.ScreenRecording:
                        MacCoreGraphics.RequestScreenCaptureAccess();
                        OpenSettings(kind);
                        break;
                    case MacPermissionKind.InputMonitoring:
                        if (!CGRequestListenEventAccess()) OpenSettings(kind);
                        break;
                    case MacPermissionKind.Microphone:
                        if (GetState(kind) == MacPermissionState.NotDetermined) RequestMicrophone();
                        else OpenSettings(kind);
                        break;
                }
            }
            catch (Exception ex) { AppLog.Error(ex, "Mac: запит дозволу " + kind); }
        }

        public static void OpenSettings(MacPermissionKind kind)
        {
            string pane = kind switch
            {
                MacPermissionKind.Microphone => "Privacy_Microphone",
                MacPermissionKind.InputMonitoring => "Privacy_ListenEvent",
                _ => "Privacy_ScreenCapture"
            };
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    ArgumentList = { "x-apple.systempreferences:com.apple.preference.security?" + pane },
                    UseShellExecute = false
                });
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        /// <summary>Перезапуск програми (дозвіл «Запис екрана» macOS застосовує лише до нового процесу).</summary>
        public static void Restart()
        {
            string? bundle = MacUpdateChecker.CurrentAppBundle();
            if (bundle == null) { AppLog.Write("Mac: перезапуск неможливий — програма не в .app"); return; }
            try
            {
                string q = "'" + bundle.Replace("'", "'\\''") + "'";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    ArgumentList = { "-c", $"sleep 1; /usr/bin/open -n {q}" },
                    UseShellExecute = false
                });
                AppLog.Write("Mac: перезапуск після зміни дозволів");
                App.ShutdownForUpdate();
            }
            catch (Exception ex) { AppLog.Error(ex, "Mac: перезапуск"); }
        }

        private static IntPtr AudioMediaType()
        {
            // AVMediaTypeAudio — глобальна NSString* у AVFoundation
            var lib = NativeLibrary.Load(AVFoundation);
            return Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, "AVMediaTypeAudio"));
        }

        // Глобальний ObjC-блок для completionHandler: requestAccessForMediaType:completionHandler: (void (^)(BOOL granted))
        [StructLayout(LayoutKind.Sequential)] private struct BlockDescriptor { public nuint Reserved; public nuint Size; }
        [StructLayout(LayoutKind.Sequential)] private struct BlockLiteral { public IntPtr Isa; public int Flags; public int Reserved; public IntPtr Invoke; public IntPtr Descriptor; }
        private static IntPtr _micBlock;

        [UnmanagedCallersOnly]
        private static void OnMicAnswer(IntPtr block, byte granted)
        {
            try
            {
                AppLog.Write($"Mac: мікрофон — {(granted != 0 ? "дозволено" : "відмовлено")}");
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Changed?.Invoke());
            }
            catch (Exception ex) { AppLog.Warn(ex); }
        }

        private static unsafe void RequestMicrophone()
        {
            if (_micBlock == IntPtr.Zero)
            {
                const int BLOCK_IS_GLOBAL = 1 << 28;
                IntPtr isa = NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteGlobalBlock");
                IntPtr desc = Marshal.AllocHGlobal(sizeof(BlockDescriptor));
                *(BlockDescriptor*)desc = new BlockDescriptor { Reserved = 0, Size = (nuint)sizeof(BlockLiteral) };
                delegate* unmanaged<IntPtr, byte, void> fn = &OnMicAnswer;
                IntPtr block = Marshal.AllocHGlobal(sizeof(BlockLiteral));
                *(BlockLiteral*)block = new BlockLiteral { Isa = isa, Flags = BLOCK_IS_GLOBAL, Reserved = 0, Invoke = (IntPtr)fn, Descriptor = desc };
                _micBlock = block; // глобальний блок живе до кінця програми (Block_copy повертає його ж)
            }
            Send2(objc_getClass("AVCaptureDevice"), sel_registerName("requestAccessForMediaType:completionHandler:"), AudioMediaType(), _micBlock);
        }
    }
}
