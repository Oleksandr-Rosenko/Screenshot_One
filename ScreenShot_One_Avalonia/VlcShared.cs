using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using LibVLCSharp.Shared;

namespace ScreenShot_One_Avalonia
{
    /// <summary>
    /// Один екземпляр LibVLC на всю програму. Раніше кожне відкриття відеоредактора викликало
    /// Core.Initialize() + new LibVLC(): VLC щоразу сканував і завантажував усі свої плагіни (~0.5–1 с затримки).
    /// Тепер це відбувається один раз — у фоні після старту програми (Prewarm) або при першому відкритті.
    /// LibVLC потокобезпечний для створення MediaPlayer/Media з різних вікон.
    /// </summary>
    public static class VlcShared
    {
        private static readonly Lazy<LibVLC> _instance = new Lazy<LibVLC>(() =>
        {
            try
            {
                string? macDir = MacLibVlcDirectory();
                if (macDir != null)
                {
                    // Плагіни VLC лежать поруч: lib/vlc/plugins
                    string plugins = Path.Combine(macDir, "vlc", "plugins");
                    if (Directory.Exists(plugins)) Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", plugins);
                }
                Core.Initialize(macDir);
                var vlc = new LibVLC();
                AppLog.Write($"[VLC] готовий: {vlc.Version}{(macDir != null ? $" ({macDir})" : "")}");
                return vlc;
            }
            catch (Exception ex)
            {
                AppLog.Error(ex, "[VLC] не вдалося ініціалізувати LibVLC");
                throw;
            }
        }, isThreadSafe: true);

        /// <summary>
        /// macOS: каталог з libvlc.dylib для поточної архітектури. LibVLCSharp 3.10 сам шукає лише libvlc/osx-x64/lib,
        /// тож на Apple Silicon (libvlc/osx-arm64/lib) плеєр не стартував і відеоредактор вічно показував «Обробка…».
        /// </summary>
        public static string? MacLibVlcDirectory()
        {
            if (!OperatingSystem.IsMacOS()) return null;
            string rid = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
            string dir = Path.Combine(AppContext.BaseDirectory, "libvlc", rid, "lib");
            if (File.Exists(Path.Combine(dir, "libvlc.dylib"))) return dir;
            AppLog.Write($"[VLC] libvlc.dylib не знайдено в {Path.Combine(AppContext.BaseDirectory, "libvlc")}");
            return null;
        }

        public static LibVLC Instance => _instance.Value;

        /// <summary>Ініціалізує LibVLC у фоновому потоці, щоб перше відкриття відео було миттєвим.</summary>
        public static void Prewarm()
        {
            Task.Run(() =>
            {
                try { _ = Instance; }
                catch (Exception ex) { AppLog.Warn(ex); }
            });
        }

        /// <summary>Ставить новий Media у плеєр і звільняє попередній (інакше він лишався в нативній пам'яті).</summary>
        public static void ReplaceMedia(MediaPlayer player, Media? media)
        {
            var old = player.Media;
            player.Media = media;
            if (old != null && !ReferenceEquals(old, media))
            {
                try { old.Dispose(); } catch (Exception ex) { AppLog.Warn(ex); }
            }
        }
    }
}
