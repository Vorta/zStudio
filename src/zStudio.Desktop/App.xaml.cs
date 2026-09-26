using System.IO;
using System.Windows;

namespace Recoil.Zbd.Desktop;

public partial class App : Application
{
    public bool ProcessCommandLine { get; init; } = true;
    private async void OnStartup(object sender, StartupEventArgs e)
    {
        if (ProcessCommandLine && e.Args.FirstOrDefault() == "--mcp")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            string? instance = e.Args.Length == 3 && e.Args[1] == "--instance" ? e.Args[2] : null;
            if (e.Args.Length != 1 && instance == null) { await Console.Error.WriteLineAsync("Usage: zStudio.exe --mcp [--instance id]"); Shutdown(2); return; }
            int result = await Task.Run(() => Recoil.Zbd.Mcp.LocalMcpHost.ConnectStdioAsync(Environment.ProcessPath!, instance, () => StudioSettings.Load().McpEnabled, typeof(App).Assembly.GetName().Version!.ToString()));
            Shutdown(result); return;
        }
        DispatcherUnhandledException += (_, error) =>
        {
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "error.log");
            try { Directory.CreateDirectory(Path.GetDirectoryName(log)!); File.AppendAllText(log, $"{DateTime.UtcNow:O}\n{error.Exception}\n"); } catch (IOException) { }
            MessageBox.Show("The operation could not finish.\n\n" + error.Exception.Message + "\n\nDetails: " + log, "zStudio", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        MainWindow window = new(); MainWindow = window; window.Show();
        if (ProcessCommandLine) window.InitializeMcp();
        if (ProcessCommandLine && e.Args.Length > 0 && e.Args[0] != "--mcp-host") window.OpenStartupPath(e.Args[0]);
    }
}
