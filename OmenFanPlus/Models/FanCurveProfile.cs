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

    /// <summary>
    /// Per-fan curves of temperature (°C) to fan speed in percent. 0 % stops the fan; 1-100 % maps onto the
    /// board's usable fan-level range (see <see cref="LevelFromPercent"/>).
    /// </summary>
    public class FanCurveProfile
    {
        /// <summary>The fan level the BIOS treats as "stopped"; the same value the Off button sends.</summary>
        public const int StoppedLevel = 1;

        public List<FanCurvePoint> CpuCurve { get; set; } = new();
        public List<FanCurvePoint> GpuCurve { get; set; } = new();

        public static FanCurveProfile CreateDefault()
        {
            return new FanCurveProfile
            {
                CpuCurve = DefaultCurve(),
                GpuCurve = DefaultCurve(),
            };
        }

        private static List<FanCurvePoint> DefaultCurve() => new()
        {
            new(30, 0),
            new(50, 30),
            new(70, 60),
            new(85, 100)
        };

        /// <summary>Fan speed in percent for a temperature. A missing curve means full speed, never a stopped fan.</summary>
        public int GetFanSpeed(List<FanCurvePoint> curve, float temperature)
        {
            if (curve == null || curve.Count == 0) return 100;
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

        /// <summary>
        /// BIOS fan level (units of 100 RPM) for a curve percentage. The BIOS levels start at the fan's lowest
        /// running speed, so 1 % is the slowest the fan can turn and 0 % stops it.
        /// </summary>
        public static int LevelFromPercent(int percent, int minLevel, int maxLevel)
        {
            percent = Math.Clamp(percent, 0, 100);
            if (percent == 0)
                return StoppedLevel;
            return (int)Math.Round(minLevel + (maxLevel - minLevel) * (percent - 1) / 99.0);
        }

        /// <summary>Inverse of <see cref="LevelFromPercent"/>, for showing a BIOS level on the curve.</summary>
        public static int PercentFromLevel(int level, int minLevel, int maxLevel)
        {
            if (level <= StoppedLevel)
                return 0;
            if (maxLevel <= minLevel)
                return 100;
            return Math.Clamp((int)Math.Round(1 + (level - minLevel) * 99.0 / (maxLevel - minLevel)), 1, 100);
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

        /// <summary>Writes the curves; the file is replaced in one step so a crash can't leave it half written.</summary>
        public void Save() => Save(StorePath);

        internal void Save(string path)
        {
            var options = new JsonSerializerOptions { WriteIndented = true };
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, options));
            File.Move(temp, path, overwrite: true);
        }

        public static FanCurveProfile Load()
        {
            var path = StorePath;
            if (!File.Exists(path))
            {
                // A curve saved before the rename to OmenFanPlus.
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OmenHubLighterPlus", "fancurve.json");
            }
            return Load(path);
        }

        /// <summary>Reads a curve file; anything unreadable or out of range falls back to the default curve.</summary>
        internal static FanCurveProfile Load(string path)
        {
            try
            {
                if (!File.Exists(path)) return CreateDefault();
                var json = File.ReadAllText(path);
                var profile = JsonSerializer.Deserialize<FanCurveProfile>(json) ?? CreateDefault();
                profile.CpuCurve = Validated(profile.CpuCurve);
                profile.GpuCurve = Validated(profile.GpuCurve);
                return profile;
            }
            catch (Exception ex)
            {
                Services.AppLog.Warn($"Couldn't read the fan curve file; using the default curve ({ex.Message})");
                return CreateDefault();
            }
        }

        // The JSON reader bypasses the clamping constructor, so a hand-edited file can hold anything.
        private static List<FanCurvePoint> Validated(List<FanCurvePoint>? curve)
        {
            if (curve == null || curve.Count < 2)
                return DefaultCurve();
            return curve.Select(p => new FanCurvePoint(p.Temperature, p.FanSpeed)).ToList();
        }
    }
}
