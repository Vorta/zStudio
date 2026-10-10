namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private static string Bounded(string text, int maximum = 1024) => text.Length <= maximum ? text : text[..maximum] + "…";
}
