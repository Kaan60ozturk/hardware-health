using System;
using System.Collections.Generic;
using System.Management;
using HardwareScanner.Models;
using Microsoft.Win32;

namespace HardwareScanner.Services
{
    public class HardwareInfoService
    {
        /// <summary>
        /// WMI üzerinden cihaz durumunu kontrol eder.
        /// Not: ConfigManagerErrorCode her WMI sınıfında yoktur (ör. Win32_BaseBoard,
        /// Win32_PhysicalMemory) — olmayan sınıfta sorgulamak "Invalid query" hatası verir.
        /// </summary>
        private (string text, HealthStatus status) CheckHealth(string queryClass, bool hasConfigManagerErrorCode)
        {
            var props = hasConfigManagerErrorCode
                ? new[] { "ConfigManagerErrorCode", "Status" }
                : new[] { "Status" };

            var results = WmiHelper.Query(queryClass, props);
            foreach (var row in results)
            {
                string status = WmiHelper.GetString(row, "Status", "OK");

                if (status.Equals("Error", StringComparison.OrdinalIgnoreCase))
                    return ("Hata Bildiriyor", HealthStatus.Bad);

                if (status.Equals("Degraded", StringComparison.OrdinalIgnoreCase) ||
                    status.Equals("Pred Fail", StringComparison.OrdinalIgnoreCase))
                    return ($"Uyarı: {status}", HealthStatus.Warning);

                if (hasConfigManagerErrorCode)
                {
                    string errCodeStr = WmiHelper.GetString(row, "ConfigManagerErrorCode", "0");
                    if (int.TryParse(errCodeStr, out int errCode) && errCode != 0)
                        return ($"Hata Kodu: {errCode}", HealthStatus.Bad);
                }
            }
            return ("Sağlam (Hata Yok)", HealthStatus.Good);
        }

        public CpuInfo GetCpuInfo()
        {
            var cpu = new CpuInfo();
            var results = WmiHelper.Query("Win32_Processor", new[] { "Name", "NumberOfCores", "NumberOfLogicalProcessors", "MaxClockSpeed" });
            if (results.Count > 0)
            {
                var row = results[0];
                cpu.Model = WmiHelper.GetString(row, "Name");
                if (int.TryParse(WmiHelper.GetString(row, "NumberOfCores", "0"), out int cores)) cpu.Cores = cores;
                if (int.TryParse(WmiHelper.GetString(row, "NumberOfLogicalProcessors", "0"), out int logical)) cpu.LogicalProcessors = logical;

                string speedStr = WmiHelper.GetString(row, "MaxClockSpeed", "0");
                if (double.TryParse(speedStr, out double speed))
                {
                    cpu.Speed = (speed / 1000.0).ToString("0.00") + " GHz";
                }
            }
            var h = CheckHealth("Win32_Processor", hasConfigManagerErrorCode: true);
            cpu.HealthText = h.text;
            cpu.Health = h.status;
            return cpu;
        }

        public MotherboardInfo GetMotherboardInfo()
        {
            var mb = new MotherboardInfo();
            var results = WmiHelper.Query("Win32_BaseBoard", new[] { "Manufacturer", "Product" });
            if (results.Count > 0)
            {
                var row = results[0];
                mb.Manufacturer = WmiHelper.GetString(row, "Manufacturer");
                mb.Model = WmiHelper.GetString(row, "Product");
            }
            // Win32_BaseBoard'da ConfigManagerErrorCode yoktur, sadece Status kontrol edilir
            var h = CheckHealth("Win32_BaseBoard", hasConfigManagerErrorCode: false);
            mb.HealthText = h.text;
            mb.Health = h.status;
            return mb;
        }

        public List<GpuInfo> GetGpuInfo()
        {
            var gpus = new List<GpuInfo>();
            var registryVram = GetGpuVramFromRegistry();

            var results = WmiHelper.Query("Win32_VideoController", new[] { "Name", "AdapterRAM", "ConfigManagerErrorCode", "Status" });
            foreach (var row in results)
            {
                var gpu = new GpuInfo();
                gpu.Model = WmiHelper.GetString(row, "Name");

                // Öncelik: Registry'deki qwMemorySize (64-bit, 4GB üstünü doğru raporlar).
                // WMI AdapterRAM 32-bit olduğu için 4GB ve üzeri kartlarda yanlış değer verir.
                long ramBytes = 0;
                if (registryVram.TryGetValue(gpu.Model, out long qwSize) && qwSize > 0)
                {
                    ramBytes = qwSize;
                }
                else
                {
                    string ramStr = WmiHelper.GetString(row, "AdapterRAM", "0");
                    long.TryParse(ramStr, out ramBytes);
                }

                if (ramBytes > 0)
                {
                    double ramGb = ramBytes / (1024.0 * 1024.0 * 1024.0);
                    if (ramGb >= 1)
                        gpu.VRam = Math.Round(ramGb) + " GB";
                    else
                        gpu.VRam = (ramBytes / (1024.0 * 1024.0)).ToString("0") + " MB";
                }
                else
                {
                    gpu.VRam = "Bilinmiyor";
                }

                string status = WmiHelper.GetString(row, "Status", "OK");
                string errCodeStr = WmiHelper.GetString(row, "ConfigManagerErrorCode", "0");
                if (status.Equals("Error", StringComparison.OrdinalIgnoreCase) || (int.TryParse(errCodeStr, out int errCode) && errCode != 0))
                {
                    gpu.HealthText = $"Hata Kodu: {errCodeStr}";
                    gpu.Health = HealthStatus.Bad;
                }
                else
                {
                    gpu.HealthText = "Sağlam (Hata Yok)";
                    gpu.Health = HealthStatus.Good;
                }
                gpus.Add(gpu);
            }
            return gpus;
        }

        /// <summary>
        /// Ekran kartı sürücü sınıfı registry anahtarından 64-bit VRAM boyutunu okur.
        /// Dönen sözlük: DriverDesc (WMI Name ile aynı) -> bayt cinsinden VRAM.
        /// </summary>
        private Dictionary<string, long> GetGpuVramFromRegistry()
        {
            var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            const string displayClassKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            try
            {
                using var classKey = Registry.LocalMachine.OpenSubKey(displayClassKey);
                if (classKey == null) return map;

                foreach (string subName in classKey.GetSubKeyNames())
                {
                    // Alt anahtarlar "0000", "0001" ... şeklindedir
                    if (subName.Length != 4 || !int.TryParse(subName, out _)) continue;

                    try
                    {
                        using var sub = classKey.OpenSubKey(subName);
                        if (sub == null) continue;

                        string? desc = sub.GetValue("DriverDesc") as string;
                        if (string.IsNullOrWhiteSpace(desc)) continue;

                        long bytes = 0;
                        object? qw = sub.GetValue("HardwareInformation.qwMemorySize");
                        if (qw is long l) bytes = l;
                        else if (qw is int i) bytes = (uint)i;
                        else if (qw is byte[] qb && qb.Length >= 8) bytes = BitConverter.ToInt64(qb, 0);

                        if (bytes <= 0)
                        {
                            object? dw = sub.GetValue("HardwareInformation.MemorySize");
                            if (dw is int di) bytes = (uint)di;
                            else if (dw is byte[] db && db.Length >= 4) bytes = BitConverter.ToUInt32(db, 0);
                        }

                        if (bytes > 0 && (!map.TryGetValue(desc, out long existing) || bytes > existing))
                        {
                            map[desc] = bytes;
                        }
                    }
                    catch
                    {
                        // Tek bir alt anahtar okunamazsa diğerlerine devam et
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("GPU registry read error: " + ex.Message);
            }
            return map;
        }

        public RamInfo GetRamInfo()
        {
            var ram = new RamInfo();
            var memResults = WmiHelper.Query("Win32_PhysicalMemory", new[] { "Capacity", "Speed", "ConfiguredClockSpeed", "MemoryType", "SMBIOSMemoryType" });
            if (memResults.Count == 0)
            {
                // ConfiguredClockSpeed/SMBIOSMemoryType Win10 öncesinde yoktur ve
                // sorgunun tamamını geçersiz kılar - temel özelliklerle tekrar dene
                memResults = WmiHelper.Query("Win32_PhysicalMemory", new[] { "Capacity", "Speed", "MemoryType" });
            }
            long totalBytes = 0;
            int count = 0;
            string speed = "Bilinmiyor";
            string type = "Bilinmiyor";

            foreach (var row in memResults)
            {
                count++;
                if (long.TryParse(WmiHelper.GetString(row, "Capacity", "0"), out long capacity))
                {
                    totalBytes += capacity;
                }
                if (speed == "Bilinmiyor")
                {
                    // ConfiguredClockSpeed gerçek çalışma hızıdır; yoksa nominal Speed kullanılır
                    string spd = WmiHelper.GetString(row, "ConfiguredClockSpeed", "0");
                    if (spd == "0" || spd == "Bilinmiyor") spd = WmiHelper.GetString(row, "Speed", "0");
                    if (spd != "0" && spd != "Bilinmiyor") speed = spd + " MHz";
                }

                if (type == "Bilinmiyor")
                {
                    int smbiosType = 0;
                    int.TryParse(WmiHelper.GetString(row, "SMBIOSMemoryType", "0"), out smbiosType);
                    int memType = 0;
                    int.TryParse(WmiHelper.GetString(row, "MemoryType", "0"), out memType);

                    int actualType = smbiosType > 0 ? smbiosType : memType;
                    type = actualType switch
                    {
                        20 => "DDR",
                        21 => "DDR2",
                        24 => "DDR3",
                        26 => "DDR4",
                        34 => "DDR5",
                        35 => "DDR5",
                        _ => "Bilinmiyor"
                    };
                }
            }

            double totalGb = totalBytes / (1024.0 * 1024.0 * 1024.0);
            ram.TotalSize = totalGb > 0 ? Math.Round(totalGb) + " GB" : "Bilinmiyor";
            ram.Speed = speed;
            ram.Type = type;

            // Anakarttaki toplam yuva sayısını da göster (ör. "2/4 Yuva Dolu")
            int totalSlots = 0;
            var arrayResults = WmiHelper.Query("Win32_PhysicalMemoryArray", new[] { "MemoryDevices" });
            if (arrayResults.Count > 0 && int.TryParse(WmiHelper.GetString(arrayResults[0], "MemoryDevices", "0"), out int slots))
            {
                totalSlots = slots;
            }
            ram.SlotsInfo = totalSlots > 0 ? $"{count}/{totalSlots} Yuva Dolu" : $"{count} Yuva Dolu";

            // Win32_PhysicalMemory'de ConfigManagerErrorCode yoktur, sadece Status kontrol edilir
            var h = CheckHealth("Win32_PhysicalMemory", hasConfigManagerErrorCode: false);
            ram.HealthText = h.text;
            ram.Health = h.status;

            return ram;
        }

        public BatteryInfo GetBatteryInfo()
        {
            var battery = new BatteryInfo();

            var staticResults = WmiHelper.Query("BatteryStaticData", new[] { "DesignedCapacity" }, @"root\wmi");
            var capacityResults = WmiHelper.Query("BatteryFullChargedCapacity", new[] { "FullChargedCapacity" }, @"root\wmi");

            if (staticResults.Count > 0 && capacityResults.Count > 0)
            {
                battery.IsPresent = true;
                if (uint.TryParse(WmiHelper.GetString(staticResults[0], "DesignedCapacity", "0"), out uint design)) battery.DesignCapacity = design;
                if (uint.TryParse(WmiHelper.GetString(capacityResults[0], "FullChargedCapacity", "0"), out uint full)) battery.FullChargeCapacity = full;
            }
            else
            {
                // Fallback to Win32_Battery
                var results = WmiHelper.Query("Win32_Battery", new[] { "DesignCapacity", "FullChargeCapacity", "BatteryStatus" });
                if (results.Count > 0)
                {
                    battery.IsPresent = true;
                    var row = results[0];
                    if (uint.TryParse(WmiHelper.GetString(row, "DesignCapacity", "0"), out uint design)) battery.DesignCapacity = design;
                    if (uint.TryParse(WmiHelper.GetString(row, "FullChargeCapacity", "0"), out uint full)) battery.FullChargeCapacity = full;
                }
            }

            // Şarj döngüsü sayısı (her üretici bildirmez)
            if (battery.IsPresent)
            {
                var cycleResults = WmiHelper.Query("BatteryCycleCount", new[] { "CycleCount" }, @"root\wmi");
                if (cycleResults.Count > 0 && int.TryParse(WmiHelper.GetString(cycleResults[0], "CycleCount", "-1"), out int cycles) && cycles > 0)
                {
                    battery.CycleCount = cycles;
                }
            }

            if (battery.DesignCapacity > 0 && battery.FullChargeCapacity > 0)
            {
                double health = (double)battery.FullChargeCapacity / battery.DesignCapacity * 100;
                if (health > 100) health = 100;
                battery.WearLevelPercent = (int)Math.Round(100 - health);

                if (battery.WearLevelPercent < 20) battery.Status = HealthStatus.Good;
                else if (battery.WearLevelPercent < 40) battery.Status = HealthStatus.Warning;
                else battery.Status = HealthStatus.Bad;
            }
            return battery;
        }

        public OsInfo GetOsInfo()
        {
            var os = new OsInfo();

            var osResults = WmiHelper.Query("Win32_OperatingSystem",
                new[] { "Caption", "Version", "BuildNumber", "OSArchitecture", "InstallDate", "LastBootUpTime" });
            if (osResults.Count > 0)
            {
                var row = osResults[0];
                os.Caption = WmiHelper.GetString(row, "Caption");
                string version = WmiHelper.GetString(row, "Version");
                string build = WmiHelper.GetString(row, "BuildNumber");
                os.Version = build != "Bilinmiyor" ? $"{version} (Build {build})" : version;
                os.Architecture = WmiHelper.GetString(row, "OSArchitecture");

                if (TryParseDmtfDate(WmiHelper.GetString(row, "InstallDate", ""), out DateTime installDate))
                {
                    os.InstallDate = installDate.ToString("dd.MM.yyyy");
                }

                if (TryParseDmtfDate(WmiHelper.GetString(row, "LastBootUpTime", ""), out DateTime bootTime))
                {
                    TimeSpan uptime = DateTime.Now - bootTime;
                    if (uptime.TotalSeconds > 0)
                    {
                        os.Uptime = uptime.TotalDays >= 1
                            ? $"{(int)uptime.TotalDays} gün {uptime.Hours} saat"
                            : $"{uptime.Hours} saat {uptime.Minutes} dakika";
                    }
                }
            }

            var csResults = WmiHelper.Query("Win32_ComputerSystem", new[] { "Manufacturer", "Model" });
            if (csResults.Count > 0)
            {
                os.ComputerManufacturer = WmiHelper.GetString(csResults[0], "Manufacturer");
                os.ComputerModel = WmiHelper.GetString(csResults[0], "Model");
            }

            var biosResults = WmiHelper.Query("Win32_BIOS", new[] { "SMBIOSBIOSVersion", "ReleaseDate" });
            if (biosResults.Count > 0)
            {
                os.BiosVersion = WmiHelper.GetString(biosResults[0], "SMBIOSBIOSVersion");
                if (TryParseDmtfDate(WmiHelper.GetString(biosResults[0], "ReleaseDate", ""), out DateTime biosDate))
                {
                    os.BiosDate = biosDate.ToString("dd.MM.yyyy");
                }
            }

            return os;
        }

        /// <summary>
        /// WMI'nin DMTF tarih formatını (ör. "20230415123045.500000+180") DateTime'a çevirir.
        /// </summary>
        private static bool TryParseDmtfDate(string dmtf, out DateTime result)
        {
            result = default;
            if (string.IsNullOrWhiteSpace(dmtf)) return false;
            try
            {
                result = ManagementDateTimeConverter.ToDateTime(dmtf);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
