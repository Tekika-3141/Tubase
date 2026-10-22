using System.Windows;
using System.Windows.Interop;
using Tubase.Audio;

namespace Tubase;

public partial class App : Application
{
    private static string LogPath => Path.Combine(Path.GetTempPath(), "Tubase-startup.log");
    private static void Log(string value)
    {
        try { File.AppendAllText(LogPath, DateTime.Now.ToString("O") + " " + value + Environment.NewLine); } catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) => { Log("DISPATCHER ERROR " + args.Exception); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log("UNHANDLED " + args.ExceptionObject);
        Exit += (_, _) => Log("EXIT");
        base.OnStartup(e);
        if (e.Args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
        {
            var success = TubaseSmokeTests.Run();
            Environment.ExitCode = success ? 0 : 1;
            Shutdown();
            return;
        }

        try
        {
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
            window.Activate();
            Log("SHOW visible=" + window.IsVisible + " handle=" + new WindowInteropHelper(window).Handle);
        }
        catch (Exception ex)
        {
            Log("STARTUP ERROR " + ex);
            throw;
        }
    }
}
