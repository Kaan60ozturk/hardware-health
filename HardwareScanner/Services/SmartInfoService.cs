using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Principal;
using HardwareScanner.Models;
using Newtonsoft.Json.Linq;

namespace HardwareScanner.Services
{
    public class SmartInfoService : IDisposable
    {
        private readonly string tempDirectory;
        private readonly string tempSmartctlPath;
        private List<SmartctlScanDevice>? _scanDevices;
        private bool _disposed;

        public SmartInfoService()
        {
            // Güvenlik: Uygulama yönetici olarak çalıştığı için smartctl'i herkesin
            // yazabildiği %TEMP% köküne tahmin edilebilir bir isimle koymak, yetkisiz
            // bir sürecin dosyayı değiştirip yönetici olarak kod çalıştırmasına yol
            // açabilir (TOCTOU). Bu yüzden rastgele isimli ve mümkünse yalnızca
            // Yöneticiler/SYSTEM erişimli bir alt klasör kullanıyoruz.
            tempDirectory = CreateSecureTempDirectory();
            tempSmartctlPath = Path.Combine(tempDirectory, "smartctl.exe");
            ExtractSmartctl();

            // Uygulama nasıl kapanırsa kapansın geçici dosyayı temizlemeye çalış
            AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanupTempFile();
        }

        private static string CreateSecureTempDirectory()
        {
            string dir = Path.Combine(Path.GetTempPath(), "HwScanner_" + Guid.NewGuid().ToString("N"));

            // Kısıtlı ACL'i YALNIZCA gerçekten yükseltilmiş (yönetici) çalışırken uygula.
            // Yönetici değilken bu ACL, dosya sahibinin (filtrelenmiş token) kendi
            // erişimini kaybetmesine yol açar: smartctl çıkarılamaz ve klasör silinemez.
            // TOCTOU tehdidi de yalnızca yükseltilmiş süreç için geçerlidir; bu yüzden
            // yükseltilmemişken normal (sadece kullanıcının erişebildiği) klasör yeterli.
            if (!IsElevated())
            {
                Directory.CreateDirectory(dir);
                return dir;
            }

            try
            {
                var security = new DirectorySecurity();
                // Kalıtılan izinleri kapat, yalnızca Yöneticiler ve SYSTEM erişebilsin.
                // Böylece orta bütünlüklü (yönetici olmayan) bir süreç smartctl.exe'yi
                // çalıştırılmadan önce değiştiremez.
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                security.AddAccessRule(new FileSystemAccessRule(
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(dir).Create(security);
            }
            catch (Exception ex)
            {
                // ACL uygulanamazsa normal klasörle devam et (işlevsellik korunur)
                AppLog.Write("Secure temp dir ACL error: " + ex.Message);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            }
            return dir;
        }

        private static bool IsElevated()
        {
            try
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }

        private void ExtractSmartctl()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                using (Stream? stream = assembly.GetManifestResourceStream("HardwareScanner.Resources.smartctl.exe"))
                {
                    if (stream != null && stream.Length > 0)
                    {
                        using (FileStream fileStream = new FileStream(tempSmartctlPath, FileMode.Create, FileAccess.Write))
                        {
                            stream.CopyTo(fileStream);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("smartctl extract error: " + ex.Message);
            }
        }

        public List<DiskInfo> GetDisksInfo()
        {
            var disks = new List<DiskInfo>();

            // MSFT_PhysicalDisk'ten güvenilir SSD/HDD ve veriyolu bilgisi (Win8+)
            var physicalDiskMap = GetPhysicalDiskMap();

            var wmiDisks = WmiHelper.Query("Win32_DiskDrive",
                new[] { "Model", "Size", "DeviceID", "Index", "MediaType", "SerialNumber", "InterfaceType", "Status" });

            foreach (var wRow in wmiDisks)
            {
                var disk = new DiskInfo();
                disk.Model = WmiHelper.GetString(wRow, "Model");
                disk.SerialNumber = WmiHelper.GetString(wRow, "SerialNumber");

                string sizeStr = WmiHelper.GetString(wRow, "Size", "0");
                if (long.TryParse(sizeStr, out long sizeBytes) && sizeBytes > 0)
                {
                    disk.Capacity = Math.Ceiling(sizeBytes / (1000.0 * 1000.0 * 1000.0)) + " GB";
                }

                // Tür tespiti - 1. adım: WMI metin alanlarından tahmin (en zayıf yöntem)
                string mediaType = WmiHelper.GetString(wRow, "MediaType", "").ToLower();
                if (mediaType.Contains("ssd") || disk.Model.ToLower().Contains("ssd"))
                    disk.Type = "SSD";
                else if (mediaType.Contains("fixed") || mediaType.Contains("hard"))
                    disk.Type = "HDD";

                // 2. adım: MSFT_PhysicalDisk (daha güvenilir)
                string indexStr = WmiHelper.GetString(wRow, "Index", "-1");
                if (physicalDiskMap.TryGetValue(indexStr, out var pd))
                {
                    if (pd.mediaType == 4) disk.Type = "SSD";
                    else if (pd.mediaType == 3) disk.Type = "HDD";

                    disk.InterfaceType = pd.busType switch
                    {
                        3 => "ATA",
                        7 => "USB",
                        8 => "RAID",
                        10 => "SAS",
                        11 => "SATA",
                        17 => "NVMe",
                        _ => disk.InterfaceType
                    };
                }
                if (disk.InterfaceType == "Bilinmiyor")
                {
                    string wmiInterface = WmiHelper.GetString(wRow, "InterfaceType", "");
                    if (!string.IsNullOrWhiteSpace(wmiInterface) && wmiInterface != "Bilinmiyor")
                        disk.InterfaceType = wmiInterface;
                }

                string deviceId = WmiHelper.GetString(wRow, "DeviceID");

                // 3. adım: smartctl ile SMART verilerini oku (en güvenilir)
                bool smartSuccess = TryGetSmartData(deviceId, disk);

                // Eğer smartctl başarısız olduysa WMI'den sadece durum alıyoruz.
                if (!smartSuccess)
                {
                    string status = WmiHelper.GetString(wRow, "Status");
                    if (status.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        disk.StatusText = "SMART Verisi Yok (WMI: OK)";
                        disk.Status = HealthStatus.Unknown; // No explicit % health
                    }
                    else
                    {
                        disk.StatusText = "Bilinmiyor / Hata (" + status + ")";
                        disk.Status = HealthStatus.Bad;
                    }
                }

                disks.Add(disk);
            }

            return disks;
        }

        /// <summary>
        /// MSFT_PhysicalDisk sorgusundan disk indeksi -> (MediaType, BusType) haritası döner.
        /// MediaType: 3=HDD, 4=SSD. BusType: 11=SATA, 17=NVMe, 7=USB...
        /// </summary>
        private Dictionary<string, (int mediaType, int busType)> GetPhysicalDiskMap()
        {
            var map = new Dictionary<string, (int, int)>();
            var results = WmiHelper.Query("MSFT_PhysicalDisk", new[] { "DeviceId", "MediaType", "BusType" }, @"root\Microsoft\Windows\Storage");
            foreach (var row in results)
            {
                string id = WmiHelper.GetString(row, "DeviceId", "");
                int.TryParse(WmiHelper.GetString(row, "MediaType", "0"), out int mt);
                int.TryParse(WmiHelper.GetString(row, "BusType", "0"), out int bt);
                if (!string.IsNullOrEmpty(id) && id != "Bilinmiyor")
                {
                    map[id] = (mt, bt);
                }
            }
            return map;
        }

        private bool TryGetSmartData(string deviceId, DiskInfo disk)
        {
            if (!File.Exists(tempSmartctlPath) || new FileInfo(tempSmartctlPath).Length == 0) return false;

            try
            {
                // WMI DeviceID formatı genellikle \\.\PHYSICALDRIVE0 şeklindedir
                string arg = GetBestSmartctlArgument(deviceId, disk);
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = tempSmartctlPath,
                        Arguments = arg,
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                proc.Start();
                // Her iki akışı da asenkron oku: senkron ReadToEnd() süreç takılırsa
                // sonsuza dek bloklanır ve aşağıdaki zaman aşımı hiç çalışmazdı.
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    AppLog.Write($"smartctl timeout for {deviceId}");
                    return false;
                }
                // Parametresiz WaitForExit, yönlendirilen akışların boşalmasını garanti eder
                proc.WaitForExit();

                string output = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();

                // Çıkış kodu bit 0: komut satırı hatası, bit 1: cihaz açılamadı (ör. yönetici izni yok)
                if ((proc.ExitCode & 0x03) != 0)
                {
                    AppLog.Write($"smartctl failed for {deviceId} (exit code {proc.ExitCode}): {stderr}");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(output)) return false;

                JObject json = JObject.Parse(output);

                // Temel alanlar
                disk.Model = json["model_name"]?.ToString() ?? disk.Model;
                disk.SerialNumber = json["serial_number"]?.ToString() ?? disk.SerialNumber;

                // Arayüz (ATA / NVMe)
                string? protocol = json["device"]?["protocol"]?.ToString();
                if (!string.IsNullOrWhiteSpace(protocol))
                {
                    disk.InterfaceType = protocol.Equals("ATA", StringComparison.OrdinalIgnoreCase) && disk.InterfaceType == "SATA"
                        ? "SATA"
                        : protocol;
                }

                // Dönüş hızı: 0 = SSD, >0 = HDD (RPM)
                if (json["rotation_rate"] is JToken rotToken)
                {
                    int rpm = rotToken.Value<int>();
                    disk.Type = rpm == 0 ? "SSD" : "HDD";
                }

                // Sıcaklık
                if (json["temperature"]?["current"] is JToken tempToken)
                {
                    disk.Temperature = tempToken.Value<int>();
                }

                // Power On Hours ve Cycles
                if (json["power_on_time"]?["hours"] is JToken pohToken)
                {
                    disk.PowerOnHours = pohToken.Value<long>();
                }
                if (json["power_cycle_count"] is JToken pccToken)
                {
                    disk.PowerCycles = pccToken.Value<long>();
                }

                // SSD Sağlığı (NVMe)
                if (json["nvme_smart_health_information_log"] is JToken log)
                {
                    disk.Type = "NVMe SSD";
                    if (disk.InterfaceType == "Bilinmiyor") disk.InterfaceType = "NVMe";

                    if (disk.Temperature < 0 && log["temperature"] is JToken nvmeTempToken)
                    {
                        disk.Temperature = nvmeTempToken.Value<int>();
                    }
                    if (disk.PowerOnHours < 0 && log["power_on_hours"] is JToken nvmeHoursToken)
                    {
                        disk.PowerOnHours = nvmeHoursToken.Value<long>();
                    }
                    if (log["percentage_used"] is JToken usedToken)
                    {
                        int used = usedToken.Value<int>();
                        disk.HealthPercent = Math.Max(0, 100 - used);
                    }
                    // NVMe veri birimi 512.000 bayttır (1000 x 512); "TB" etiketi kullandığımız
                    // için ondalık TB'ye (10^12 bayt) çeviriyoruz
                    if (log["data_units_written"] is JToken writtenToken)
                    {
                        long units = writtenToken.Value<long>();
                        disk.Tbw = Math.Round((units * 512000.0) / 1_000_000_000_000.0, 2);
                    }
                    if (log["data_units_read"] is JToken readToken)
                    {
                        long units = readToken.Value<long>();
                        disk.DataRead = Math.Round((units * 512000.0) / 1_000_000_000_000.0, 2);
                    }
                }

                // ATA SMART özellikleri (SSD/HDD)
                if (json["ata_smart_attributes"]?["table"] is JArray table)
                {
                    foreach (var item in table)
                    {
                        string? name = item["name"]?.ToString()?.ToLower();
                        // Raw değerler int sınırını aşabilir (ör. toplam yazılan LBA) - long kullan
                        long rawVal = item["raw"]?["value"]?.Value<long>() ?? 0;
                        int val = item["value"]?.Value<int>() ?? 0;

                        if (name == "power_on_hours") disk.PowerOnHours = rawVal;
                        if (name == "power_cycle_count") disk.PowerCycles = rawVal;

                        // SATA SSD'lerde toplam yazılan veri (ondalık TB = 10^12 bayt)
                        if (name == "total_lbas_written" && rawVal > 0 && disk.Tbw < 0)
                        {
                            // LBA başına 512 bayt varsayımı (en yaygın)
                            disk.Tbw = Math.Round((rawVal * 512.0) / 1_000_000_000_000.0, 2);
                        }
                        if (name == "total_lbas_read" && rawVal > 0 && disk.DataRead < 0)
                        {
                            disk.DataRead = Math.Round((rawVal * 512.0) / 1_000_000_000_000.0, 2);
                        }

                        // SSD Health (bazı SSD'ler "percent_lifetime_remain" gibi özelliklere sahip)
                        // Not: Her zaman NORMALİZE değeri (val) kullan. Ham değer üretici bazlıdır;
                        // ör. Samsung Wear_Leveling_Count ham değeri silme döngüsü SAYISIDIR ve
                        // 100'den çıkarmak sağlıklı diski %0 gösterir. Normalize değer 100'den başlar.
                        if (name != null && (name.Contains("wear") || name.Contains("life") || name.Contains("health")))
                        {
                            if (val > 0 && val <= 100)
                                disk.HealthPercent = val;
                        }

                        // Bad Sectors
                        if (name == "reallocated_sector_ct" || name == "current_pending_sector" || name == "offline_uncorrectable")
                        {
                            if (rawVal > 0) disk.HasBadSectors = true;
                        }
                    }
                }

                // SMART Durumu (Geçti/Kaldı)
                bool passed = json["smart_status"]?["passed"]?.Value<bool>() ?? true;

                if (!passed)
                {
                    disk.StatusText = "SMART Başarısız! (Arıza Riski)";
                    disk.Status = HealthStatus.Bad;
                }
                else if (disk.HasBadSectors)
                {
                    // Kötü sektör varsa sağlık yüzdesi iyi görünse bile en fazla "Uyarı" olabilir
                    disk.StatusText = disk.HealthPercent != -1
                        ? $"Kötü Sektör Var! (Sağlık: %{disk.HealthPercent})"
                        : "Kötü Sektör(ler) Tespit Edildi!";
                    disk.Status = disk.HealthPercent != -1 && disk.HealthPercent <= 50
                        ? HealthStatus.Bad
                        : HealthStatus.Warning;
                }
                else if (disk.HealthPercent != -1)
                {
                    disk.StatusText = $"Sağlık: %{disk.HealthPercent}";
                    if (disk.HealthPercent > 80) disk.Status = HealthStatus.Good;
                    else if (disk.HealthPercent > 50) disk.Status = HealthStatus.Warning;
                    else disk.Status = HealthStatus.Bad;
                }
                else
                {
                    disk.StatusText = "İyi Durumda";
                    disk.Status = HealthStatus.Good;
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"smartctl parse error for {deviceId}: {ex.Message}");
                return false;
            }
        }

        private string GetBestSmartctlArgument(string deviceId, DiskInfo disk)
        {
            bool likelyNvme = IsLikelyNvme(disk);
            int? physicalIndex = TryGetPhysicalDriveIndex(deviceId);

            foreach (var scanDevice in GetSmartctlScanDevices())
            {
                bool indexMatches = physicalIndex.HasValue && scanDevice.PhysicalDriveIndex == physicalIndex.Value;
                bool typeMatches = !physicalIndex.HasValue && likelyNvme && IsNvmeText(scanDevice.Protocol + " " + scanDevice.Type);

                if (indexMatches || typeMatches)
                {
                    string deviceType = !string.IsNullOrWhiteSpace(scanDevice.Type)
                        ? scanDevice.Type
                        : likelyNvme ? "nvme" : "";
                    return BuildSmartctlArgument(scanDevice.Name, deviceType);
                }
            }

            if (physicalIndex is int index && index >= 0 && index < 26)
            {
                string smartctlAlias = "/dev/sd" + (char)('a' + index);
                return BuildSmartctlArgument(smartctlAlias, likelyNvme ? "nvme" : "");
            }

            return BuildSmartctlArgument(deviceId, likelyNvme ? "nvme" : "");
        }

        private static string BuildSmartctlArgument(string deviceName, string? deviceType)
        {
            return string.IsNullOrWhiteSpace(deviceType)
                ? $"-a -j \"{deviceName}\""
                : $"-a -j -d {deviceType} \"{deviceName}\"";
        }

        private List<SmartctlScanDevice> GetSmartctlScanDevices()
        {
            if (_scanDevices != null) return _scanDevices;

            _scanDevices = new List<SmartctlScanDevice>();

            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = tempSmartctlPath,
                        Arguments = "--scan-open -j",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true
                    }
                };

                proc.Start();
                var stdoutTask = proc.StandardOutput.ReadToEndAsync();
                var stderrTask = proc.StandardError.ReadToEndAsync();

                if (!proc.WaitForExit(30000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    AppLog.Write("smartctl scan timeout");
                    return _scanDevices;
                }

                proc.WaitForExit();

                string output = stdoutTask.GetAwaiter().GetResult();
                string stderr = stderrTask.GetAwaiter().GetResult();
                if (string.IsNullOrWhiteSpace(output))
                {
                    AppLog.Write("smartctl scan empty output: " + stderr);
                    return _scanDevices;
                }

                JObject json = JObject.Parse(output);
                if (json["devices"] is not JArray devices)
                {
                    return _scanDevices;
                }

                foreach (var item in devices)
                {
                    string name = item["name"]?.ToString() ?? "";
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    string openError = item["open_error"]?.ToString() ?? "";
                    _scanDevices.Add(new SmartctlScanDevice
                    {
                        Name = name,
                        Type = item["type"]?.ToString() ?? "",
                        Protocol = item["protocol"]?.ToString() ?? "",
                        PhysicalDriveIndex = TryGetPhysicalDriveIndex(openError) ?? TryGetSmartctlAliasIndex(name)
                    });
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("smartctl scan error: " + ex.Message);
            }

            return _scanDevices;
        }

        private static bool IsLikelyNvme(DiskInfo disk)
        {
            return IsNvmeText(disk.InterfaceType)
                || IsNvmeText(disk.Type)
                || IsNvmeText(disk.Model);
        }

        private static bool IsNvmeText(string? value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && value.IndexOf("NVMe", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int? TryGetPhysicalDriveIndex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;

            const string marker = "PHYSICALDRIVE";
            int markerIndex = value.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex < 0) return null;

            int start = markerIndex + marker.Length;
            int end = start;
            while (end < value.Length && char.IsDigit(value[end])) end++;

            return int.TryParse(value.Substring(start, end - start), out int index) ? index : null;
        }

        private static int? TryGetSmartctlAliasIndex(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("/dev/sd", StringComparison.OrdinalIgnoreCase))
                return null;

            char suffix = value[value.Length - 1];
            if (suffix >= 'a' && suffix <= 'z') return suffix - 'a';
            if (suffix >= 'A' && suffix <= 'Z') return suffix - 'A';
            return null;
        }

        private sealed class SmartctlScanDevice
        {
            public string Name { get; init; } = "";
            public string Type { get; init; } = "";
            public string Protocol { get; init; } = "";
            public int? PhysicalDriveIndex { get; init; }
        }

        private void CleanupTempFile()
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch { }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CleanupTempFile();
            GC.SuppressFinalize(this);
        }

        ~SmartInfoService()
        {
            CleanupTempFile();
        }
    }
}
