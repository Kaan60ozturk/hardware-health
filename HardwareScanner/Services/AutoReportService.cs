using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HardwareScanner.Models;

namespace HardwareScanner.Services
{
    public class AutoReportService
    {
        private const string Unknown = "Bilinmiyor";
        private readonly string _indexPath;

        public AutoReportService()
        {
            ReportsDirectory = Path.Combine(AppContext.BaseDirectory, "raporlar");
            _indexPath = Path.Combine(ReportsDirectory, "systems.json");
        }

        public string ReportsDirectory { get; }

        public AutoReportResult SaveIfNewSystem(SystemInfo sysInfo, string textReport, string htmlReport)
        {
            try
            {
                Directory.CreateDirectory(ReportsDirectory);

                string fingerprint = BuildFingerprint(sysInfo);
                if (string.IsNullOrWhiteSpace(fingerprint))
                {
                    return AutoReportResult.Skipped(ReportsDirectory, "Sistem kimliği için yeterli veri yok.");
                }

                var index = LoadIndex();
                if (index.Systems.Any(x => x.Fingerprint.Equals(fingerprint, StringComparison.OrdinalIgnoreCase)))
                {
                    return AutoReportResult.Skipped(ReportsDirectory, "Bu sistem daha önce raporlandı.");
                }

                string displayName = BuildDisplayName(sysInfo);
                string stamp = sysInfo.ScanDate.ToString("yyyyMMdd_HHmmss");
                string slug = Slug(displayName);
                string htmlPath = UniquePath(Path.Combine(ReportsDirectory, $"{stamp}_{slug}.html"));
                string textPath = UniquePath(Path.Combine(ReportsDirectory, $"{stamp}_{slug}.txt"));

                File.WriteAllText(htmlPath, htmlReport, new UTF8Encoding(true));
                File.WriteAllText(textPath, textReport, new UTF8Encoding(true));

                index.Systems.Add(new ReportedSystem
                {
                    Fingerprint = fingerprint,
                    DisplayName = displayName,
                    FirstSeen = sysInfo.ScanDate,
                    HtmlReport = Path.GetFileName(htmlPath),
                    TextReport = Path.GetFileName(textPath)
                });
                SaveIndex(index);

                return AutoReportResult.Saved(ReportsDirectory, htmlPath, textPath);
            }
            catch (Exception ex)
            {
                AppLog.Write("Auto report error: " + ex);
                return AutoReportResult.Failed(ReportsDirectory, ex.Message);
            }
        }

        private static string BuildFingerprint(SystemInfo sysInfo)
        {
            var parts = new List<string?>
            {
                Environment.MachineName,
                sysInfo.Os.ComputerManufacturer,
                sysInfo.Os.ComputerModel,
                sysInfo.Motherboard.Manufacturer,
                sysInfo.Motherboard.Model,
                sysInfo.Os.BiosVersion,
                sysInfo.Cpu.Model,
            };

            parts.AddRange(sysInfo.Disks
                .Select(x => x.SerialNumber)
                .Where(x => !string.IsNullOrWhiteSpace(x) && !IsUnknown(x))
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));

            var normalized = parts
                .Where(x => !string.IsNullOrWhiteSpace(x) && !IsUnknown(x!))
                .Select(x => x!.Trim().ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (normalized.Count < 2) return "";

            string raw = string.Join("|", normalized);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
            return Convert.ToHexString(hash);
        }

        private static string BuildDisplayName(SystemInfo sysInfo)
        {
            string manufacturer = CleanDisplayPart(sysInfo.Os.ComputerManufacturer);
            string model = CleanDisplayPart(sysInfo.Os.ComputerModel);

            string name = string.Join(" ", new[] { manufacturer, model }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                name = CleanDisplayPart(sysInfo.Motherboard.Model);
            }
            if (string.IsNullOrWhiteSpace(name))
            {
                name = Environment.MachineName;
            }

            return name;
        }

        private ReportIndex LoadIndex()
        {
            try
            {
                if (!File.Exists(_indexPath)) return new ReportIndex();

                string json = File.ReadAllText(_indexPath, Encoding.UTF8);
                return JsonSerializer.Deserialize<ReportIndex>(json) ?? new ReportIndex();
            }
            catch (Exception ex)
            {
                AppLog.Write("Auto report index read error: " + ex.Message);
                return new ReportIndex();
            }
        }

        private void SaveIndex(ReportIndex index)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(_indexPath, JsonSerializer.Serialize(index, options), new UTF8Encoding(true));
        }

        private static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;

            string dir = Path.GetDirectoryName(path) ?? "";
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);

            for (int i = 2; i < 1000; i++)
            {
                string candidate = Path.Combine(dir, $"{name}_{i}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }

            return Path.Combine(dir, $"{name}_{Guid.NewGuid():N}{ext}");
        }

        private static string Slug(string value)
        {
            string s = value
                .Replace('ı', 'i').Replace('İ', 'I')
                .Replace('ğ', 'g').Replace('Ğ', 'G')
                .Replace('ü', 'u').Replace('Ü', 'U')
                .Replace('ş', 's').Replace('Ş', 'S')
                .Replace('ö', 'o').Replace('Ö', 'O')
                .Replace('ç', 'c').Replace('Ç', 'C');

            s = Regex.Replace(s, @"[^A-Za-z0-9]+", "_").Trim('_');
            if (string.IsNullOrWhiteSpace(s)) return "sistem";
            return s.Length <= 60 ? s : s.Substring(0, 60);
        }

        private static string CleanDisplayPart(string value)
            => IsUnknown(value) ? "" : (value ?? "").Trim();

        private static bool IsUnknown(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;

            string trimmed = value.Trim();
            return trimmed.Equals(Unknown, StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("-", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.Equals("0", StringComparison.OrdinalIgnoreCase);
        }

        private class ReportIndex
        {
            public List<ReportedSystem> Systems { get; set; } = new();
        }

        private class ReportedSystem
        {
            public string Fingerprint { get; set; } = "";
            public string DisplayName { get; set; } = "";
            public DateTime FirstSeen { get; set; }
            public string HtmlReport { get; set; } = "";
            public string TextReport { get; set; } = "";
        }
    }

    public class AutoReportResult
    {
        public bool WasSaved { get; init; }
        public bool IsFailed { get; init; }
        public string ReportsDirectory { get; init; } = "";
        public string Message { get; init; } = "";
        public string HtmlPath { get; init; } = "";
        public string TextPath { get; init; } = "";

        public static AutoReportResult Saved(string dir, string htmlPath, string textPath)
            => new()
            {
                WasSaved = true,
                ReportsDirectory = dir,
                HtmlPath = htmlPath,
                TextPath = textPath,
                Message = "Yeni sistem algılandı, rapor otomatik kaydedildi."
            };

        public static AutoReportResult Skipped(string dir, string message)
            => new() { ReportsDirectory = dir, Message = message };

        public static AutoReportResult Failed(string dir, string message)
            => new() { IsFailed = true, ReportsDirectory = dir, Message = "Otomatik rapor kaydedilemedi: " + message };
    }
}
