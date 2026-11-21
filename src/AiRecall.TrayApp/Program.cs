using AiRecall.Core.Configuration;

namespace AiRecall.TrayApp;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\AiRecall.TrayApp.SingleInstance";

    [STAThread]
    private static int Main(string[] args)
    {
        AppPaths.UseExecutableDirectory();
        if (args is ["--verify-package"]) return PackageSmokeTest.Run();

        using var mutex = new System.Threading.Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            // Zweite Instanz: Bring-to-Front-Signal an erste Instanz.
            // Implementierung folgt mit Named-Window-Message-IPC in Schritt 4 (Spec 0006 §Single-Instance).
            BringToFrontOfExistingInstance();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAppContext(mutex));
        return 0;
    }

    private static void BringToFrontOfExistingInstance()
    {
        // Stub: Suche via FindWindow("AiRecall.TrayApp.MainWindow") und SetForegroundWindow.
        // Vollständige IPC folgt in Schritt 4.
    }
}