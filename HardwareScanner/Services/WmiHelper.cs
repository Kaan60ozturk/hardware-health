using System;
using System.Collections.Generic;
using System.IO;
using System.Management;

namespace HardwareScanner.Services
{
    public static class WmiHelper
    {
        public static List<Dictionary<string, object?>> Query(string wmiClass, string[] properties, string? scope = null)
        {
            var results = new List<Dictionary<string, object?>>();
            try
            {
                string queryStr = $"SELECT {string.Join(", ", properties)} FROM {wmiClass}";
                ManagementObjectSearcher searcher;
                if (!string.IsNullOrEmpty(scope))
                {
                    searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(queryStr));
                }
                else
                {
                    searcher = new ManagementObjectSearcher(queryStr);
                }

                using (searcher)
                {
                    using (var collection = searcher.Get())
                    {
                        foreach (var obj in collection)
                        {
                            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            foreach (var prop in properties)
                            {
                                try
                                {
                                    row[prop] = obj[prop];
                                }
                                catch
                                {
                                    row[prop] = null;
                                }
                            }
                            results.Add(row);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Hata durumunda boş liste döner, program çökmez
                AppLog.Write($"WMI Error for {wmiClass}: {ex}");
            }
            return results;
        }

        public static string GetString(Dictionary<string, object?> row, string key, string defaultValue = "Bilinmiyor")
        {
            if (row != null && row.TryGetValue(key, out object? val) && val != null)
            {
                string? s = val.ToString();
                return string.IsNullOrWhiteSpace(s) ? defaultValue : s.Trim();
            }
            return defaultValue;
        }
    }

    /// <summary>
    /// Uygulama loglarını kullanıcının yazma izni olan AppData klasörüne yazar.
    /// (Çalışma dizini Program Files gibi korumalı bir yer olabilir.)
    /// </summary>
    public static class AppLog
    {
        private static readonly object _lock = new();

        public static string LogDirectory
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "HardwareScanner");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (_lock)
                {
                    // BOM yalnızca dosya ilk oluşturulurken yazılır (append konumu > 0 ise eklenmez)
                    File.AppendAllText(
                        Path.Combine(LogDirectory, "app.log"),
                        $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}",
                        new System.Text.UTF8Encoding(true));
                }
            }
            catch
            {
                // Loglama asla uygulamayı düşürmemeli
            }
        }
    }
}
