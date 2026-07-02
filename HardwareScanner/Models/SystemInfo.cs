using System;
using System.Collections.Generic;

namespace HardwareScanner.Models
{
    public enum HealthStatus
    {
        Good,
        Warning,
        Bad,
        Unknown
    }

    public class SystemInfo
    {
        public string OverallSummary { get; set; } = "Bilgi alınamadı";
        public HealthStatus OverallStatus { get; set; } = HealthStatus.Unknown;
        public int OverallScore { get; set; } = 0;
        public DateTime ScanDate { get; set; } = DateTime.Now;

        public OsInfo Os { get; set; } = new();
        public List<DiskInfo> Disks { get; set; } = new();
        public RamInfo Ram { get; set; } = new();
        public CpuInfo Cpu { get; set; } = new();
        public List<GpuInfo> Gpus { get; set; } = new();
        public BatteryInfo Battery { get; set; } = new();
        public MotherboardInfo Motherboard { get; set; } = new();
        public List<NetworkInfo> Networks { get; set; } = new();
        public NetworkHealthInfo NetworkHealth { get; set; } = new();
    }

    /// <summary>Ortak yardımcılar (byte biçimleme, sıcaklık metni).</summary>
    public static class Fmt
    {
        public static string Bytes(long bytes)
        {
            if (bytes < 0) return "Bilinmiyor";
            string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
            double val = bytes;
            int u = 0;
            while (val >= 1024 && u < units.Length - 1) { val /= 1024; u++; }
            return $"{val:0.##} {units[u]}";
        }

        public static string BitsPerSec(double bytesPerSec)
        {
            if (bytesPerSec < 0) return "Bilinmiyor";
            double bits = bytesPerSec * 8;
            string[] units = { "bps", "Kbps", "Mbps", "Gbps" };
            int u = 0;
            while (bits >= 1000 && u < units.Length - 1) { bits /= 1000; u++; }
            return $"{bits:0.#} {units[u]}";
        }

        public static string Temp(int celsius) => celsius < 0 ? "Bilinmiyor" : $"{celsius} °C";
    }

    public class OsInfo
    {
        public string Caption { get; set; } = "Bilinmiyor";        // ör: Microsoft Windows 11 Pro
        public string Version { get; set; } = "Bilinmiyor";        // ör: 10.0.22631 (Build 22631)
        public string Architecture { get; set; } = "Bilinmiyor";   // ör: 64-bit
        public string InstallDate { get; set; } = "Bilinmiyor";
        public string Uptime { get; set; } = "Bilinmiyor";
        public string ComputerManufacturer { get; set; } = "Bilinmiyor";
        public string ComputerModel { get; set; } = "Bilinmiyor";
        public string BiosVersion { get; set; } = "Bilinmiyor";
        public string BiosDate { get; set; } = "Bilinmiyor";
    }

    public class DiskInfo
    {
        public string Model { get; set; } = "Bilinmiyor";
        public string SerialNumber { get; set; } = "Bilinmiyor";
        public string Capacity { get; set; } = "Bilinmiyor";
        public string Type { get; set; } = "Bilinmiyor"; // SSD / HDD / NVMe SSD
        public string InterfaceType { get; set; } = "Bilinmiyor"; // NVMe / SATA / USB ...
        public int HealthPercent { get; set; } = -1; // -1 means unknown
        public long PowerOnHours { get; set; } = -1;
        public long PowerCycles { get; set; } = -1;
        public double Tbw { get; set; } = -1; // Total Bytes Written in TB
        public double DataRead { get; set; } = -1; // Total Bytes Read in TB
        public int Temperature { get; set; } = -1;
        public bool HasBadSectors { get; set; } = false;
        public string StatusText { get; set; } = "Bilinmiyor";
        public HealthStatus Status { get; set; } = HealthStatus.Unknown;
    }

    public class RamInfo
    {
        public string TotalSize { get; set; } = "Bilinmiyor";
        public string Speed { get; set; } = "Bilinmiyor";
        public string Type { get; set; } = "Bilinmiyor"; // DDR3/4/5
        public string SlotsInfo { get; set; } = "Bilinmiyor";
        public string HealthText { get; set; } = "Sağlam";
        public HealthStatus Health { get; set; } = HealthStatus.Good;
    }

    public class CpuInfo
    {
        public string Model { get; set; } = "Bilinmiyor";
        public int Cores { get; set; } = 0;
        public int LogicalProcessors { get; set; } = 0;
        public string Speed { get; set; } = "Bilinmiyor";
        public int Temperature { get; set; } = -1;
        public string TemperatureText => Fmt.Temp(Temperature);
        public string HealthText { get; set; } = "Sağlam";
        public HealthStatus Health { get; set; } = HealthStatus.Good;
    }

    public class GpuInfo
    {
        public string Model { get; set; } = "Bilinmiyor";
        public string VRam { get; set; } = "Bilinmiyor";
        public int Temperature { get; set; } = -1;
        public string TemperatureText => Fmt.Temp(Temperature);
        public string HealthText { get; set; } = "Sağlam";
        public HealthStatus Health { get; set; } = HealthStatus.Good;
    }

    public class BatteryInfo
    {
        public bool IsPresent { get; set; } = false;
        public uint DesignCapacity { get; set; } = 0;
        public uint FullChargeCapacity { get; set; } = 0;
        public int WearLevelPercent { get; set; } = -1;
        public int CycleCount { get; set; } = -1;
        public HealthStatus Status { get; set; } = HealthStatus.Unknown;
        public string WearText => WearLevelPercent >= 0 ? $"Aşınma: %{WearLevelPercent}" : "Aşınma: Bilinmiyor";
    }

    public class MotherboardInfo
    {
        public string Manufacturer { get; set; } = "Bilinmiyor";
        public string Model { get; set; } = "Bilinmiyor";
        public int Temperature { get; set; } = -1;
        public string TemperatureText => Fmt.Temp(Temperature);
        public string HealthText { get; set; } = "Sağlam";
        public HealthStatus Health { get; set; } = HealthStatus.Good;
    }

    public class NetworkInfo
    {
        public string Name { get; set; } = "Bilinmiyor";
        public string Type { get; set; } = "Bilinmiyor";      // Ethernet / Wi-Fi
        public string StatusText { get; set; } = "Bilinmiyor"; // Bağlı / Bağlı Değil
        public bool IsUp { get; set; }
        public string MacAddress { get; set; } = "Bilinmiyor";
        public string IpAddress { get; set; } = "-";
        public string LinkSpeed { get; set; } = "Bilinmiyor";

        // Açılıştan beri toplam veri (biçimlenmiş)
        public string TotalDownloaded { get; set; } = "Bilinmiyor";
        public string TotalUploaded { get; set; } = "Bilinmiyor";

        // Anlık hız (biçimlenmiş)
        public string DownloadSpeed { get; set; } = "0 bps";
        public string UploadSpeed { get; set; } = "0 bps";

        public int SignalPercent { get; set; } = -1;           // Yalnızca Wi-Fi
        public bool IsWifi { get; set; }
        public string SignalText => SignalPercent >= 0 ? $"Sinyal: %{SignalPercent}" : "";

        public HealthStatus Health { get; set; } = HealthStatus.Good;
        public string HealthText { get; set; } = "Bağlı";
    }

    public class NetworkHealthInfo
    {
        public bool HasConnection { get; set; }
        public bool InternetReachable { get; set; }
        public string PingTarget { get; set; } = "8.8.8.8";
        public long PingMs { get; set; } = -1;
        public int PacketLossPercent { get; set; } = -1;
        public bool DnsOk { get; set; }
        public string DnsText => DnsOk ? "Başarılı" : "Başarısız";

        public HealthStatus Status { get; set; } = HealthStatus.Unknown;
        public string Summary { get; set; } = "Ağ durumu kontrol edilmedi";

        public string PingText => PingMs >= 0 ? $"{PingMs} ms" : "Bilinmiyor";
        public string PacketLossText => PacketLossPercent >= 0 ? $"%{PacketLossPercent}" : "Bilinmiyor";
    }
}
