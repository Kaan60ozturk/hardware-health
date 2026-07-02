using System;
using System.ComponentModel;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using HardwareScanner.Models;
using HardwareScanner.Services;

namespace HardwareScanner.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly HardwareInfoService _hardwareService;
        private readonly SmartInfoService _smartService;
        private readonly NetworkInfoService _networkService;
        private readonly TemperatureService _temperatureService;
        private readonly AutoReportService _autoReportService;

        public MainViewModel()
        {
            _hardwareService = new HardwareInfoService();
            _smartService = new SmartInfoService();
            _networkService = new NetworkInfoService();
            _temperatureService = new TemperatureService();
            _autoReportService = new AutoReportService();
            _sysInfo = new SystemInfo();
        }

        private SystemInfo _sysInfo;
        public SystemInfo SysInfo
        {
            get => _sysInfo;
            set { _sysInfo = value; OnPropertyChanged(); }
        }

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        private string _autoReportStatus = "";
        public string AutoReportStatus
        {
            get => _autoReportStatus;
            set { _autoReportStatus = value; OnPropertyChanged(); }
        }

        public async Task LoadDataAsync()
        {
            if (IsLoading) return; // Aynı anda ikinci taramayı engelle
            IsLoading = true;
            try
            {
                var sysInfo = new SystemInfo();

                // Arka planda donanım bilgilerini getir
                await Task.Run(() =>
                {
                    sysInfo.Os = _hardwareService.GetOsInfo();
                    sysInfo.Cpu = _hardwareService.GetCpuInfo();
                    sysInfo.Ram = _hardwareService.GetRamInfo();
                    sysInfo.Gpus = _hardwareService.GetGpuInfo();
                    sysInfo.Motherboard = _hardwareService.GetMotherboardInfo();
                    sysInfo.Battery = _hardwareService.GetBatteryInfo();
                    sysInfo.Disks = _smartService.GetDisksInfo();
                    sysInfo.Networks = _networkService.GetNetworkAdapters();
                    sysInfo.NetworkHealth = _networkService.GetNetworkHealth();

                    ApplyTemperatures(sysInfo);
                    sysInfo.ScanDate = DateTime.Now;

                    EvaluateOverallStatus(sysInfo);
                });

                SysInfo = sysInfo;
                var autoReport = _autoReportService.SaveIfNewSystem(
                    SysInfo,
                    GenerateReportText(),
                    GenerateReportHtml());
                AutoReportStatus = autoReport.Message;
            }
            finally
            {
                IsLoading = false;
            }
        }

        private void ApplyTemperatures(SystemInfo sysInfo)
        {
            try
            {
                var temps = _temperatureService.GetReadings();
                sysInfo.TemperatureSensors = temps.Sensors;

                if (temps.CpuTemp >= 0) sysInfo.Cpu.Temperature = temps.CpuTemp;
                if (temps.MotherboardTemp >= 0) sysInfo.Motherboard.Temperature = temps.MotherboardTemp;

                if (sysInfo.Gpus.Count == 0 && temps.GpuTemps.Count > 0)
                {
                    foreach (var sensorGpu in temps.GpuTemps)
                    {
                        sysInfo.Gpus.Add(new GpuInfo
                        {
                            Model = sensorGpu.Key,
                            Temperature = sensorGpu.Value,
                            HealthText = "Sağlam (Hata Yok)",
                            Health = HealthStatus.Good
                        });
                    }
                }

                // GPU sıcaklıklarını isme göre eşle
                var unusedGpuTemps = temps.GpuTemps.ToList();
                foreach (var gpu in sysInfo.Gpus)
                {
                    if (gpu.Temperature >= 0) continue;

                    var match = unusedGpuTemps.FirstOrDefault(kv => IsLikelySameDeviceName(kv.Key, gpu.Model));
                    if (string.IsNullOrEmpty(match.Key) && unusedGpuTemps.Count == 1)
                    {
                        match = unusedGpuTemps[0];
                    }

                    if (!string.IsNullOrEmpty(match.Key))
                    {
                        gpu.Temperature = match.Value;
                        unusedGpuTemps.Remove(match);
                    }
                }

                // Disk sıcaklığı smartctl'den gelmediyse LHM'den eşle
                var unusedStorageTemps = temps.StorageTemps.ToList();
                foreach (var disk in sysInfo.Disks)
                {
                    if (disk.Temperature >= 0) continue;
                    var match = unusedStorageTemps.FirstOrDefault(kv => IsLikelySameDeviceName(kv.Key, disk.Model));
                    if (string.IsNullOrEmpty(match.Key) && unusedStorageTemps.Count == 1)
                    {
                        match = unusedStorageTemps[0];
                    }

                    if (!string.IsNullOrEmpty(match.Key))
                    {
                        disk.Temperature = match.Value;
                        unusedStorageTemps.Remove(match);
                    }
                }

                ApplyTemperatureHealth(sysInfo);
            }
            catch (Exception ex)
            {
                AppLog.Write("ApplyTemperatures error: " + ex.Message);
            }
        }

        private static bool IsLikelySameDeviceName(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;

            if (left.Contains(right, StringComparison.OrdinalIgnoreCase) ||
                right.Contains(left, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var leftTokens = DeviceNameTokens(left);
            var rightTokens = DeviceNameTokens(right);
            return leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count() >= 2;
        }

        private static string[] DeviceNameTokens(string value)
            => value
                .Replace("(R)", " ", StringComparison.OrdinalIgnoreCase)
                .Replace("(TM)", " ", StringComparison.OrdinalIgnoreCase)
                .Split(new[] { ' ', '-', '_', '(', ')', '[', ']', '/', '\\', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(x => x.Length >= 3 && !x.Equals("intel", StringComparison.OrdinalIgnoreCase) && !x.Equals("nvidia", StringComparison.OrdinalIgnoreCase) && !x.Equals("amd", StringComparison.OrdinalIgnoreCase))
                .ToArray();

        private static void ApplyTemperatureHealth(SystemInfo sysInfo)
        {
            if (sysInfo.Cpu.Temperature >= 95)
            {
                sysInfo.Cpu.Health = HealthStatus.Bad;
                sysInfo.Cpu.HealthText = "Aşırı Sıcak";
            }
            else if (sysInfo.Cpu.Temperature >= 85 && sysInfo.Cpu.Health != HealthStatus.Bad)
            {
                sysInfo.Cpu.Health = HealthStatus.Warning;
                sysInfo.Cpu.HealthText = "Sıcak";
            }

            if (sysInfo.Motherboard.Temperature >= 85)
            {
                sysInfo.Motherboard.Health = HealthStatus.Bad;
                sysInfo.Motherboard.HealthText = "Aşırı Sıcak";
            }
            else if (sysInfo.Motherboard.Temperature >= 75 && sysInfo.Motherboard.Health != HealthStatus.Bad)
            {
                sysInfo.Motherboard.Health = HealthStatus.Warning;
                sysInfo.Motherboard.HealthText = "Sıcak";
            }

            foreach (var gpu in sysInfo.Gpus)
            {
                if (gpu.Temperature >= 95)
                {
                    gpu.Health = HealthStatus.Bad;
                    gpu.HealthText = "Aşırı Sıcak";
                }
                else if (gpu.Temperature >= 87 && gpu.Health != HealthStatus.Bad)
                {
                    gpu.Health = HealthStatus.Warning;
                    gpu.HealthText = "Sıcak";
                }
            }

            foreach (var disk in sysInfo.Disks)
            {
                if (disk.Temperature >= 70)
                {
                    disk.Status = HealthStatus.Bad;
                    disk.StatusText = "Aşırı Sıcak";
                }
                else if (disk.Temperature >= 55 && disk.Status != HealthStatus.Bad)
                {
                    disk.Status = HealthStatus.Warning;
                    disk.StatusText = "Sıcak";
                }
            }
        }

        private void EvaluateOverallStatus(SystemInfo sysInfo)
        {
            int score = 100;

            // Sıcaklık değerlendirmesi: aşırı ısınma puanı düşürür
            int cpuT = sysInfo.Cpu.Temperature;
            if (cpuT >= 95) score -= 20;
            else if (cpuT >= 85) score -= 10;
            foreach (var gpu in sysInfo.Gpus)
            {
                if (gpu.Temperature >= 95) score -= 15;
                else if (gpu.Temperature >= 87) score -= 8;
            }

            // SSD/HDD değerlendirmesi (En kritik)
            foreach (var disk in sysInfo.Disks)
            {
                if (disk.Status == HealthStatus.Bad) score -= 40;
                else if (disk.Status == HealthStatus.Warning) score -= 20;
                if (disk.HasBadSectors) score -= 20;
            }

            // Batarya değerlendirmesi: aşınmayı orantılı yansıt.
            // Aşınmanın ~yarısı kadar puan düşülür (maks 40). Böylece %16 aşınma
            // ~8 puan düşürür (100 yerine 92) — görünür aşınmayla 100 çelişkisi olmaz.
            if (sysInfo.Battery.IsPresent && sysInfo.Battery.WearLevelPercent > 0)
            {
                score -= Math.Min(40, (int)Math.Round(sysInfo.Battery.WearLevelPercent / 2.0));
            }

            // Diğer bileşen hataları (CPU, GPU, RAM, Anakart)
            if (sysInfo.Cpu.Health == HealthStatus.Bad) score -= 15;
            if (sysInfo.Ram.Health == HealthStatus.Bad) score -= 15;
            if (sysInfo.Motherboard.Health == HealthStatus.Bad) score -= 15;
            foreach (var gpu in sysInfo.Gpus)
            {
                if (gpu.Health == HealthStatus.Bad) score -= 15;
            }

            // Ağ sağlığı: internet erişimi, DNS, gecikme ve paket kaybı da genel puanı etkiler.
            if (sysInfo.NetworkHealth.Status == HealthStatus.Bad) score -= 20;
            else if (sysInfo.NetworkHealth.Status == HealthStatus.Warning) score -= 10;

            foreach (var network in sysInfo.Networks)
            {
                if (network.Health == HealthStatus.Bad) score -= 8;
                else if (network.Health == HealthStatus.Warning) score -= 4;
            }

            if (score >= 80)
            {
                sysInfo.OverallStatus = HealthStatus.Good;
                sysInfo.OverallSummary = "Bu cihaz genel olarak İYİ durumda.";
            }
            else if (score >= 50)
            {
                sysInfo.OverallStatus = HealthStatus.Warning;
                sysInfo.OverallSummary = "Bu cihazda bazı UYARILAR var, kontrol edilmeli.";
            }
            else
            {
                sysInfo.OverallStatus = HealthStatus.Bad;
                sysInfo.OverallSummary = "DİKKAT: Cihazda ciddi DONANIM SORUNLARI var!";
            }

            sysInfo.OverallScore = score < 0 ? 0 : score;
        }

        private static string ValueOrUnknown(long value, string suffix = "")
            => value < 0 ? "Bilinmiyor" : $"{value}{suffix}";

        private static string ValueOrUnknown(double value, string suffix = "")
            => value < 0 ? "Bilinmiyor" : $"{value}{suffix}";

        public string GenerateReportText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("=== DONANIM TEŞHİS RAPORU ===");
            sb.AppendLine($"Tarih: {SysInfo.ScanDate}");
            sb.AppendLine($"Genel Durum: {SysInfo.OverallSummary}");
            sb.AppendLine($"Genel Puan: {SysInfo.OverallScore}/100");
            sb.AppendLine();

            sb.AppendLine("--- SİSTEM ---");
            sb.AppendLine($"Bilgisayar: {SysInfo.Os.ComputerManufacturer} {SysInfo.Os.ComputerModel}");
            sb.AppendLine($"İşletim Sistemi: {SysInfo.Os.Caption} ({SysInfo.Os.Architecture})");
            sb.AppendLine($"Sürüm: {SysInfo.Os.Version}");
            sb.AppendLine($"Kurulum Tarihi: {SysInfo.Os.InstallDate}");
            sb.AppendLine($"Açık Kalma Süresi: {SysInfo.Os.Uptime}");
            sb.AppendLine($"BIOS: {SysInfo.Os.BiosVersion} ({SysInfo.Os.BiosDate})");
            sb.AppendLine();

            sb.AppendLine("--- İŞLEMCİ (CPU) ---");
            sb.AppendLine($"Model: {SysInfo.Cpu.Model}");
            sb.AppendLine($"Çekirdek: {SysInfo.Cpu.Cores} Fiziksel, {SysInfo.Cpu.LogicalProcessors} Mantıksal");
            sb.AppendLine($"Hız: {SysInfo.Cpu.Speed}");
            sb.AppendLine($"Sıcaklık: {SysInfo.Cpu.TemperatureText}");
            sb.AppendLine($"Durum: {SysInfo.Cpu.HealthText}");
            sb.AppendLine();

            sb.AppendLine("--- BELLEK (RAM) ---");
            sb.AppendLine($"Boyut: {SysInfo.Ram.TotalSize}");
            sb.AppendLine($"Hız/Tür: {SysInfo.Ram.Speed} {SysInfo.Ram.Type}");
            sb.AppendLine($"Durum: {SysInfo.Ram.SlotsInfo}");
            sb.AppendLine();

            sb.AppendLine("--- DEPOLAMA (DİSK) ---");
            foreach (var disk in SysInfo.Disks)
            {
                sb.AppendLine($"Model: {disk.Model}");
                sb.AppendLine($"Seri No: {disk.SerialNumber}");
                sb.AppendLine($"Kapasite/Tür: {disk.Capacity} {disk.Type} ({disk.InterfaceType})");
                sb.AppendLine($"Durum: {disk.StatusText}");
                sb.AppendLine($"Sıcaklık: {ValueOrUnknown(disk.Temperature, " °C")}");
                sb.AppendLine($"Çalışma Saati: {ValueOrUnknown(disk.PowerOnHours, " saat")}");
                sb.AppendLine($"Açma/Kapama: {ValueOrUnknown(disk.PowerCycles, " kez")}");
                sb.AppendLine($"Toplam Yazılan: {ValueOrUnknown(disk.Tbw, " TB")}");
                sb.AppendLine($"Toplam Okunan: {ValueOrUnknown(disk.DataRead, " TB")}");
                sb.AppendLine();
            }

            sb.AppendLine("--- EKRAN KARTI (GPU) ---");
            foreach (var gpu in SysInfo.Gpus)
            {
                sb.AppendLine($"Model: {gpu.Model}");
                sb.AppendLine($"VRAM: {gpu.VRam}");
                sb.AppendLine($"Sıcaklık: {gpu.TemperatureText}");
                sb.AppendLine($"Durum: {gpu.HealthText}");
                sb.AppendLine();
            }

            sb.AppendLine("--- SICAKLIK SENSÖRLERİ ---");
            if (SysInfo.TemperatureSensors.Count == 0)
            {
                sb.AppendLine("Sıcaklık sensörü okunamadı.");
            }
            foreach (var sensor in SysInfo.TemperatureSensors)
            {
                sb.AppendLine($"{sensor.Component}: {sensor.Name} = {sensor.TemperatureText}");
            }
            sb.AppendLine();

            sb.AppendLine("--- BATARYA ---");
            sb.AppendLine($"Durum: {SysInfo.Battery.SummaryText}");
            sb.AppendLine($"Tasarım Kapasitesi: {SysInfo.Battery.DesignCapacityText}");
            sb.AppendLine($"Tam Dolum: {SysInfo.Battery.FullChargeCapacityText}");
            sb.AppendLine($"Şarj Döngüsü: {SysInfo.Battery.CycleCountText}");
            sb.AppendLine();

            sb.AppendLine("--- AĞ SAĞLIĞI ---");
            sb.AppendLine($"Özet: {SysInfo.NetworkHealth.Summary}");
            sb.AppendLine($"Bağlantı: {SysInfo.NetworkHealth.HasConnectionText}");
            sb.AppendLine($"İnternet: {SysInfo.NetworkHealth.InternetReachableText}");
            sb.AppendLine($"Ping ({SysInfo.NetworkHealth.PingTarget}): {SysInfo.NetworkHealth.PingText}");
            sb.AppendLine($"Paket Kaybı: {SysInfo.NetworkHealth.PacketLossText}");
            sb.AppendLine($"DNS: {SysInfo.NetworkHealth.DnsText}");
            sb.AppendLine();

            sb.AppendLine("--- AĞ BAĞDAŞTIRICILARI ---");
            if (SysInfo.Networks.Count == 0)
            {
                sb.AppendLine("Aktif ağ bağdaştırıcısı bulunamadı.");
            }
            foreach (var network in SysInfo.Networks)
            {
                sb.AppendLine($"Ad: {network.Name}");
                sb.AppendLine($"Tür/Durum: {network.Type} - {network.StatusText} ({network.HealthText})");
                sb.AppendLine($"IP/MAC: {network.IpAddress} / {network.MacAddress}");
                sb.AppendLine($"Link Hızı: {network.LinkSpeed}");
                sb.AppendLine($"Anlık Hız: ↓ {network.DownloadSpeed} / ↑ {network.UploadSpeed}");
                sb.AppendLine($"Toplam Veri: ↓ {network.TotalDownloaded} / ↑ {network.TotalUploaded}");
                if (!string.IsNullOrEmpty(network.SignalText)) sb.AppendLine(network.SignalText);
                sb.AppendLine();
            }

            sb.AppendLine("--- ANAKART ---");
            sb.AppendLine($"Üretici: {SysInfo.Motherboard.Manufacturer}");
            sb.AppendLine($"Model: {SysInfo.Motherboard.Model}");
            sb.AppendLine($"Sıcaklık: {SysInfo.Motherboard.TemperatureText}");
            sb.AppendLine($"Durum: {SysInfo.Motherboard.HealthText}");

            return sb.ToString();
        }

        public string GenerateReportHtml()
        {
            static string E(string s) => WebUtility.HtmlEncode(s ?? "");

            string statusColor = SysInfo.OverallStatus switch
            {
                HealthStatus.Good => "#4CAF50",
                HealthStatus.Warning => "#FFC107",
                HealthStatus.Bad => "#F44336",
                _ => "#9E9E9E"
            };

            static string PillColor(HealthStatus s) => s switch
            {
                HealthStatus.Good => "#4CAF50",
                HealthStatus.Warning => "#FFC107",
                HealthStatus.Bad => "#F44336",
                _ => "#9E9E9E"
            };

            var sb = new StringBuilder();
            sb.AppendLine("<!DOCTYPE html><html lang=\"tr\"><head><meta charset=\"utf-8\">");
            sb.AppendLine("<title>Donanım Teşhis Raporu</title>");
            sb.AppendLine(@"<style>
body{font-family:'Segoe UI',Arial,sans-serif;background:#F0F2F5;color:#111827;margin:0;padding:24px}
.container{max-width:900px;margin:0 auto}
h1{font-size:24px;margin:0 0 4px}
.date{color:#6B7280;font-size:13px;margin-bottom:20px}
.card{background:#fff;border-radius:12px;padding:20px;margin-bottom:16px;box-shadow:0 2px 8px rgba(0,0,0,.06)}
.card h2{font-size:17px;margin:0 0 12px;color:#1F2937}
.pill{display:inline-block;color:#fff;font-weight:600;font-size:13px;border-radius:12px;padding:4px 12px;margin-bottom:12px}
table{width:100%;border-collapse:collapse;font-size:14px}
td{padding:5px 0;vertical-align:top}
td.k{color:#6B7280;font-weight:600;width:200px}
.score{font-size:40px;font-weight:700}
.score small{font-size:16px;color:#9CA3AF;font-weight:400}
.summary{display:flex;justify-content:space-between;align-items:center}
</style></head><body><div class=""container"">");

            sb.AppendLine("<h1>Donanım Teşhis Raporu</h1>");
            sb.AppendLine($"<div class=\"date\">Tarih: {E(SysInfo.ScanDate.ToString())}</div>");

            sb.AppendLine("<div class=\"card\"><div class=\"summary\"><div>");
            sb.AppendLine($"<span class=\"pill\" style=\"background:{statusColor}\">{E(SysInfo.OverallSummary)}</span>");
            sb.AppendLine($"</div><div class=\"score\" style=\"color:{statusColor}\">{SysInfo.OverallScore}<small>/100</small></div></div></div>");

            // Sistem
            sb.AppendLine("<div class=\"card\"><h2>🖥️ Sistem</h2><table>");
            sb.AppendLine($"<tr><td class=\"k\">Bilgisayar</td><td>{E(SysInfo.Os.ComputerManufacturer)} {E(SysInfo.Os.ComputerModel)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">İşletim Sistemi</td><td>{E(SysInfo.Os.Caption)} ({E(SysInfo.Os.Architecture)})</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Sürüm</td><td>{E(SysInfo.Os.Version)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Kurulum Tarihi</td><td>{E(SysInfo.Os.InstallDate)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">BIOS</td><td>{E(SysInfo.Os.BiosVersion)} ({E(SysInfo.Os.BiosDate)})</td></tr>");
            sb.AppendLine("</table></div>");

            // CPU
            sb.AppendLine("<div class=\"card\"><h2>⚙️ İşlemci (CPU)</h2>");
            sb.AppendLine($"<span class=\"pill\" style=\"background:{PillColor(SysInfo.Cpu.Health)}\">{E(SysInfo.Cpu.HealthText)}</span><table>");
            sb.AppendLine($"<tr><td class=\"k\">Model</td><td>{E(SysInfo.Cpu.Model)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Çekirdek</td><td>{SysInfo.Cpu.Cores} Fiziksel / {SysInfo.Cpu.LogicalProcessors} Mantıksal</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Hız</td><td>{E(SysInfo.Cpu.Speed)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Sıcaklık</td><td>{E(SysInfo.Cpu.TemperatureText)}</td></tr>");
            sb.AppendLine("</table></div>");

            // RAM
            sb.AppendLine("<div class=\"card\"><h2>🧠 Bellek (RAM)</h2>");
            sb.AppendLine($"<span class=\"pill\" style=\"background:{PillColor(SysInfo.Ram.Health)}\">{E(SysInfo.Ram.HealthText)}</span><table>");
            sb.AppendLine($"<tr><td class=\"k\">Boyut</td><td>{E(SysInfo.Ram.TotalSize)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Hız / Tür</td><td>{E(SysInfo.Ram.Speed)} {E(SysInfo.Ram.Type)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Yuvalar</td><td>{E(SysInfo.Ram.SlotsInfo)}</td></tr>");
            sb.AppendLine("</table></div>");

            // Diskler
            foreach (var disk in SysInfo.Disks)
            {
                sb.AppendLine("<div class=\"card\"><h2>💾 Depolama Sürücüsü</h2>");
                sb.AppendLine($"<span class=\"pill\" style=\"background:{PillColor(disk.Status)}\">{E(disk.StatusText)}</span><table>");
                sb.AppendLine($"<tr><td class=\"k\">Model</td><td>{E(disk.Model)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Seri No</td><td>{E(disk.SerialNumber)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Kapasite / Tür</td><td>{E(disk.Capacity)} {E(disk.Type)} ({E(disk.InterfaceType)})</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Sıcaklık</td><td>{E(ValueOrUnknown(disk.Temperature, " °C"))}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Çalışma Saati</td><td>{E(ValueOrUnknown(disk.PowerOnHours, " saat"))}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Açma/Kapama</td><td>{E(ValueOrUnknown(disk.PowerCycles, " kez"))}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Toplam Yazılan</td><td>{E(ValueOrUnknown(disk.Tbw, " TB"))}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Toplam Okunan</td><td>{E(ValueOrUnknown(disk.DataRead, " TB"))}</td></tr>");
                sb.AppendLine("</table></div>");
            }

            // GPU
            foreach (var gpu in SysInfo.Gpus)
            {
                sb.AppendLine("<div class=\"card\"><h2>🎮 Ekran Kartı (GPU)</h2>");
                sb.AppendLine($"<span class=\"pill\" style=\"background:{PillColor(gpu.Health)}\">{E(gpu.HealthText)}</span><table>");
                sb.AppendLine($"<tr><td class=\"k\">Model</td><td>{E(gpu.Model)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">VRAM</td><td>{E(gpu.VRam)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Sıcaklık</td><td>{E(gpu.TemperatureText)}</td></tr>");
                sb.AppendLine("</table></div>");
            }

            // Sıcaklık sensörleri
            sb.AppendLine("<div class=\"card\"><h2>🌡️ Sıcaklık Sensörleri</h2>");
            sb.AppendLine($"<span class=\"pill\" style=\"background:#4CAF50\">{E(SysInfo.TemperatureSensorSummaryText)}</span><table>");
            foreach (var sensor in SysInfo.TemperatureSensors)
            {
                sb.AppendLine($"<tr><td class=\"k\">{E(sensor.Component)}</td><td>{E(sensor.Name)}: {E(sensor.TemperatureText)}</td></tr>");
            }
            sb.AppendLine("</table></div>");

            // Batarya
            sb.AppendLine("<div class=\"card\"><h2>🔋 Batarya</h2>");
            sb.AppendLine($"<span class=\"pill\" style=\"background:{PillColor(SysInfo.Battery.Status)}\">{E(SysInfo.Battery.SummaryText)}</span><table>");
            sb.AppendLine($"<tr><td class=\"k\">Tasarım Kapasitesi</td><td>{E(SysInfo.Battery.DesignCapacityText)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Tam Dolum Kapasitesi</td><td>{E(SysInfo.Battery.FullChargeCapacityText)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Şarj Döngüsü</td><td>{E(SysInfo.Battery.CycleCountText)}</td></tr>");
            sb.AppendLine("</table></div>");

            // Ağ
            sb.AppendLine("<div class=\"card\"><h2>🌐 Ağ Sağlığı</h2>");
            sb.AppendLine($"<span class=\"pill\" style=\"background:{PillColor(SysInfo.NetworkHealth.Status)}\">{E(SysInfo.NetworkHealth.Summary)}</span><table>");
            sb.AppendLine($"<tr><td class=\"k\">Bağlantı</td><td>{E(SysInfo.NetworkHealth.HasConnectionText)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">İnternet</td><td>{E(SysInfo.NetworkHealth.InternetReachableText)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Ping</td><td>{E(SysInfo.NetworkHealth.PingText)} ({E(SysInfo.NetworkHealth.PingTarget)})</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Paket Kaybı</td><td>{E(SysInfo.NetworkHealth.PacketLossText)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">DNS</td><td>{E(SysInfo.NetworkHealth.DnsText)}</td></tr>");
            sb.AppendLine("</table></div>");

            foreach (var network in SysInfo.Networks)
            {
                sb.AppendLine("<div class=\"card\"><h2>📡 Ağ Bağdaştırıcısı</h2>");
                sb.AppendLine($"<span class=\"pill\" style=\"background:{PillColor(network.Health)}\">{E(network.HealthText)}</span><table>");
                sb.AppendLine($"<tr><td class=\"k\">Ad</td><td>{E(network.Name)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Tür / Durum</td><td>{E(network.Type)} / {E(network.StatusText)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">IP</td><td>{E(network.IpAddress)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">MAC</td><td>{E(network.MacAddress)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Link Hızı</td><td>{E(network.LinkSpeed)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Anlık Hız</td><td>↓ {E(network.DownloadSpeed)} / ↑ {E(network.UploadSpeed)}</td></tr>");
                sb.AppendLine($"<tr><td class=\"k\">Toplam Veri</td><td>↓ {E(network.TotalDownloaded)} / ↑ {E(network.TotalUploaded)}</td></tr>");
                if (!string.IsNullOrEmpty(network.SignalText))
                    sb.AppendLine($"<tr><td class=\"k\">Sinyal</td><td>{E(network.SignalText)}</td></tr>");
                sb.AppendLine("</table></div>");
            }

            // Anakart
            sb.AppendLine("<div class=\"card\"><h2>🎛️ Anakart</h2>");
            sb.AppendLine($"<span class=\"pill\" style=\"background:{PillColor(SysInfo.Motherboard.Health)}\">{E(SysInfo.Motherboard.HealthText)}</span><table>");
            sb.AppendLine($"<tr><td class=\"k\">Üretici</td><td>{E(SysInfo.Motherboard.Manufacturer)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Model</td><td>{E(SysInfo.Motherboard.Model)}</td></tr>");
            sb.AppendLine($"<tr><td class=\"k\">Sıcaklık</td><td>{E(SysInfo.Motherboard.TemperatureText)}</td></tr>");
            sb.AppendLine("</table></div>");

            sb.AppendLine("</div></body></html>");
            return sb.ToString();
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
