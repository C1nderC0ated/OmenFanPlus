using System;
using LibreHardwareMonitor.Hardware;
using PawnIoInfo = LibreHardwareMonitor.PawnIo.PawnIo;

namespace OmenHubLight.Lib
{
    public enum CpuTemperatureSource
    {
        None,
        /// <summary>The processor's own sensor, read through the signed PawnIO driver.</summary>
        CpuSensor,
        /// <summary>The ACPI thermal zone, which needs no driver.</summary>
        AcpiThermalZone,
    }

    /// <summary>Temperatures in °C; null when no source could provide one.</summary>
    public readonly record struct TemperatureReading(float? Cpu, float? Gpu, CpuTemperatureSource CpuSource);

    /// <summary>
    /// CPU and GPU temperatures through LibreHardwareMonitorLib, without the WinRing0 driver.
    /// CPU: the processor's own sensor when PawnIO is installed, otherwise the ACPI thermal zone.
    /// GPU: NVIDIA's NVAPI (or AMD's ADL), which run in user mode.
    /// </summary>
    public static class TemperatureService
    {
        /// <summary>How old the GPU reading may be while the popup is open.</summary>
        public static readonly TimeSpan GpuIntervalVisible = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How old the GPU reading may be for background work (the custom fan curve): every NVAPI query can keep a
        /// hybrid-graphics dGPU awake, so it isn't asked every couple of seconds around the clock.
        /// </summary>
        public static readonly TimeSpan GpuIntervalBackground = TimeSpan.FromSeconds(15);

        // CPU temperature sensors in order of preference. Intel: "CPU Package", "Core Max".
        // AMD Ryzen (LibreHardwareMonitor's Amd17Cpu): "Core (Tctl/Tdie)", "Core (Tdie)", "CCDs Max (Tdie)", "Core (Tctl)".
        private static readonly string[] CpuSensorNames =
            { "CPU Package", "Core Max", "Core (Tctl/Tdie)", "Core (Tdie)", "CCDs Max (Tdie)", "Core (Tctl)", "CPU Cores" };

        private static readonly object Lock = new();
        private static Computer _computer;
        private static DateTime _lastGpuUpdate = DateTime.MinValue;
        private static float? _lastGpu;

        /// <summary>Whether PawnIO was installed when the app started (LibreHardwareMonitor checks once).</summary>
        public static bool PawnIoInstalled => PawnIoInfo.IsInstalled;

        public static Version PawnIoVersion => PawnIoInfo.Version;

        /// <summary>Opens the CPU and GPU sensors only: no motherboard, embedded-controller or SMBus access.</summary>
        public static void Open()
        {
            lock (Lock)
            {
                if (_computer != null)
                    return;

                var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true };
                computer.Open();
                _computer = computer;
            }
        }

        public static void Close()
        {
            lock (Lock)
            {
                _computer?.Close();
                _computer = null;
            }
        }

        public static TemperatureReading Read() => Read(GpuIntervalVisible);

        /// <param name="gpuMaxAge">Query the GPU only if the last GPU reading is older than this.</param>
        public static TemperatureReading Read(TimeSpan gpuMaxAge)
        {
            lock (Lock)
            {
                float? cpu = null;
                var updateGpu = DateTime.UtcNow - _lastGpuUpdate >= gpuMaxAge;

                if (_computer != null)
                {
                    float? gpu = null;
                    foreach (var hardware in _computer.Hardware)
                    {
                        switch (hardware.HardwareType)
                        {
                            case HardwareType.Cpu:
                                hardware.Update();
                                cpu ??= FindCpuTemperature(hardware);
                                break;
                            case HardwareType.GpuNvidia:
                            case HardwareType.GpuAmd:
                                if (!updateGpu)
                                    break;
                                hardware.Update();
                                var core = FindTemperature(hardware, "GPU Core");
                                if (core != null && (gpu == null || core > gpu))
                                    gpu = core;
                                break;
                        }
                    }

                    // Without PawnIO the CPU sensors exist but have no value, so cpu stays null.
                    if (updateGpu)
                    {
                        _lastGpu = gpu;
                        _lastGpuUpdate = DateTime.UtcNow;
                    }
                }

                if (cpu != null)
                    return new TemperatureReading(cpu, _lastGpu, CpuTemperatureSource.CpuSensor);

                var acpi = AcpiThermalZone.ReadHottestZone();
                return new TemperatureReading(acpi, _lastGpu,
                    acpi != null ? CpuTemperatureSource.AcpiThermalZone : CpuTemperatureSource.None);
            }
        }

        public static string Describe(CpuTemperatureSource source) => source switch
        {
            CpuTemperatureSource.CpuSensor => "CPU temperature from the processor's own sensor (via PawnIO).",
            CpuTemperatureSource.AcpiThermalZone when PawnIoInstalled =>
                "CPU temperature from the ACPI thermal zone: PawnIO is installed, but the processor sensor didn't answer.",
            CpuTemperatureSource.AcpiThermalZone =>
                "CPU temperature from the ACPI thermal zone, which follows the CPU closely. " +
                "Install PawnIO to read the processor's own sensor.",
            _ => "No CPU temperature source is available.",
        };

        private static float? FindCpuTemperature(IHardware cpu)
        {
            foreach (var name in CpuSensorNames)
            {
                var value = FindTemperature(cpu, name);
                if (value != null)
                    return value;
            }

            return null;
        }

        private static float? FindTemperature(IHardware hardware, string name)
        {
            foreach (var sensor in hardware.Sensors)
            {
                if (sensor.SensorType == SensorType.Temperature && sensor.Name == name &&
                    sensor.Value is float value && !float.IsNaN(value) && value > 0)
                    return value;
            }

            return null;
        }
    }
}
