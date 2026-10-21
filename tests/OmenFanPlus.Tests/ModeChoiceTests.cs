using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using OmenFanPlus.Services;
using Xunit;

namespace OmenFanPlus.Tests;

public class ModeChoiceTests
{
    [Theory]
    [InlineData("Comfort", ModeChoice.Comfort)]
    [InlineData("comfort", ModeChoice.Comfort)]
    [InlineData("Cool", ModeChoice.Comfort)]      // the firmware's name
    [InlineData("Quiet", ModeChoice.Comfort)]     // the Victus name
    [InlineData("Default", ModeChoice.Default)]
    [InlineData("Performance", ModeChoice.Performance)]
    [InlineData(" Auto ", ModeChoice.Auto)]
    public void Settings_text_is_read_leniently(string text, ModeChoice expected)
    {
        Assert.Equal(expected, ModeChoices.Parse(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Max")]
    [InlineData("7")]
    public void Unknown_settings_text_gives_nothing(string? text)
    {
        Assert.Null(ModeChoices.Parse(text));
    }

    [Fact]
    public void Keys_round_trip_through_parse()
    {
        foreach (var choice in ModeChoices.All)
            Assert.Equal(choice, ModeChoices.Parse(ModeChoices.Key(choice)));
    }

    [Theory]
    [InlineData(false, PerformanceMode.Cool, false, ModeChoice.Comfort)]
    [InlineData(false, PerformanceMode.Quiet, false, ModeChoice.Comfort)]
    [InlineData(false, PerformanceMode.Default, false, ModeChoice.Default)]
    [InlineData(false, PerformanceMode.Performance, false, ModeChoice.Performance)]
    [InlineData(true, PerformanceMode.Performance, false, ModeChoice.Auto)]   // Auto wins over the saved profile
    [InlineData(false, PerformanceMode.Default, true, null)]                  // Max/Off/Custom active
    [InlineData(false, PerformanceMode.Extreme, false, null)]                 // not a choice the cycle knows
    public void The_saved_mode_maps_to_a_choice(bool fanAuto, PerformanceMode mode, bool manualActive, ModeChoice? expected)
    {
        Assert.Equal(expected, ModeChoices.FromSaved(fanAuto, mode, manualActive));
    }

    [Theory]
    [InlineData(ModeChoice.Comfort, PerformanceMode.Cool, PerformanceMode.Cool)]
    [InlineData(ModeChoice.Comfort, PerformanceMode.Quiet, PerformanceMode.Quiet)]
    [InlineData(ModeChoice.Default, PerformanceMode.Cool, PerformanceMode.Default)]
    [InlineData(ModeChoice.Performance, PerformanceMode.Cool, PerformanceMode.Performance)]
    public void Choices_map_to_the_boards_profiles(ModeChoice choice, PerformanceMode calm, PerformanceMode expected)
    {
        Assert.Equal(expected, ModeChoices.ToPerformanceMode(choice, calm));
    }

    [Fact]
    public void Auto_has_no_single_profile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ModeChoices.ToPerformanceMode(ModeChoice.Auto, PerformanceMode.Cool));
    }

    [Theory]
    [InlineData(ModeChoice.Comfort, PerformanceMode.Cool, "Comfort")]
    [InlineData(ModeChoice.Comfort, PerformanceMode.Quiet, "Quiet")]
    [InlineData(ModeChoice.Default, PerformanceMode.Cool, "Default")]
    [InlineData(ModeChoice.Performance, PerformanceMode.Quiet, "Performance")]
    [InlineData(ModeChoice.Auto, PerformanceMode.Cool, "Auto")]
    public void Display_names_follow_the_board(ModeChoice choice, PerformanceMode calm, string name)
    {
        Assert.Equal(name, ModeChoices.DisplayName(choice, calm));
    }

    [Fact]
    public void The_OMEN_key_cycles_through_all_four_on_the_charger()
    {
        ModeChoice? current = null;
        var seen = new List<ModeChoice>();
        for (var i = 0; i < 5; i++)
        {
            current = ModeChoices.Next(current, onBattery: false);
            seen.Add(current.Value);
        }
        Assert.Equal(new[] { ModeChoice.Comfort, ModeChoice.Default, ModeChoice.Performance, ModeChoice.Auto, ModeChoice.Comfort }, seen);
    }

    [Fact]
    public void On_battery_the_cycle_skips_Performance()
    {
        Assert.Equal(ModeChoice.Auto, ModeChoices.Next(ModeChoice.Default, onBattery: true));
        Assert.Equal(ModeChoice.Comfort, ModeChoices.Next(ModeChoice.Auto, onBattery: true));
        Assert.Equal(ModeChoice.Default, ModeChoices.Next(ModeChoice.Comfort, onBattery: true));
    }

    [Fact]
    public void From_a_manual_state_the_cycle_starts_at_Comfort()
    {
        Assert.Equal(ModeChoice.Comfort, ModeChoices.Next(null, onBattery: false));
    }
}
