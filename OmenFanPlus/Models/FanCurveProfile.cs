using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace OmenFanPlus.Models
{
    public class FanCurvePoint : IComparable<FanCurvePoint>
    {
        public int Temperature { get; set; }
        public int FanSpeed { get; set; }

        public FanCurvePoint() { }
        public FanCurvePoint(int temperature, int fanSpeed)
        {
            Temperature = Math.Clamp(temperature, 0, 100);
            FanSpeed = Math.Clamp(fanSpeed, 0, 100);
        }

        public int CompareTo(FanCurvePoint? other) => Temperature.CompareTo(other?.Temperature ?? 0);
    }

    public class FanCurveProfile
    {
        public List<FanCurvePoint> CpuCurve { get; set; } = new();
        public List<FanCurvePoint> GpuCurve { get; set; } = new();

        public static FanCurveProfile CreateDefault()
        {
            return new FanCurveProfile
            {
                CpuCurve = new List<FanCurvePoint>
                {
                    new(30, 0),
                    new(50, 30),
                    new(70, 60),
                    new(85, 100)
                },
                GpuCurve = new List<FanCurvePoint>
                {
                    new(30, 0),
                    new(50, 30),
                    new(70, 60),
                    new(85, 100)
                }
            };
        }

        public int GetFanSpeed(List<FanCurvePoint> curve, float temperature)
        {
            if (curve == null || curve.Count == 0) return 0;
            var sorted = curve.OrderBy(p => p.Temperature).ToList();

            if (temperature <= sorted[0].Temperature)
                return sorted[0].FanSpeed;
            if (temperature >= sorted[^1].Temperature)
                return sorted[^1].FanSpeed;

            for (int i = 0; i < sorted.Count - 1; i++)
            {
                if (temperature >= sorted[i].Temperature && temperature <= sorted[i + 1].Temperature)
                {
                    float range = sorted[i + 1].Temperature - sorted[i].Temperature;
                    if (range == 0) return sorted[i].FanSpeed;
                    float ratio = (temperature - sorted[i].Temperature) / range;
                    return (int)(sorted[i].FanSpeed + ratio * (sorted[i + 1].FanSpeed - sorted[i].FanSpeed));
                }
            }

            return sorted[^1].FanSpeed;
        }

        private static string StorePath
        {
            get
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OmenFanPlus");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "fancurve.json");
            }
        }

        public void Save()
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(StorePath, JsonSerializer.Serialize(this, options));
        }

        public static FanCurveProfile Load()
        {
            try
            {
                var path = StorePath;
                if (!File.Exists(path))
                {
                    // A curve saved before the rename to OmenFanPlus.
                    path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "OmenHubLighterPlus", "fancurve.json");
                    if (!File.Exists(path)) return CreateDefault();
                }
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<FanCurveProfile>(json) ?? CreateDefault();
            }
            catch
            {
                return CreateDefault();
            }
        }
    }
}
