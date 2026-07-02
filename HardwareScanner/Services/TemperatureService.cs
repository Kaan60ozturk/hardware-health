using System;
using System.Collections.Generic;
using System.Linq;
using HardwareScanner.Models;
using LibreHardwareMonitor.Hardware;

namespace HardwareScanner.Services
{
    /// <summary>
    /// Donanım sıcaklıklarını okur.
    /// Öncelik: LibreHardwareMonitor (GPU için NVAPI/ADL sürücüsüz çalışır; CPU/anakart
    /// için Ring0 sürücüsü gerekir — bazı Windows sürümlerinde "savunmasız sürücü engelleme
    /// listesi" nedeniyle yüklenemez). CPU/sistem sıcaklığı okunamazsa WMI termal bölgesine
    /// (MSAcpi_ThermalZoneTemperature, yönetici gerektirir) düşer.
    /// </summary>
    public class TemperatureService : IDisposable
    {
        private Computer? _computer;
        private bool _openFailed;
        private readonly object _lock = new();

        public class Readings
        {
            public int CpuTemp = -1;
            public int MotherboardTemp = -1;
            // Donanım adı -> sıcaklık (GPU ve depolama eşlemesi için)
            public Dictionary<string, int> GpuTemps = new(StringComparer.OrdinalIgnoreCase);
            public Dictionary<string, int> StorageTemps = new(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class UpdateVisitor : IVisitor
        {
            public void VisitComputer(IComputer computer) => computer.Traverse(this);
            public void VisitHardware(IHardware hardware)
            {
                hardware.Update();
                foreach (var sub in hardware.SubHardware) sub.Accept(this);
            }
            public void VisitSensor(ISensor sensor) { }
            public void VisitParameter(IParameter parameter) { }
        }

        private void EnsureOpen()
        {
            if (_computer != null || _openFailed) return;
            try
            {
                var c = new Computer
                {
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsMotherboardEnabled = true,
                    IsStorageEnabled = true,
                };
                c.Open();
                _computer = c;
            }
            catch (Exception ex)
            {
                _openFailed = true;
                AppLog.Write("LHM open error: " + ex.Message);
            }
        }

        public Readings GetReadings()
        {
            var r = new Readings();
            lock (_lock)
            {
                EnsureOpen();
                if (_computer != null)
                {
                    try
                    {
                        _computer.Accept(new UpdateVisitor());
                        foreach (var hw in _computer.Hardware)
                        {
                            switch (hw.HardwareType)
                            {
                                case HardwareType.Cpu:
                                    int cpu = ReadCpuTemp(hw);
                                    if (cpu >= 0) r.CpuTemp = cpu;
                                    break;
                                case HardwareType.GpuNvidia:
                                case HardwareType.GpuAmd:
                                case HardwareType.GpuIntel:
                                    int gpu = ReadSensor(hw, "GPU Core") ?? ReadAnyTemp(hw) ?? -1;
                                    if (gpu >= 0) r.GpuTemps[hw.Name] = gpu;
                                    break;
                                case HardwareType.Motherboard:
                                    int mb = ReadMotherboardTemp(hw);
                                    if (mb >= 0) r.MotherboardTemp = mb;
                                    break;
                                case HardwareType.Storage:
                                    int st = ReadSensor(hw, "Temperature") ?? ReadAnyTemp(hw) ?? -1;
                                    if (st >= 0) r.StorageTemps[hw.Name] = st;
                                    break;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("LHM read error: " + ex.Message);
                    }
                }
            }

            // CPU/anakart sıcaklığı LHM'den gelmediyse WMI termal bölgesine düş
            if (r.CpuTemp < 0)
            {
                int zone = ReadThermalZoneWmi();
                if (zone >= 0) r.CpuTemp = zone;
            }

            return r;
        }

        private static int ReadCpuTemp(IHardware cpu)
        {
            // Tercih sırası: CPU Package > Core (Tmax) > çekirdek ortalaması/maksimumu
            return ReadSensor(cpu, "CPU Package")
                ?? ReadSensor(cpu, "Core (Tmax)")
                ?? ReadSensor(cpu, "Core Average")
                ?? MaxCoreTemp(cpu)
                ?? -1;
        }

        private static int ReadMotherboardTemp(IHardware mb)
        {
            // Anakart sıcaklığı genelde Super-I/O alt donanımındadır
            foreach (var sub in mb.SubHardware)
            {
                int? t = ReadSensor(sub, "Temperature")
                    ?? ReadSensor(sub, "System")
                    ?? ReadAnyTemp(sub);
                if (t.HasValue) return t.Value;
            }
            return ReadAnyTemp(mb) ?? -1;
        }

        private static int? ReadSensor(IHardware hw, string sensorName)
        {
            var s = hw.Sensors.FirstOrDefault(x =>
                x.SensorType == SensorType.Temperature &&
                x.Value.HasValue &&
                string.Equals(x.Name, sensorName, StringComparison.OrdinalIgnoreCase));
            return s?.Value is float v ? (int)Math.Round(v) : (int?)null;
        }

        private static int? ReadAnyTemp(IHardware hw)
        {
            var s = hw.Sensors.FirstOrDefault(x => x.SensorType == SensorType.Temperature && x.Value.HasValue);
            return s?.Value is float v ? (int)Math.Round(v) : (int?)null;
        }

        private static int? MaxCoreTemp(IHardware hw)
        {
            var temps = hw.Sensors
                .Where(x => x.SensorType == SensorType.Temperature && x.Value.HasValue &&
                            x.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Value!.Value)
                .ToList();
            return temps.Count > 0 ? (int)Math.Round(temps.Max()) : (int?)null;
        }

        private static int ReadThermalZoneWmi()
        {
            var results = WmiHelper.Query("MSAcpi_ThermalZoneTemperature", new[] { "CurrentTemperature" }, @"root\wmi");
            foreach (var row in results)
            {
                if (int.TryParse(WmiHelper.GetString(row, "CurrentTemperature", "0"), out int deci) && deci > 0)
                {
                    // Kelvin'in onda biri -> Celsius
                    double celsius = (deci / 10.0) - 273.15;
                    if (celsius > 0 && celsius < 130) return (int)Math.Round(celsius);
                }
            }
            return -1;
        }

        public void Dispose()
        {
            lock (_lock)
            {
                try { _computer?.Close(); } catch { }
                _computer = null;
            }
            GC.SuppressFinalize(this);
        }
    }
}
