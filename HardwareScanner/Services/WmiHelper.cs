using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Management;
using System.Text;
using System.Text.Json;

namespace HardwareScanner.Services
{
    public static class WmiHelper
    {
        private static bool _preferCimFallback;

        public static List<Dictionary<string, object?>> Query(string wmiClass, string[] properties, string? scope = null)
        {
            var results = new List<Dictionary<string, object?>>();
            if (!_preferCimFallback)
            {
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

                    return results;
                }
                catch (Exception ex)
                {
                    _preferCimFallback = ex is PlatformNotSupportedException ||
                        ex.ToString().Contains("System.Management currently is only supported", StringComparison.OrdinalIgnoreCase);
                    AppLog.Write($"WMI Error for {wmiClass}: {ex.Message}");
                }
            }

            return QueryWithCim(wmiClass, properties, scope);
        }

        private static List<Dictionary<string, object?>> QueryWithCim(string wmiClass, string[] properties, string? scope)
        {
            var results = new List<Dictionary<string, object?>>();
            try
            {
                string ns = string.IsNullOrWhiteSpace(scope) ? @"root\cimv2" : scope;
                string propertyList = string.Join(", ", Array.ConvertAll(properties, QuotePowerShellString));

                string command =
                    "$ErrorActionPreference='Stop';" +
                    $"$items=@(Get-CimInstance -Namespace {QuotePowerShellString(ns)} -ClassName {QuotePowerShellString(wmiClass)} | Select-Object -Property {propertyList});" +
                    "if($items.Count -eq 0){'[]'}else{ConvertTo-Json -InputObject $items -Compress -Depth 4}";
                string encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));

                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        Arguments = "-NoProfile -ExecutionPolicy Bypass -EncodedCommand " + encodedCommand,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                proc.Start();
                var outputTask = proc.StandardOutput.ReadToEndAsync();
                var errorTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(10000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    AppLog.Write($"CIM timeout for {wmiClass}");
                    return results;
                }

                string output = outputTask.GetAwaiter().GetResult();
                string error = errorTask.GetAwaiter().GetResult();

                if (proc.ExitCode != 0)
                {
                    AppLog.Write($"CIM Error for {wmiClass}: {error}");
                    return results;
                }

                if (string.IsNullOrWhiteSpace(output)) return results;

                using var document = JsonDocument.Parse(output);
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        AddJsonRow(results, item, properties);
                    }
                }
                else if (document.RootElement.ValueKind == JsonValueKind.Object)
                {
                    AddJsonRow(results, document.RootElement, properties);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"CIM parse error for {wmiClass}: {ex.Message}");
            }

            return results;
        }

        private static void AddJsonRow(List<Dictionary<string, object?>> results, JsonElement item, string[] properties)
        {
            if (item.ValueKind != JsonValueKind.Object) return;

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (string prop in properties)
            {
                if (item.TryGetProperty(prop, out JsonElement value))
                {
                    row[prop] = ConvertJsonValue(value);
                }
                else
                {
                    row[prop] = null;
                }
            }
            results.Add(row);
        }

        private static object? ConvertJsonValue(JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString();
                case JsonValueKind.Number:
                    if (value.TryGetInt64(out long l)) return l;
                    if (value.TryGetDouble(out double d)) return d;
                    return value.ToString();
                case JsonValueKind.True:
                    return true;
                case JsonValueKind.False:
                    return false;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return null;
                default:
                    return value.ToString();
            }
        }

        private static string QuotePowerShellString(string value)
            => "'" + value.Replace("'", "''") + "'";

        public static string GetString(Dictionary<string, object?> row, string key, string defaultValue = "Bilinmiyor")
        {
            if (row != null && row.TryGetValue(key, out object? val) && val != null)
            {
                string? s = val switch
                {
                    DateTime dt => dt.ToString("o"),
                    _ => val.ToString()
                };
                return string.IsNullOrWhiteSpace(s) ? defaultValue : s.Trim();
            }
            return defaultValue;
        }

        public static bool TryParseWmiDate(string value, out DateTime result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(value)) return false;

            if (DateTime.TryParse(value, out result))
            {
                return true;
            }

            try
            {
                if (value.Length >= 14 &&
                    int.TryParse(value.Substring(0, 4), out int year) &&
                    int.TryParse(value.Substring(4, 2), out int month) &&
                    int.TryParse(value.Substring(6, 2), out int day) &&
                    int.TryParse(value.Substring(8, 2), out int hour) &&
                    int.TryParse(value.Substring(10, 2), out int minute) &&
                    int.TryParse(value.Substring(12, 2), out int second))
                {
                    result = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Local);
                    return true;
                }
            }
            catch
            {
                return false;
            }

            return false;
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
