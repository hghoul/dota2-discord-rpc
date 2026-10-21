using Dota2RPC.Core;
using Dota2RPC.UI;

namespace Dota2RPC;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        var cli = new CommandLine(args);
        Log.Info($"Запуск {AppInfo.ProductName} {AppInfo.Version}, аргументы: {string.Join(' ', args)}");

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => ReportCrash(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => ReportCrash(e.ExceptionObject as Exception, fatal: true);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Необработанное исключение в задаче", e.Exception);
            e.SetObserved();
        };

        try
        {
            // Служебные режимы для установщика.
            if (cli.Has("write-gsi")) return SetupRunner.RunWriteGsi(cli);
            if (cli.Has("uninstall")) return SetupRunner.RunUninstall();
            if (cli.Has("exit")) return SingleInstance.SignalExitAndWait(TimeSpan.FromSeconds(8)) ? 0 : 1;
            if (cli.Has("setup")) return SetupRunner.RunSetup(cli);

            var background = cli.Has("background");
            var watch = cli.Has("watch");

            using var instance = SingleInstance.TryAcquire();
            if (instance is null)
            {
                // Программа уже работает — показываем её окно (кроме автозапуска).
                if (!background && !watch) SingleInstance.SignalShow();
                return 0;
            }

            var settings = AppSettings.Load();
            using var controller = new AppController(settings);
            controller.Start();

            var showWindow = !background && !watch;
            var page = cli.Get("page") switch { "settings" => 1, "integration" => 2, _ => 0 };
            using var tray = new TrayApp(controller, instance, showWindow, watchMode: watch, initialPage: page, justUpdated: cli.Has("updated"));
            Application.Run(tray);
            return 0;
        }
        catch (Exception ex)
        {
            ReportCrash(ex, fatal: true);
            return SetupRunner.ExitError;
        }
    }

    static void ReportCrash(Exception? ex, bool fatal)
    {
        Log.Error(fatal ? "Критическая ошибка" : "Необработанная ошибка", ex);
        try
        {
            MessageBox.Show(
                (fatal ? "Программа столкнулась с критической ошибкой и будет закрыта.\n\n" : "Произошла ошибка, программа продолжит работу.\n\n") +
                (ex?.Message ?? "неизвестная ошибка") + $"\n\nПодробности в журнале:\n{Log.FilePath}",
                AppInfo.ProductName, MessageBoxButtons.OK, fatal ? MessageBoxIcon.Error : MessageBoxIcon.Warning);
        }
        catch
        {
            // UI недоступен
        }
    }
}
