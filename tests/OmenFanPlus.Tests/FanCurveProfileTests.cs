using OmenFanPlus.Models;
using Xunit;

namespace OmenFanPlus.Tests;

public class FanCurveProfileTests
{
    private const int Min = 20;
    private const int Max = 55;

    [Theory]
    [InlineData(20f, 0)]    // below the first point: its speed
    [InlineData(30f, 0)]    // on the first point
    [InlineData(60f, 45)]   // halfway between (50, 30) and (70, 60)
    [InlineData(85f, 100)]  // on the last point
    [InlineData(95f, 100)]  // above the last point: its speed
    public void GetFanSpeed_interpolates_the_default_curve(float temperature, int expected)
    {
        var profile = FanCurveProfile.CreateDefault();
        Assert.Equal(expected, profile.GetFanSpeed(profile.CpuCurve, temperature));
    }

    [Fact]
    public void GetFanSpeed_uses_the_points_in_temperature_order_however_they_were_entered()
    {
        var profile = new FanCurveProfile();
        var curve = new List<FanCurvePoint> { new(80, 100), new(40, 20) };
        Assert.Equal(60, profile.GetFanSpeed(curve, 60f));
    }

    [Fact]
    public void GetFanSpeed_with_two_points_at_the_same_temperature_does_not_divide_by_zero()
    {
        var profile = new FanCurveProfile();
        var curve = new List<FanCurvePoint> { new(50, 20), new(50, 80) };
        var speed = profile.GetFanSpeed(curve, 50f);
        Assert.InRange(speed, 20, 80);
    }

    [Fact]
    public void GetFanSpeed_treats_a_missing_curve_as_full_speed_not_stopped_fans()
    {
        var profile = new FanCurveProfile();
        Assert.Equal(100, profile.GetFanSpeed(null!, 70f));
        Assert.Equal(100, profile.GetFanSpeed(new List<FanCurvePoint>(), 70f));
    }

    [Theory]
    [InlineData(0, FanCurveProfile.StoppedLevel)]
    [InlineData(1, Min)]
    [InlineData(100, Max)]
    [InlineData(50, 37)]
    [InlineData(-10, FanCurveProfile.StoppedLevel)]  // clamped
    [InlineData(250, Max)]                          // clamped
    public void LevelFromPercent_maps_the_percent_scale_onto_the_fan_level_band(int percent, int expectedLevel)
    {
        Assert.Equal(expectedLevel, FanCurveProfile.LevelFromPercent(percent, Min, Max));
    }

    [Theory]
    [InlineData(FanCurveProfile.StoppedLevel, 0)]
    [InlineData(0, 0)]
    [InlineData(Min, 1)]
    [InlineData(Max, 100)]
    [InlineData(70, 100)]  // above the band: clamped
    public void PercentFromLevel_is_the_inverse_mapping(int level, int expectedPercent)
    {
        Assert.Equal(expectedPercent, FanCurveProfile.PercentFromLevel(level, Min, Max));
    }

    [Fact]
    public void Percent_to_level_and_back_stays_within_the_rounding_of_one_level()
    {
        for (var percent = 1; percent <= 100; percent++)
        {
            var level = FanCurveProfile.LevelFromPercent(percent, Min, Max);
            Assert.InRange(level, Min, Max);
            var back = FanCurveProfile.PercentFromLevel(level, Min, Max);
            Assert.InRange(back, percent - 2, percent + 2);
        }
    }

    [Fact]
    public void PercentFromLevel_with_an_empty_band_does_not_divide_by_zero()
    {
        Assert.Equal(100, FanCurveProfile.PercentFromLevel(30, 30, 30));
    }

    [Fact]
    public void Save_then_Load_round_trips_and_leaves_no_temp_file()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "fancurve.json");
        var profile = new FanCurveProfile
        {
            CpuCurve = new List<FanCurvePoint> { new(40, 10), new(80, 90) },
            GpuCurve = new List<FanCurvePoint> { new(35, 5), new(75, 95), new(90, 100) },
        };

        profile.Save(path);
        var loaded = FanCurveProfile.Load(path);

        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal(new[] { (40, 10), (80, 90) }, loaded.CpuCurve.Select(p => (p.Temperature, p.FanSpeed)));
        Assert.Equal(new[] { (35, 5), (75, 95), (90, 100) }, loaded.GpuCurve.Select(p => (p.Temperature, p.FanSpeed)));
    }

    [Fact]
    public void Load_of_a_missing_file_gives_the_default_curve()
    {
        using var dir = new TempDir();
        var loaded = FanCurveProfile.Load(Path.Combine(dir.Path, "nothing.json"));
        Assert.Equal(FanCurveProfile.CreateDefault().CpuCurve.Count, loaded.CpuCurve.Count);
    }

    [Theory]
    [InlineData("{ \"CpuCurve\": null, \"GpuCurve\": [ {\"Temperature\": 40, \"FanSpeed\": 50}, {\"Temperature\": 60, \"FanSpeed\": 70} ] }")]
    [InlineData("{ \"CpuCurve\": [ {\"Temperature\": 40, \"FanSpeed\": 50} ], \"GpuCurve\": [ {\"Temperature\": 40, \"FanSpeed\": 50}, {\"Temperature\": 60, \"FanSpeed\": 70} ] }")]
    [InlineData("{ \"GpuCurve\": [ {\"Temperature\": 40, \"FanSpeed\": 50}, {\"Temperature\": 60, \"FanSpeed\": 70} ] }")]
    public void Load_replaces_a_null_missing_or_single_point_curve_with_the_default_and_keeps_the_other(string json)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "fancurve.json");
        File.WriteAllText(path, json);

        var loaded = FanCurveProfile.Load(path);

        var defaults = FanCurveProfile.CreateDefault().CpuCurve.Select(p => (p.Temperature, p.FanSpeed));
        Assert.Equal(defaults, loaded.CpuCurve.Select(p => (p.Temperature, p.FanSpeed)));
        Assert.Equal(new[] { (40, 50), (60, 70) }, loaded.GpuCurve.Select(p => (p.Temperature, p.FanSpeed)));
    }

    [Fact]
    public void Load_clamps_points_that_are_out_of_range()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "fancurve.json");
        File.WriteAllText(path,
            "{ \"CpuCurve\": [ {\"Temperature\": -20, \"FanSpeed\": 300}, {\"Temperature\": 150, \"FanSpeed\": -5} ]," +
            "  \"GpuCurve\": [ {\"Temperature\": 30, \"FanSpeed\": 0}, {\"Temperature\": 80, \"FanSpeed\": 100} ] }");

        var loaded = FanCurveProfile.Load(path);

        Assert.Equal(new[] { (0, 100), (100, 0) }, loaded.CpuCurve.Select(p => (p.Temperature, p.FanSpeed)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("{ \"CpuCurve\": \"oops\" }")]
    public void Load_of_an_unreadable_file_gives_the_default_curve_instead_of_throwing(string content)
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "fancurve.json");
        File.WriteAllText(path, content);

        var loaded = FanCurveProfile.Load(path);

        Assert.Equal(FanCurveProfile.CreateDefault().GpuCurve.Count, loaded.GpuCurve.Count);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OmenFanPlus.Tests-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }
}
