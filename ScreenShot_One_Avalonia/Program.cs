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
            catch (Exception ex) { AppLog.Warn(ex); }

            // Глобальні обробники: будь-яке необроблене виключення (у т.ч. з async void) потрапить у лог,
            // замість того щоб програма тихо зникла з трею.
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                if (e.ExceptionObject is Exception ex) AppLog.Error(ex, "UnhandledException");
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                AppLog.Error(e.Exception, "UnobservedTaskException");
                e.SetObserved();
            };

            AppLog.Write($"Старт Screenshot_One {typeof(Program).Assembly.GetName().Version} на {Environment.OSVersion}");

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }

        public static AppBuilder BuildAvaloniaApp()
            => AppBuilder.Configure<App>()
                .UsePlatformDetect()
                .WithInterFont()
                .LogToTrace();
    }
}
