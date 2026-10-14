using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace AudiobookPlayer;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnUnhandled;
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Audiobook Player");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"), $"{DateTime.Now:u}\n{e.Exception}\n\n");
        }
        catch { }
        e.Handled = true;   // keep playing; the error is in error.log
    }
}
