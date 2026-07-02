using System;
using System.IO;
using System.Text;
using System.Windows;
using HardwareScanner.Services;

namespace HardwareScanner
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Çökme logları çalışma dizinine değil AppData'ya yazılır
            // (çalışma dizini Program Files gibi yazma izni olmayan bir yer olabilir)
            this.DispatcherUnhandledException += (s, args) =>
            {
                string? logPath = WriteCrashLog("crash_log.txt", "Dispatcher exception: " + args.Exception);
                MessageBox.Show(
                    "Uygulama hatası oluştu:\n" + args.Exception.Message +
                    (logPath != null ? $"\n\nDetaylar: {logPath}" : ""),
                    "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                args.Handled = true;

                // Ana pencere hiç açılamadıysa Handled=true görünmez bir hayalet
                // süreç bırakır (OnLastWindowClose hiç tetiklenmez) - kapat.
                if (MainWindow == null || !MainWindow.IsLoaded)
                {
                    Shutdown(1);
                }
            };

            AppDomain.CurrentDomain.UnhandledException += (s, args) =>
            {
                string? logPath = WriteCrashLog("crash_log_domain.txt", "Domain exception: " + args.ExceptionObject);
                MessageBox.Show(
                    "Kritik hata!" + (logPath != null ? $" Log: {logPath}" : ""),
                    "Kritik Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            };
        }

        private static string? WriteCrashLog(string fileName, string content)
        {
            try
            {
                string path = Path.Combine(AppLog.LogDirectory, fileName);
                // BOM'lu UTF-8: Türkçe karakterlerin her editörde doğru görünmesi için
                File.WriteAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {content}", new UTF8Encoding(true));
                return path;
            }
            catch
            {
                return null;
            }
        }
    }
}
