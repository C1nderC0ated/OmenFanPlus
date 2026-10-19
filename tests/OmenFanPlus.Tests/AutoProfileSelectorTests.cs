using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using OmenFanPlus.Services;
using Xunit;

namespace OmenFanPlus.Tests;

public class AutoProfileSelectorTests
{
    private static readonly DateTime T0 = new(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);
    private static DateTime Later(int seconds) => T0.AddSeconds(seconds);

    private static AutoProfileSelector Selector(PerformanceMode calm = PerformanceMode.Cool) =>
        new(comfortBelow: 55, performanceAbove: 80, hysteresis: 5, calmProfile: calm);

    [Theory]
    [InlineData(40f, PerformanceMode.Cool)]
    [InlineData(54.9f, PerformanceMode.Cool)]
    [InlineData(55f, PerformanceMode.Default)]
    [InlineData(79.9f, PerformanceMode.Default)]
    [InlineData(80f, PerformanceMode.Performance)]
    [InlineData(95f, PerformanceMode.Performance)]
    public void The_first_reading_picks_the_band_directly(float temperature, PerformanceMode expected)
    {
        var s = Selector();
        Assert.Equal(expected, s.Next(temperature, onBattery: false, T0));
        Assert.Equal(expected, s.Current);
    }

    [Fact]
    public void Victus_boards_get_Quiet_as_the_calm_profile()
    {
        var s = Selector(PerformanceMode.Quiet);
        Assert.Equal(PerformanceMode.Quiet, s.Next(40f, false, T0));
    }

    [Fact]
    public void Stepping_up_happens_as_soon_as_a_threshold_is_reached()
    {
        var s = Selector();
        s.Next(40f, false, T0);
        Assert.Equal(PerformanceMode.Default, s.Next(55f, false, Later(20)));
        Assert.Equal(PerformanceMode.Performance, s.Next(80f, false, Later(40)));
    }

    [Fact]
    public void A_cold_start_under_load_can_jump_straight_to_Performance()
    {
        var s = Selector();
        s.Next(40f, false, T0);
        Assert.Equal(PerformanceMode.Performance, s.Next(90f, false, Later(20)));
    }

    [Fact]
    public void Stepping_down_waits_for_the_hysteresis()
    {
        var s = Selector();
        s.Next(85f, false, T0);                                                  // Performance
        Assert.Equal(PerformanceMode.Performance, s.Next(78f, false, Later(20)));  // 78 > 80 - 5: stays
        Assert.Equal(PerformanceMode.Performance, s.Next(76f, false, Later(40)));  // 76 > 75: stays
        Assert.Equal(PerformanceMode.Default, s.Next(75f, false, Later(60)));      // 75 <= 75: steps down
        Assert.Equal(PerformanceMode.Default, s.Next(52f, false, Later(80)));      // 52 > 55 - 5: stays
        Assert.Equal(PerformanceMode.Cool, s.Next(50f, false, Later(100)));        // 50 <= 50: steps down
    }

    [Fact]
    public void A_change_is_held_for_the_minimum_dwell_time()
    {
        var s = Selector();
        s.Next(40f, false, T0);                                                         // Cool
        Assert.Equal(PerformanceMode.Cool, s.Next(60f, false, Later(5)));               // wants Default, too soon
        Assert.Equal(PerformanceMode.Default, s.Next(60f, false, Later(15)));           // dwell over
        Assert.Equal(PerformanceMode.Default, s.Next(90f, false, Later(20)));           // too soon after the last change
        Assert.Equal(PerformanceMode.Performance, s.Next(90f, false, Later(30)));
    }

    [Fact]
    public void On_battery_Performance_is_never_chosen()
    {
        var s = Selector();
        Assert.Equal(PerformanceMode.Default, s.Next(95f, onBattery: true, T0));
        Assert.Equal(PerformanceMode.Default, s.Next(95f, onBattery: true, Later(60)));
        Assert.Equal(PerformanceMode.Performance, s.Next(95f, onBattery: false, Later(120)));
    }

    [Fact]
    public void Pulling_the_charger_leaves_Performance_at_once_without_waiting_for_the_dwell()
    {
        var s = Selector();
        s.Next(90f, false, T0);                                                    // Performance
        Assert.Equal(PerformanceMode.Default, s.Next(90f, onBattery: true, Later(2)));
    }

    [Fact]
    public void Reset_makes_the_next_reading_pick_the_band_again()
    {
        var s = Selector();
        s.Next(90f, false, T0);
        s.Reset();
        Assert.Null(s.Current);
        Assert.Equal(PerformanceMode.Cool, s.Next(40f, false, Later(1)));
    }

    [Theory]
    [InlineData(55, 80, 5, true)]
    [InlineData(30, 100, 15, true)]
    [InlineData(70, 75, 5, true)]    // exactly one hysteresis apart is allowed
    [InlineData(70, 74, 5, false)]   // less than one hysteresis apart: no Default band on the way down
    [InlineData(80, 55, 5, false)]   // reversed
    [InlineData(20, 80, 5, false)]   // below the minimum threshold
    [InlineData(55, 101, 5, false)]  // above the maximum threshold
    [InlineData(55, 80, 0, false)]   // hysteresis out of range
    [InlineData(55, 80, 16, false)]
    public void Thresholds_are_validated(int comfortBelow, int performanceAbove, int hysteresis, bool valid)
    {
        Assert.Equal(valid, AutoProfileSelector.TryValidate(comfortBelow, performanceAbove, hysteresis, out var error));
        Assert.Equal(valid, error == null);
        if (!valid)
            Assert.Throws<ArgumentException>(() => new AutoProfileSelector(comfortBelow, performanceAbove, hysteresis, PerformanceMode.Cool));
    }

    [Theory]
    [InlineData(50f, false, PerformanceMode.Cool)]
    [InlineData(60f, false, PerformanceMode.Default)]
    [InlineData(85f, false, PerformanceMode.Performance)]
    [InlineData(85f, true, PerformanceMode.Default)]
    public void Band_is_the_plain_threshold_answer_for_previews(float temperature, bool onBattery, PerformanceMode expected)
    {
        Assert.Equal(expected, Selector().Band(temperature, onBattery));
    }
}
