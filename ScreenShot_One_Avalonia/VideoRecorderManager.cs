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

        Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null, bool recordMicrophone = false);

        /// <summary>Запис на паузі (IsRecording при цьому лишається true).</summary>
        bool IsPaused { get; }
        event Action<bool>? OnPausedChanged;

        /// <summary>Ставить запис на паузу. Повертає false, якщо не вдалося.</summary>
        Task<bool> PauseAsync();

        /// <summary>Продовжує запис після паузи.</summary>
        Task<bool> ResumeAsync();

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

        public static Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null, bool recordMicrophone = false)
            => Instance.StartRecordingAsync(showClicks, recordSystemAudio, monitorDeviceName, left, top, right, bottom, recordMicrophone);

        public static bool IsPaused => Instance.IsPaused;
        public static Task<bool> PauseAsync() => Instance.PauseAsync();
        public static Task<bool> ResumeAsync() => Instance.ResumeAsync();

        public static event Action<bool>? OnPausedChanged
        {
            add => Instance.OnPausedChanged += value;
            remove => Instance.OnPausedChanged -= value;
        }

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

        private volatile bool _isPaused;
        public bool IsPaused => _isPaused;
        public event Action<bool>? OnPausedChanged;

        public Task<bool> PauseAsync()
        {
            var recorder = _recorder;
            if (!_isRecording || _isPaused || recorder == null) return Task.FromResult(false);
            try
            {
                recorder.Pause();
                _isPaused = true;
                OnPausedChanged?.Invoke(true);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Пауза запису");
                return Task.FromResult(false);
            }
        }

        public Task<bool> ResumeAsync()
        {
            var recorder = _recorder;
            if (!_isRecording || !_isPaused || recorder == null) return Task.FromResult(false);
            try
            {
                recorder.Resume();
                _isPaused = false;
                OnPausedChanged?.Invoke(false);
                return Task.FromResult(true);
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Продовження запису");
                return Task.FromResult(false);
            }
        }

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
                        ? Loc.F("Video_Screen", counter)
                        : Loc.F("Video_ScreenNamed", display.FriendlyName, counter);

                    monitorList.Add(new MonitorInfo { DisplayName = realName, DeviceName = display.DeviceName });
                    counter++;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"[WindowsVideoRecorder] GetDisplays error: {ex.Message}");
            }
            return monitorList;
        }

        public Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null, bool recordMicrophone = false)
        {
            if (_isRecording) return Task.FromResult(RecordingStartResult.Fail(Loc.T("Video_AlreadyRecording")));
            _isPaused = false;

            try
            {
                _currentVideoPath = VideoRecorderManager.CreateVideoPath();
            }
            catch (Exception ex)
            {
                return Task.FromResult(RecordingStartResult.Fail(Loc.F("Video_FolderFailed", ex.Message)));
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
                    // Системний звук і мікрофон незалежні; мікрофон — пристрій запису за замовчуванням
                    IsAudioEnabled = recordSystemAudio || recordMicrophone,
                    IsOutputDeviceEnabled = recordSystemAudio,
                    IsInputDeviceEnabled = recordMicrophone
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
                    _lastError = string.IsNullOrWhiteSpace(e.Error) ? Loc.T("Video_UnknownRecordError") : e.Error;
                    AppLog.Write($"[WindowsVideoRecorder] Recording failed: {_lastError}");

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
                    return Task.FromResult(RecordingStartResult.Fail(Loc.F("Video_StartFailed", _lastError)));
                }

                OnStatusChanged?.Invoke(true);
                return Task.FromResult(RecordingStartResult.Ok());
            }
            catch (Exception ex)
            {
                AppLog.Write($"Критична помилка старту запису відео: {ex.Message}");
                _isRecording = false;
                _recorder = null;
                _completion = null;
                if (recorder != null) DisposeLater(recorder);
                OnStatusChanged?.Invoke(false);
                return Task.FromResult(RecordingStartResult.Fail(Loc.F("Video_StartFailed", ex.Message)));
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
            if (_isPaused) { _isPaused = false; OnPausedChanged?.Invoke(false); }
            OnStatusChanged?.Invoke(false);

            string path = _currentVideoPath;

            try
            {
                await Task.Run(() => recorder.Stop());
            }
            catch (Exception ex)
            {
                AppLog.Write($"[WindowsVideoRecorder] Stop error: {ex.Message}");
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
                try { recorder.Dispose(); } catch (Exception logEx) { AppLog.Warn(logEx); }
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
        public Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null, bool recordMicrophone = false)
            => Task.FromResult(RecordingStartResult.Fail(Loc.T("Video_WindowsOnly")));
        public Task<string> StopRecordingAsync() => Task.FromResult(string.Empty);
        public bool IsPaused => false;
        public event Action<bool>? OnPausedChanged { add { } remove { } }
        public Task<bool> PauseAsync() => Task.FromResult(false);
        public Task<bool> ResumeAsync() => Task.FromResult(false);
    }
#endif

    /// <summary>
    /// macOS: запис через ffmpeg (avfoundation).
    /// Пауза реалізована сегментами: на паузі поточний шматок штатно дописується (ffmpeg «q»),
    /// після продовження пишеться новий, а при зупинці всі шматки склеюються без перекодування (concat, -c copy).
    /// Мікрофон: аудіопристрій avfoundation «default» (якщо ffmpeg його не знає — індекс 0).
    /// </summary>
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
        private volatile bool _isPaused = false;
        private string _currentVideoPath = string.Empty;
        private string? _ffmpegExe;
        private string _argsTemplate = ""; // з плейсхолдером {OUTPUT}
        private readonly List<string> _segments = new List<string>();
        private readonly Queue<string> _ffmpegLog = new Queue<string>();
        private readonly object _logLock = new object();
        private readonly SemaphoreSlim _opLock = new SemaphoreSlim(1, 1);

        public bool IsRecording => _isRecording;
        public bool IsPaused => _isPaused;
        public event Action<bool>? OnStatusChanged;
        public event Action<string>? OnRecordingFailed;
        public event Action<bool>? OnPausedChanged;

        public IReadOnlyList<MonitorInfo> GetDisplays()
        {
            var list = new List<MonitorInfo>();
            var screens = ScreenService.GetScreens()?.All;
            if (screens != null && screens.Count > 0)
            {
                for (int i = 0; i < screens.Count; i++)
                {
                    list.Add(new MonitorInfo { DisplayName = Loc.F("Video_Screen", i + 1), DeviceName = i.ToString() });
                }
            }
            else
            {
                list.Add(new MonitorInfo { DisplayName = Loc.T("Video_MainScreen"), DeviceName = "0" });
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

        /// <summary>Аргументи ffmpeg (публічний для тестів). {OUTPUT} замінюється шляхом сегмента.</summary>
        public static string BuildArguments(int screenIndex, string? audioDevice, int? left, int? top, int? right, int? bottom)
        {
            // У avfoundation під індексом 0 зазвичай камера (на MacBook), тому звертаємось до екрана за назвою.
            string input = $"\"Capture screen {screenIndex}:{audioDevice ?? "none"}\"";

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

            string audio = audioDevice != null ? "-c:a aac -b:a 128k" : "";
            return $"-y -hide_banner -nostats -loglevel warning -f avfoundation -capture_cursor 1 -i {input} {filter} " +
                   $"-c:v libx264 -preset ultrafast -pix_fmt yuv420p {audio} \"{{OUTPUT}}\"";
        }

        public async Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null, bool recordMicrophone = false)
        {
            if (_isRecording || _isStarting) return RecordingStartResult.Fail(Loc.T("Video_AlreadyRecording"));

            _ffmpegExe = FindFfmpeg();
            if (_ffmpegExe == null)
            {
                return RecordingStartResult.Fail(
                    Loc.T("Video_NoFfmpeg"));
            }

            try
            {
                _currentVideoPath = VideoRecorderManager.CreateVideoPath();
            }
            catch (Exception ex)
            {
                return RecordingStartResult.Fail(Loc.F("Video_FolderFailed", ex.Message));
            }

            int screenIndex = int.TryParse(monitorDeviceName, out int parsed) && parsed >= 0 ? parsed : 0;
            _segments.Clear();
            _isPaused = false;

            // Системний звук macOS без віртуального аудіопристрою (BlackHole тощо) не віддає — пишемо лише мікрофон
            string?[] audioCandidates = recordMicrophone ? new string?[] { "default", "0" } : new string?[] { null };

            string? lastError = null;
            foreach (var audioDevice in audioCandidates)
            {
                _argsTemplate = BuildArguments(screenIndex, audioDevice, left, top, right, bottom);
                lastError = await StartSegmentAsync();
                if (lastError == null)
                {
                    _isRecording = true;
                    OnStatusChanged?.Invoke(true);
                    return RecordingStartResult.Ok();
                }
            }

            string hint = recordMicrophone
                ? Loc.T("Video_MacHintMic")
                : Loc.T("Video_MacHint");
            return RecordingStartResult.Fail(Loc.F("Video_FfmpegExited", hint, lastError));
        }

        private string NextSegmentPath()
        {
            string dir = Path.GetDirectoryName(_currentVideoPath) ?? Path.GetTempPath();
            string name = Path.GetFileNameWithoutExtension(_currentVideoPath);
            return Path.Combine(dir, $"{name}_part{_segments.Count + 1}.mp4");
        }

        /// <summary>Запускає ffmpeg у новий сегмент. Повертає null при успіху або текст помилки.</summary>
        private async Task<string?> StartSegmentAsync()
        {
            string segmentPath = NextSegmentPath();
            var psi = new ProcessStartInfo
            {
                FileName = _ffmpegExe!,
                Arguments = _argsTemplate.Replace("{OUTPUT}", segmentPath),
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            lock (_logLock) _ffmpegLog.Clear();
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
                AppLog.Write($"[MacVideoRecorder] Start error: {ex.Message}");
                return Loc.F("Video_FfmpegStartFailed", ex.Message);
            }

            _ffmpegProc = proc;

            // Якщо ffmpeg одразу завершився (немає дозволу, не той пристрій тощо) — це помилка старту
            await Task.Delay(StartupCheckDelay);
            _isStarting = false;

            if (proc.HasExited)
            {
                _ffmpegProc = null;
                try { if (File.Exists(segmentPath)) File.Delete(segmentPath); } catch (Exception logEx) { AppLog.Warn(logEx); }
                return GetLastLog();
            }

            _segments.Add(segmentPath);
            return null;
        }

        /// <summary>Штатно зупиняє поточний сегмент і чекає, поки ffmpeg допише файл.</summary>
        private async Task StopSegmentAsync()
        {
            var proc = _ffmpegProc;
            _ffmpegProc = null; // до «q», щоб OnFfmpegExited не вважав це збоєм
            if (proc == null) return;

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
                AppLog.Write($"[MacVideoRecorder] Stop error: {ex.Message}");
                try { if (!proc.HasExited) proc.Kill(); } catch (Exception logEx) { AppLog.Warn(logEx); }
            }
            finally
            {
                try { proc.Dispose(); } catch (Exception logEx) { AppLog.Warn(logEx); }
            }
        }

        public async Task<bool> PauseAsync()
        {
            await _opLock.WaitAsync();
            try
            {
                if (!_isRecording || _isPaused) return false;
                await StopSegmentAsync();
                _isPaused = true;
                OnPausedChanged?.Invoke(true);
                return true;
            }
            finally { _opLock.Release(); }
        }

        public async Task<bool> ResumeAsync()
        {
            await _opLock.WaitAsync();
            try
            {
                if (!_isRecording || !_isPaused) return false;
                string? error = await StartSegmentAsync();
                if (error != null)
                {
                    AppLog.Write("[MacVideoRecorder] Resume failed: " + error);
                    return false;
                }
                _isPaused = false;
                OnPausedChanged?.Invoke(false);
                return true;
            }
            finally { _opLock.Release(); }
        }

        public async Task<string> StopRecordingAsync()
        {
            await _opLock.WaitAsync();
            try
            {
                if (!_isRecording) return string.Empty;

                _isRecording = false;
                bool wasPaused = _isPaused;
                _isPaused = false;
                if (wasPaused) OnPausedChanged?.Invoke(false);
                OnStatusChanged?.Invoke(false);

                if (!wasPaused) await StopSegmentAsync();

                return await Task.Run(() => FinalizeSegments(_ffmpegExe!, _segments, _currentVideoPath));
            }
            finally { _opLock.Release(); }
        }

        /// <summary>Об'єднує сегменти в підсумковий файл (один сегмент — просто перейменування).</summary>
        private static string FinalizeSegments(string ffmpegExe, List<string> segments, string finalPath)
        {
            var existing = segments.FindAll(VideoRecorderManager.IsNonEmptyFile);
            if (existing.Count == 0) return string.Empty;

            try
            {
                if (existing.Count == 1)
                {
                    File.Move(existing[0], finalPath, overwrite: true);
                    return finalPath;
                }

                string listPath = Path.ChangeExtension(finalPath, ".concat.txt");
                File.WriteAllLines(listPath, existing.ConvertAll(p => "file '" + p.Replace("'", "'\\''") + "'"));

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = $"-y -hide_banner -loglevel error -f concat -safe 0 -i \"{listPath}\" -c copy \"{finalPath}\"",
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                using (var proc = Process.Start(psi))
                {
                    string err = proc?.StandardError.ReadToEnd() ?? "";
                    proc?.WaitForExit(60000);
                    if (!string.IsNullOrWhiteSpace(err)) AppLog.Write("[MacVideoRecorder] concat: " + err);
                }

                try { File.Delete(listPath); } catch (Exception logEx) { AppLog.Warn(logEx); }

                if (VideoRecorderManager.IsNonEmptyFile(finalPath))
                {
                    foreach (var seg in existing)
                    {
                        try { File.Delete(seg); } catch (Exception logEx) { AppLog.Warn(logEx); }
                    }
                    return finalPath;
                }

                // Склеїти не вдалося — віддаємо хоча б перший шматок, решту лишаємо поруч
                AppLog.Write("[MacVideoRecorder] concat не створив файл, повертаю перший сегмент");
                return existing[0];
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "Об'єднання сегментів відео");
                return existing[0];
            }
        }

        private void OnFfmpegExited(Process proc)
        {
            // Нас цікавить лише випадок, коли ffmpeg зупинився сам посеред запису
            if (_isStarting || !_isRecording || !ReferenceEquals(proc, _ffmpegProc)) return;

            _isRecording = false;
            _isPaused = false;
            _ffmpegProc = null;
            OnStatusChanged?.Invoke(false);
            OnRecordingFailed?.Invoke(Loc.F("Video_FfmpegCrashed", GetLastLog()));
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
                return _ffmpegLog.Count == 0 ? "" : Loc.F("Video_FfmpegLog", string.Join("\n", _ffmpegLog));
            }
        }
    }

    public class FallbackVideoRecorderService : IVideoRecorderService
    {
        public bool IsRecording => false;
        public event Action<bool>? OnStatusChanged { add { } remove { } }
        public event Action<string>? OnRecordingFailed { add { } remove { } }
        public IReadOnlyList<MonitorInfo> GetDisplays() => new List<MonitorInfo>();
        public Task<RecordingStartResult> StartRecordingAsync(bool showClicks, bool recordSystemAudio, string monitorDeviceName, int? left = null, int? top = null, int? right = null, int? bottom = null, bool recordMicrophone = false)
            => Task.FromResult(RecordingStartResult.Fail(Loc.T("Video_Unsupported")));
        public Task<string> StopRecordingAsync() => Task.FromResult(string.Empty);
        public bool IsPaused => false;
        public event Action<bool>? OnPausedChanged { add { } remove { } }
        public Task<bool> PauseAsync() => Task.FromResult(false);
        public Task<bool> ResumeAsync() => Task.FromResult(false);
    }
}
