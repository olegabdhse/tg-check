using System;
using System.Windows.Forms;

namespace TgCheck.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Installer.Args = args;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}
