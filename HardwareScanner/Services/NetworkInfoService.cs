using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using HardwareScanner.Models;

namespace HardwareScanner.Services
{
    /// <summary>
    /// Ağ bağdaştırıcısı bilgilerini (veri kullanımı, anlık hız, bağlantı durumu) ve
    /// genel ağ sağlığını (internet erişimi, ping gecikmesi, paket kaybı, DNS) okur.
    /// Tümü .NET dahili API'leri ile — ek bağımlılık yok.
    /// </summary>
    public class NetworkInfoService
    {
        /// <summary>
        /// Aktif ağ bağdaştırıcılarını, ~1 saniyelik örneklemeyle anlık hız dahil döner.
        /// </summary>
        public List<NetworkInfo> GetNetworkAdapters()
        {
            var list = new List<NetworkInfo>();
            NetworkInterface[] interfaces;
            try { interfaces = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception ex) { AppLog.Write("Network interfaces error: " + ex.Message); return list; }

            // İlgili bağdaştırıcıları filtrele (loopback/tünel hariç). Aktif olanlar
            // öncelikli; hiç aktif kart bulunamazsa fiziksel adayları yine göster.
            var candidates = interfaces.Where(IsRelevantInterface).ToList();
            var relevant = candidates
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .OrderBy(IsVirtualInterface)
                .ThenBy(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                .ThenBy(ni => ni.Name)
                .ToList();

            if (relevant.Count == 0)
            {
                relevant = candidates
                    .OrderBy(IsVirtualInterface)
                    .ThenBy(ni => ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
                    .ThenBy(ni => ni.Name)
                    .ToList();
            }

            // Anlık hız için iki örnek al: önce tüm sayaçları oku, ~1sn bekle, tekrar oku
            var firstBytes = new Dictionary<string, (long rx, long tx)>();
            foreach (var ni in relevant)
            {
                try { var s = ni.GetIPv4Statistics(); firstBytes[ni.Id] = (s.BytesReceived, s.BytesSent); }
                catch { }
            }

            var sw = Stopwatch.StartNew();
            Thread.Sleep(1000);
            sw.Stop();
            double seconds = sw.Elapsed.TotalSeconds;
            if (seconds <= 0) seconds = 1;

            var wifiSignals = GetWifiSignalPercents();

            foreach (var ni in relevant)
            {
                var info = new NetworkInfo();
                try
                {
                    info.Name = ni.Description;
                    info.IsUp = ni.OperationalStatus == OperationalStatus.Up;
                    info.StatusText = info.IsUp ? "Bağlı" : "Bağlı Değil";
                    info.IsWifi = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
                    info.Type = info.IsWifi ? "Wi-Fi"
                        : ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? "Ethernet"
                        : ni.NetworkInterfaceType.ToString();

                    var mac = ni.GetPhysicalAddress().ToString();
                    info.MacAddress = string.IsNullOrEmpty(mac)
                        ? "Bilinmiyor"
                        : string.Join(":", Enumerable.Range(0, mac.Length / 2).Select(i => mac.Substring(i * 2, 2)));

                    // Bağlantı hızı (link speed)
                    if (ni.Speed > 0)
                        info.LinkSpeed = Fmt.BitsPerSec(ni.Speed / 8.0);

                    // IP adresi
                    try
                    {
                        var ip = ni.GetIPProperties().UnicastAddresses
                            .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
                        if (ip != null) info.IpAddress = ip.Address.ToString();
                    }
                    catch { }

                    // Toplam veri + anlık hız
                    var stats = ni.GetIPv4Statistics();
                    info.TotalDownloaded = Fmt.Bytes(stats.BytesReceived);
                    info.TotalUploaded = Fmt.Bytes(stats.BytesSent);

                    if (firstBytes.TryGetValue(ni.Id, out var first))
                    {
                        double dnRate = Math.Max(0, (stats.BytesReceived - first.rx) / seconds);
                        double upRate = Math.Max(0, (stats.BytesSent - first.tx) / seconds);
                        info.DownloadSpeed = Fmt.BitsPerSec(dnRate);
                        info.UploadSpeed = Fmt.BitsPerSec(upRate);
                    }

                    // Wi-Fi sinyali
                    if (info.IsWifi && wifiSignals.Count > 0)
                    {
                        // Genelde tek Wi-Fi arayüzü olur; ilk sinyali eşle
                        info.SignalPercent = wifiSignals[0];
                    }

                    // Sağlık: gönderme/alma hatalarına bak
                    long errors = 0;
                    try { errors = stats.IncomingPacketsWithErrors + stats.OutgoingPacketsWithErrors; } catch { }
                    if (!info.IsUp)
                    {
                        info.Health = HealthStatus.Unknown;
                        info.HealthText = "Bağlı Değil";
                    }
                    else if (info.IsWifi && info.SignalPercent >= 0 && info.SignalPercent < 40)
                    {
                        info.Health = HealthStatus.Warning;
                        info.HealthText = "Zayıf Sinyal";
                    }
                    else if (errors > 100)
                    {
                        info.Health = HealthStatus.Warning;
                        info.HealthText = "Paket Hataları";
                    }
                    else
                    {
                        info.Health = HealthStatus.Good;
                        info.HealthText = "Bağlı";
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("Network adapter read error: " + ex.Message);
                    info.Health = HealthStatus.Unknown;
                    info.HealthText = "Okunamadı";
                }
                list.Add(info);
            }

            return list;
        }

        private static bool IsRelevantInterface(NetworkInterface ni)
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
            {
                return false;
            }

            string name = (ni.Name + " " + ni.Description).ToLowerInvariant();
            return !name.Contains("wi-fi direct") &&
                   !name.Contains("wifi direct") &&
                   !name.Contains("teredo") &&
                   !name.Contains("isatap") &&
                   !name.Contains("pseudo");
        }

        private static bool IsVirtualInterface(NetworkInterface ni)
        {
            string name = (ni.Name + " " + ni.Description).ToLowerInvariant();
            return name.Contains("virtual") ||
                   name.Contains("hyper-v") ||
                   name.Contains("wsl") ||
                   name.Contains("vmware") ||
                   name.Contains("virtualbox");
        }

        /// <summary>
        /// İnternet erişimi, ping gecikmesi/paket kaybı ve DNS çözümlemesini test eder.
        /// </summary>
        public NetworkHealthInfo GetNetworkHealth()
        {
            var health = new NetworkHealthInfo();

            try { health.HasConnection = NetworkInterface.GetIsNetworkAvailable(); }
            catch { health.HasConnection = false; }

            // Ping testi (4 paket)
            const int attempts = 4;
            int success = 0;
            long totalMs = 0;
            try
            {
                using var ping = new Ping();
                for (int i = 0; i < attempts; i++)
                {
                    try
                    {
                        var reply = ping.Send(health.PingTarget, 2000);
                        if (reply != null && reply.Status == IPStatus.Success)
                        {
                            success++;
                            totalMs += reply.RoundtripTime;
                        }
                    }
                    catch { }
                }
            }
            catch (Exception ex) { AppLog.Write("Ping error: " + ex.Message); }

            if (success > 0)
            {
                health.PingMs = totalMs / success;
                health.InternetReachable = true;
            }
            health.PacketLossPercent = (int)Math.Round((attempts - success) * 100.0 / attempts);

            // DNS testi
            try
            {
                var entry = Dns.GetHostEntry("www.microsoft.com");
                health.DnsOk = entry.AddressList.Length > 0;
            }
            catch { health.DnsOk = false; }

            EvaluateHealth(health);
            return health;
        }

        private static void EvaluateHealth(NetworkHealthInfo h)
        {
            if (!h.HasConnection && !h.InternetReachable)
            {
                h.Status = HealthStatus.Bad;
                h.Summary = "İnternet bağlantısı yok";
            }
            else if (!h.InternetReachable)
            {
                h.Status = HealthStatus.Warning;
                h.Summary = "Yerel ağ var, internete erişilemiyor";
            }
            else if (h.PacketLossPercent >= 50 || !h.DnsOk)
            {
                h.Status = HealthStatus.Warning;
                h.Summary = !h.DnsOk ? "İnternet var ama DNS sorunlu" : "Yüksek paket kaybı";
            }
            else if (h.PacketLossPercent > 0 || h.PingMs > 150)
            {
                h.Status = HealthStatus.Warning;
                h.Summary = h.PingMs > 150 ? "Bağlantı yavaş (yüksek gecikme)" : "Hafif paket kaybı";
            }
            else
            {
                h.Status = HealthStatus.Good;
                h.Summary = "İnternet bağlantısı sağlıklı";
            }
        }

        /// <summary>
        /// "netsh wlan show interfaces" çıktısından Wi-Fi sinyal yüzdelerini okur.
        /// Yerelden bağımsız: çıktıdaki tek yüzde değeri sinyaldir.
        /// </summary>
        private static List<int> GetWifiSignalPercents()
        {
            var signals = new List<int>();
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                };
                using var proc = Process.Start(psi);
                if (proc == null) return signals;
                string output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(4000);

                // "Sinyal" / "Signal" satırındaki yüzde değerini yakala
                foreach (Match m in Regex.Matches(output, @"(?:Signal|Sinyal)\s*[:=]\s*(\d{1,3})\s*%", RegexOptions.IgnoreCase))
                {
                    if (int.TryParse(m.Groups[1].Value, out int pct)) signals.Add(Math.Min(100, pct));
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("WiFi signal read error: " + ex.Message);
            }
            return signals;
        }
    }
}
