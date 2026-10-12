using System.Diagnostics;
using System.Threading;
using System.Windows;

namespace NexusTyper.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        const string mutexName = "NexusTyper_SingleInstance_AppMutex";
        _mutex = new Mutex(true, mutexName, out var isNewInstance);
        if (!isNewInstance)
        {
            try
            {
                var current = Process.GetCurrentProcess();
                var processNames = new[] { current.ProcessName, "Nexus", "NexusTyper" };
                foreach (var name in processNames)
                {
                    foreach (var process in Process.GetProcessesByName(name))
                    {
                        if (process.Id != current.Id)
                        {
                            try
                            {
                                process.Kill();
                                process.WaitForExit(500);
                            }
                            catch
                            {
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore process cleanup errors
            }
        }

        base.OnStartup(e);
    }
}
