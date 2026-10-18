using System;
using System.Linq;
using System.Management;

namespace OmenHubLight.Lib
{
    /// <summary>
    /// Reads the ACPI thermal zones through Windows' own performance counters. Needs no driver.
    /// On the OMEN 15-dc1xxx, \_TZ.TZ01 follows the CPU closely (it rose from 67 to 81 °C under load).
    /// </summary>
    internal static class AcpiThermalZone
    {
        public static float? ReadHottestZone()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                    new ObjectQuery("SELECT HighPrecisionTemperature, Temperature FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"),
                    new EnumerationOptions { Timeout = TimeSpan.FromSeconds(3) });
                using var results = searcher.Get();

                var temperatures = results.OfType<ManagementBaseObject>()
                    .Select(ToCelsius)
                    .Where(t => t is > 0 and < 125)
                    .ToList();

                return temperatures.Count > 0 ? temperatures.Max() : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static float? ToCelsius(ManagementBaseObject zone)
        {
            // HighPrecisionTemperature is in tenths of a kelvin, Temperature in whole kelvin.
            if (zone["HighPrecisionTemperature"] is uint tenthsKelvin && tenthsKelvin > 0)
                return tenthsKelvin / 10f - 273.15f;
            if (zone["Temperature"] is uint kelvin && kelvin > 0)
                return kelvin - 273.15f;
            return null;
        }
    }
}
