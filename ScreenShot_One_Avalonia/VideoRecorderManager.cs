using System;
using System.IO;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
#if WINDOWS
using ScreenRecorderLib;
#endif

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Результат спроби почати запис. Якщо Success == false, у Error — текст для користувача.
    /// </summary>
    public sealed class RecordingStartResult
    {
        public bool Success { get; }
        public string? Error { get; }

        private RecordingStartResult(bool success, string? error)
        {
            Success = success;
            Error = error;
        }

        public static RecordingStartResult Ok() => new RecordingStartResult(true, null);
        public static RecordingStartResult Fail(string error) => new RecordingStartResult(false, error);
    }

    public interface IVideoRecorderService
    {
        bool IsRecording { get; }
        event Action<bool>? OnStatusChanged;

        /// <summary>Запис зупинився сам, без команди користувача (помилка енкодера, ffmpeg завершився тощо).</summary>
        event Action<string>? OnRecordingFailed;

        IReadOnlyList<MonitorInfo> GetDisplays();

        Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null);

        /// <summary>
        /// Зупиняє запис і чекає, поки файл буде повністю дописаний і закритий.
        /// Повертає шлях до відео або порожній рядок, якщо файлу немає.
        /// </summary>
        Task<string> StopRecordingAsync();
    }

    public static class VideoRecorderManager
    {
        private static IVideoRecorderService? _instance;

        public static IVideoRecorderService Instance
        {
            get
            {
                if (_instance == null)
                {
                    if (OperatingSystem.IsWindows())
                    {
                        _instance = new WindowsVideoRecorderService();
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        _instance = new MacVideoRecorderService();
                    }
                    else
                    {
                        _instance = new FallbackVideoRecorderService();
                    }
                }
                return _instance;
            }
            set => _instance = value;
        }

        public static bool IsRecording => Instance.IsRecording;

        public static event Action<bool>? OnStatusChanged
        {
            add => Instance.OnStatusChanged += value;
            remove => Instance.OnStatusChanged -= value;
        }

        public static event Action<string>? OnRecordingFailed
        {
            add => Instance.OnRecordingFailed += value;
            remove => Instance.OnRecordingFailed -= value;
        }

        public static IReadOnlyList<MonitorInfo> GetDisplays() => Instance.GetDisplays();

        public static Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null)
            => Instance.StartRecordingAsync(showClicks, recordSystemAudio, monitorDeviceName, left, top, right, bottom);

        public static Task<string> StopRecordingAsync() => Instance.StopRecordingAsync();

        internal static string CreateVideoPath()
        {
            string videoFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screenshot_One", "Videos");
            if (!Directory.Exists(videoFolder)) Directory.CreateDirectory(videoFolder);
            return Path.Combine(videoFolder, $"video_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        }

        internal static bool IsNonEmptyFile(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path) && new FileInfo(path).Length > 0; }
            catch { return false; }
        }
    }

#if WINDOWS
    public class WindowsVideoRecorderService : IVideoRecorderService
    {
        // Скільки максимум чекаємо, поки ScreenRecorderLib допише файл після Stop()
        private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(20);

        private Recorder? _recorder;
        private TaskCompletionSource<string>? _completion;
        private volatile bool _isRecording = false;
        private string _currentVideoPath = string.Empty;
        private string? _lastError;

        public bool IsRecording => _isRecording;
        public event Action<bool>? OnStatusChanged;
        public event Action<string>? OnRecordingFailed;

        public IReadOnlyList<MonitorInfo> GetDisplays()
        {
            var monitorList = new List<MonitorInfo>();
            try
            {
                var displays = Recorder.GetDisplays();
                int counter = 1;
                foreach (var display in displays)
                {
                    string realName = string.IsNullOrWhiteSpace(display.FriendlyName)
                        ? $"Екран {counter}"
                        : $"{display.FriendlyName} (Екран {counter})";

                    monitorList.Add(new MonitorInfo { DisplayName = realName, DeviceName = display.DeviceName });
                    counter++;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsVideoRecorder] GetDisplays error: {ex.Message}");
            }
            return monitorList;
        }

        public Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null)
        {
            if (_isRecording) return Task.FromResult(RecordingStartResult.Fail("Запис уже йде."));

            try
            {
                _currentVideoPath = VideoRecorderManager.CreateVideoPath();
            }
            catch (Exception ex)
            {
                return Task.FromResult(RecordingStartResult.Fail($"Не вдалося створити папку для відео: {ex.Message}"));
            }

            var options = new RecorderOptions
            {
                OutputOptions = new OutputOptions { RecorderMode = RecorderMode.Video },
                VideoEncoderOptions = new VideoEncoderOptions
                {
                    Framerate = 30,
                    Quality = 75,
                    IsHardwareEncodingEnabled = true,
                    IsLowLatencyEnabled = true,
                    IsThrottlingDisabled = true,
                    IsMp4FastStartEnabled = false
                },
                MouseOptions = new MouseOptions { IsMousePointerEnabled = true, IsMouseClicksDetected = showClicks },

                AudioOptions = new AudioOptions
                {
                    Bitrate = AudioBitrate.bitrate_128kbps,
                    Channels = AudioChannels.Stereo,
                    IsAudioEnabled = recordSystemAudio,
                    IsOutputDeviceEnabled = recordSystemAudio,
                    IsInputDeviceEnabled = false
                }
            };

            Recorder? recorder = null;
            try
            {
                var displaySource = new DisplayRecordingSource(monitorDeviceName);

                if (left.HasValue && top.HasValue && right.HasValue && bottom.HasValue)
                {
                    int x = left.Value;
                    int y = top.Value;
                    int w = right.Value - left.Value;
                    int h = bottom.Value - top.Value;

                    // Координати зони — у системі всього робочого столу, а ScreenRecorderLib чекає їх відносно монітора.
                    var screen = ScreenService.FindScreenByDeviceName(ScreenService.GetScreens(), monitorDeviceName);
                    if (screen != null)
                    {
                        x -= screen.Bounds.X;
                        y -= screen.Bounds.Y;
                        w = Math.Min(w, screen.Bounds.Width - x);
                        h = Math.Min(h, screen.Bounds.Height - y);
                    }

                    if (x % 2 != 0) { x--; w++; }
                    if (y % 2 != 0) { y--; h++; }
                    if (w % 2 != 0) w--;
                    if (h % 2 != 0) h--;

                    if (w <= 0) w = 2;
                    if (h <= 0) h = 2;

                    displaySource.SourceRect = new ScreenRect(x, y, w, h);
                }

                options.SourceOptions = new SourceOptions { RecordingSources = new List<RecordingSourceBase> { displaySource } };

                string path = _currentVideoPath;
                var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                Recorder thisRecorder = Recorder.CreateRecorder(options);
                recorder = thisRecorder;

                thisRecorder.OnRecordingComplete += (s, e) =>
                {
                    completion.TrySetResult(string.IsNullOrEmpty(e.FilePath) ? path : e.FilePath);
                };

                thisRecorder.OnRecordingFailed += (s, e) =>
                {
                    _lastError = string.IsNullOrWhiteSpace(e.Error) ? "Невідома помилка запису." : e.Error;
                    Debug.WriteLine($"[WindowsVideoRecorder] Recording failed: {_lastError}");

                    completion.TrySetResult(VideoRecorderManager.IsNonEmptyFile(path) ? path : string.Empty);

                    // Якщо запис упав сам (а не після Stop) — повідомляємо UI
                    if (_isRecording && ReferenceEquals(_recorder, thisRecorder))
                    {
                        _isRecording = false;
                        _recorder = null;
                        _completion = null;
                        OnStatusChanged?.Invoke(false);
                        OnRecordingFailed?.Invoke(_lastError);
                        DisposeLater(thisRecorder);
                    }
                };

                _lastError = null;
                _recorder = thisRecorder;
                _completion = completion;
                _isRecording = true;

                thisRecorder.Record(path);

                if (!_isRecording)
                {
                    // Помилка прийшла одразу під час старту — її вже обробив OnRecordingFailed
                    return Task.FromResult(RecordingStartResult.Fail($"Не вдалося почати запис: {_lastError}"));
                }

                OnStatusChanged?.Invoke(true);
                return Task.FromResult(RecordingStartResult.Ok());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Критична помилка старту запису відео: {ex.Message}");
                _isRecording = false;
                _recorder = null;
                _completion = null;
                if (recorder != null) DisposeLater(recorder);
                OnStatusChanged?.Invoke(false);
                return Task.FromResult(RecordingStartResult.Fail($"Не вдалося почати запис: {ex.Message}"));
            }
        }

        public async Task<string> StopRecordingAsync()
        {
            var recorder = _recorder;
            var completion = _completion;
            if (!_isRecording || recorder == null || completion == null) return string.Empty;

            _isRecording = false;
            _recorder = null;
            _completion = null;
            OnStatusChanged?.Invoke(false);

            string path = _currentVideoPath;

            try
            {
                await Task.Run(() => recorder.Stop());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WindowsVideoRecorder] Stop error: {ex.Message}");
            }

            // Чекаємо OnRecordingComplete / OnRecordingFailed — тільки тоді файл повністю записаний
            var finished = await Task.WhenAny(completion.Task, Task.Delay(FinalizeTimeout));
            string result = finished == completion.Task
                ? await completion.Task
                : (VideoRecorderManager.IsNonEmptyFile(path) ? path : string.Empty);

            DisposeLater(recorder);
            return result;
        }

        private static void DisposeLater(Recorder recorder)
        {
            // Dispose не викликаємо з потоку подій самої бібліотеки
            Task.Run(() =>
            {
                try { recorder.Dispose(); } catch { }
            });
        }
    }
#else
    public class WindowsVideoRecorderService : IVideoRecorderService
    {
        public bool IsRecording => false;
        public event Action<bool>? OnStatusChanged { add { } remove { } }
        public event Action<string>? OnRecordingFailed { add { } remove { } }
        public IReadOnlyList<MonitorInfo> GetDisplays() => new List<MonitorInfo>();
        public Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null)
            => Task.FromResult(RecordingStartResult.Fail("Запис через ScreenRecorderLib доступний лише на Windows."));
        public Task<string> StopRecordingAsync() => Task.FromResult(string.Empty);
    }
#endif

    public class MacVideoRecorderService : IVideoRecorderService
    {
        // Скільки чекаємо після старту, щоб упевнитись, що ffmpeg не впав одразу
        private static readonly TimeSpan StartupCheckDelay = TimeSpan.FromMilliseconds(800);
        // Скільки максимум чекаємо, поки ffmpeg допише файл після «q»
        private static readonly TimeSpan FinalizeTimeout = TimeSpan.FromSeconds(15);
        private const int MaxLogLines = 20;

        private Process? _ffmpegProc;
        private volatile bool _isRecording = false;
        private volatile bool _isStarting = false;
        private volatile bool _stopRequested = false;
        private string _currentVideoPath = string.Empty;
        private readonly Queue<string> _ffmpegLog = new Queue<string>();
        private readonly object _logLock = new object();

        public bool IsRecording => _isRecording;
        public event Action<bool>? OnStatusChanged;
        public event Action<string>? OnRecordingFailed;

        public IReadOnlyList<MonitorInfo> GetDisplays()
        {
            var list = new List<MonitorInfo>();
            var screens = ScreenService.GetScreens()?.All;
            if (screens != null && screens.Count > 0)
            {
                for (int i = 0; i < screens.Count; i++)
                {
                    list.Add(new MonitorInfo { DisplayName = $"Екран {i + 1}", DeviceName = i.ToString() });
                }
            }
            else
            {
                list.Add(new MonitorInfo { DisplayName = "Головний екран", DeviceName = "0" });
            }
            return list;
        }

        /// <summary>
        /// Шукає ffmpeg: спершу поруч із програмою (всередині .app), потім у стандартних місцях Homebrew.
        /// Програми, запущені з Finder, не бачать PATH з терміналу, тому просто "ffmpeg" тут не працює.
        /// </summary>
        public static string? FindFfmpeg()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
                "/opt/homebrew/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                "/usr/bin/ffmpeg"
            };

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public async Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null)
        {
            if (_isRecording || _isStarting) return RecordingStartResult.Fail("Запис уже йде.");

            string? ffmpegExe = FindFfmpeg();
            if (ffmpegExe == null)
            {
                return RecordingStartResult.Fail(
                    "Для запису відео на Mac потрібен ffmpeg, а його не знайдено.\n\n" +
                    "Встановіть його командою «brew install ffmpeg» у Терміналі й спробуйте ще раз.");
            }

            try
            {
                _currentVideoPath = VideoRecorderManager.CreateVideoPath();
            }
            catch (Exception ex)
            {
                return RecordingStartResult.Fail($"Не вдалося створити папку для відео: {ex.Message}");
            }

            // У avfoundation під індексом 0 зазвичай камера (на MacBook), тому звертаємось до екрана за назвою.
            int screenIndex = int.TryParse(monitorDeviceName, out int parsed) && parsed >= 0 ? parsed : 0;
            string input = $"\"Capture screen {screenIndex}:none\"";

            string filter = "";
            if (left.HasValue && top.HasValue && right.HasValue && bottom.HasValue)
            {
                int w = right.Value - left.Value;
                int h = bottom.Value - top.Value;
                if (w % 2 != 0) w--;
                if (h % 2 != 0) h--;
                if (w > 0 && h > 0)
                {
                    filter = $"-filter:v \"crop={w}:{h}:{left.Value}:{top.Value}\"";
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                Arguments = $"-y -hide_banner -nostats -loglevel warning -f avfoundation -capture_cursor 1 -i {input} {filter} -c:v libx264 -preset ultrafast -pix_fmt yuv420p \"{_currentVideoPath}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            lock (_logLock) _ffmpegLog.Clear();
            _stopRequested = false;
            _isStarting = true;

            Process proc;
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                // stderr обов'язково читаємо, інакше ffmpeg зависне, коли заповниться буфер
                proc.ErrorDataReceived += (s, e) => { if (e.Data != null) AppendLog(e.Data); };
                proc.Exited += (s, e) => OnFfmpegExited(proc);
                proc.Start();
                proc.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _isStarting = false;
                Debug.WriteLine($"[MacVideoRecorder] Start error: {ex.Message}");
                return RecordingStartResult.Fail($"Не вдалося запустити ffmpeg: {ex.Message}");
            }

            _ffmpegProc = proc;
            _isRecording = true;

            // Якщо ffmpeg одразу завершився (немає дозволу на запис екрана, не той пристрій тощо) — це помилка старту
            await Task.Delay(StartupCheckDelay);
            _isStarting = false;

            if (proc.HasExited)
            {
                _isRecording = false;
                _ffmpegProc = null;
                return RecordingStartResult.Fail(
                    "ffmpeg завершився одразу після старту. Перевірте, що Screenshot One має дозвіл " +
                    "«Запис екрана» в Системних параметрах → Приватність і безпека.\n\n" + GetLastLog());
            }

            OnStatusChanged?.Invoke(true);
            return RecordingStartResult.Ok();
        }

        public async Task<string> StopRecordingAsync()
        {
            var proc = _ffmpegProc;
            if (!_isRecording || proc == null) return string.Empty;

            _stopRequested = true;
            _isRecording = false;
            _ffmpegProc = null;
            OnStatusChanged?.Invoke(false);

            string path = _currentVideoPath;

            try
            {
                // «q» — штатна зупинка: ffmpeg дописує індекс MP4 і закриває файл
                await proc.StandardInput.WriteLineAsync("q");
                await proc.StandardInput.FlushAsync();

                using var cts = new CancellationTokenSource(FinalizeTimeout);
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MacVideoRecorder] Stop error: {ex.Message}");
                try { if (!proc.HasExited) proc.Kill(); } catch { }
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }

            return VideoRecorderManager.IsNonEmptyFile(path) ? path : string.Empty;
        }

        private void OnFfmpegExited(Process proc)
        {
            // Нас цікавить лише випадок, коли ffmpeg зупинився сам посеред запису
            if (_isStarting || _stopRequested || !_isRecording || !ReferenceEquals(proc, _ffmpegProc)) return;

            _isRecording = false;
            _ffmpegProc = null;
            OnStatusChanged?.Invoke(false);
            OnRecordingFailed?.Invoke("Запис зупинився: ffmpeg несподівано завершив роботу.\n\n" + GetLastLog());
        }

        private void AppendLog(string line)
        {
            lock (_logLock)
            {
                _ffmpegLog.Enqueue(line);
                while (_ffmpegLog.Count > MaxLogLines) _ffmpegLog.Dequeue();
            }
        }

        private string GetLastLog()
        {
            lock (_logLock)
            {
                return _ffmpegLog.Count == 0 ? "" : "Повідомлення ffmpeg:\n" + string.Join("\n", _ffmpegLog);
            }
        }
    }

    public class FallbackVideoRecorderService : IVideoRecorderService
    {
        public bool IsRecording => false;
        public event Action<bool>? OnStatusChanged { add { } remove { } }
        public event Action<string>? OnRecordingFailed { add { } remove { } }
        public IReadOnlyList<MonitorInfo> GetDisplays() => new List<MonitorInfo>();
        public Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null)
            => Task.FromResult(RecordingStartResult.Fail("Запис відео на цій платформі не підтримується."));
        public Task<string> StopRecordingAsync() => Task.FromResult(string.Empty);
    }
}
