namespace Poe2Verlay;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Length > 0 && args[0].StartsWith("--", StringComparison.Ordinal))
            return SelfTest.Run(args);

        using var mutex = new Mutex(true, "poe2verlay-single-instance", out var first);
        if (!first)
        {
            MessageBox.Show("poe2verlay is already running – look for its icon in the system tray.", "poe2verlay");
            return 0;
        }

        Application.ThreadException += (_, e) => Log.Write("ui exception: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("fatal: " + e.ExceptionObject);
        Application.Run(new TrayApp());
        return 0;
    }
}
