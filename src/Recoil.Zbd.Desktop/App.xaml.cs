using System.IO;
using System.Windows;

namespace Recoil.Zbd.Desktop;

public partial class App : Application
{
    public bool ProcessCommandLine { get; init; } = true;
    private void OnStartup(object sender, StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, error) =>
        {
            string log = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "error.log");
            try { Directory.CreateDirectory(Path.GetDirectoryName(log)!); File.AppendAllText(log, $"{DateTime.UtcNow:O}\n{error.Exception}\n"); } catch (IOException) { }
            MessageBox.Show("The operation could not finish.\n\n" + error.Exception.Message + "\n\nDetails: " + log, "zStudio", MessageBoxButton.OK, MessageBoxImage.Error);
            error.Handled = true;
        };
        MainWindow window = new(); MainWindow = window; window.Show();
        if (ProcessCommandLine && e.Args.Length > 0) window.OpenStartupPath(e.Args[0]);
    }
}
