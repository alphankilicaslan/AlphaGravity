using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace EnterpriseAIApp;

public partial class App : Application
{
    public App()
    {
        this.DispatcherUnhandledException += (s, e) =>
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt"), $"[Dispatcher Error]: {e.Exception}\n{e.Exception.StackTrace}"); } catch { }
            MessageBox.Show($"Uygulama Hatası: {e.Exception.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            try { File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt"), $"[AppDomain Error]: {e.ExceptionObject}"); } catch { }
        };
    }
}

