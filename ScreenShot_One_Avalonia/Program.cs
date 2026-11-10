using Avalonia;
using System;
using Velopack;

namespace ScreenShot_One_Avalonia
{
    internal class Program
    {
        [STAThread]
        public static void Main(string[] args)
        {
            try
            {
                // Хук Velopack для швидкої обробки подій встановлення/оновлення
                VelopackApp.Build().Run();
            }
            catch { }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}