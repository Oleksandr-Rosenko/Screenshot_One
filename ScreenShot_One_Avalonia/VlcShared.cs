using System;
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
            Core.Initialize();
            return new LibVLC();
        }, isThreadSafe: true);

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
