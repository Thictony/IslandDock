using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace IslandDock;

internal static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, @"Local\IslandDock.SingleInstance", out bool created);
        if (!created) return;

        // An older build launched from elsewhere hands over to the newer installed copy.
        string installed = Startup.InstalledPath;
        if (Environment.ProcessPath is string self && File.Exists(installed)
            && !string.Equals(Path.GetFullPath(self), installed, StringComparison.OrdinalIgnoreCase)
            && File.GetLastWriteTimeUtc(installed) > File.GetLastWriteTimeUtc(self))
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installed) { UseShellExecute = true });
            return;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // Docked windows are children of our host windows and would be destroyed with them, so always give them back.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Islands.UndockAll();
        app.DispatcherUnhandledException += (_, e) =>
        {
            Islands.UndockAll();
            e.Handled = true;
        };
        app.SessionEnding += (_, _) =>
        {
            Islands.UndockAll();
            Notifications.Stop(); // the pop-ups stay on if the island doesn't start with Windows next time
        };

        Islands.Start();
        app.Run();
    }
}
