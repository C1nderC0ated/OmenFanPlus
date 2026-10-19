using Hp.Omen.OmenCommonLib;
using Hp.Omen.OmenCommonLib.PowerControl.Enum;
using Xunit;

namespace OmenFanPlus.Tests;

public class OmenCapabilitiesTests
{
    private const int Ok = 0;
    private const int NotAvailable = 3;
    private const int WmiFailed = -1;
    private const int WrongSignature = 2;

    [Fact]
    public void An_OMEN_15_dc1xxx_gets_thermal_modes_only_with_Comfort_as_the_third_one()
    {
        // The user's board: 0x2D, 0x26 and 0x28 unsupported, 0x11 works.
        var caps = OmenCapabilities.FromReturnCodes(NotAvailable, NotAvailable, NotAvailable, Ok, "8574", ThermalPolicyVersion.V0);

        Assert.True(caps.BiosAvailable);
        Assert.True(caps.ProbeComplete);
        Assert.False(caps.ManualFanControlSupported);
        Assert.False(caps.MaxFanSupported);
        Assert.True(caps.FanRpmSupported);
        Assert.False(caps.IsVictusBoard);
        Assert.Equal(PerformanceMode.Cool, caps.QuietOrCoolMode);
    }

    [Theory]
    [InlineData("8A25")]
    [InlineData("8a25")]  // board IDs compare case-insensitively
    [InlineData("88F8")]
    public void A_Victus_board_gets_Quiet_as_the_third_mode(string systemId)
    {
        var caps = OmenCapabilities.FromReturnCodes(Ok, Ok, Ok, Ok, systemId, ThermalPolicyVersion.V1);

        Assert.True(caps.IsVictusBoard);
        Assert.Equal(PerformanceMode.Quiet, caps.QuietOrCoolMode);
        Assert.True(caps.ManualFanControlSupported);
        Assert.True(caps.MaxFanSupported);
    }

    [Fact]
    public void A_newer_OMEN_board_with_manual_fan_control_still_gets_Comfort_not_Quiet()
    {
        var caps = OmenCapabilities.FromReturnCodes(Ok, Ok, Ok, Ok, "8A14", ThermalPolicyVersion.V1);

        Assert.False(caps.IsVictusBoard);
        Assert.Equal(PerformanceMode.Cool, caps.QuietOrCoolMode);
        Assert.True(caps.ManualFanControlSupported);
    }

    [Theory]
    [InlineData(WmiFailed, NotAvailable, NotAvailable)]
    [InlineData(NotAvailable, WmiFailed, NotAvailable)]
    [InlineData(NotAvailable, NotAvailable, WmiFailed)]
    [InlineData(WrongSignature, NotAvailable, NotAvailable)]
    public void The_probe_is_incomplete_while_any_deciding_answer_is_transient(int fanLevel, int maxFan, int designData)
    {
        var caps = OmenCapabilities.FromReturnCodes(fanLevel, maxFan, designData, Ok, "8574", ThermalPolicyVersion.V0);

        Assert.False(caps.ProbeComplete);
        Assert.True(caps.BiosAvailable);
    }

    [Fact]
    public void A_transient_fan_rpm_answer_does_not_hold_the_probe_back()
    {
        var caps = OmenCapabilities.FromReturnCodes(NotAvailable, NotAvailable, NotAvailable, WmiFailed, "8574", ThermalPolicyVersion.V0);

        Assert.True(caps.ProbeComplete);
        Assert.False(caps.FanRpmSupported);
    }

    [Fact]
    public void Unavailable_means_no_answer_at_all()
    {
        var caps = OmenCapabilities.Unavailable();

        Assert.False(caps.BiosAvailable);
        Assert.False(caps.ProbeComplete);
        Assert.False(caps.ManualFanControlSupported);
        Assert.False(caps.FanRpmSupported);
    }

    [Fact]
    public void The_fan_level_band_defaults_to_OmenMons_observed_range()
    {
        var caps = OmenCapabilities.FromReturnCodes(Ok, Ok, Ok, Ok, "8A25", ThermalPolicyVersion.V1);

        Assert.Equal(20, caps.FanLevelMin);
        Assert.Equal(55, caps.FanLevelMax);
    }

    [Fact]
    public void ToString_names_the_third_mode_and_the_return_codes()
    {
        var caps = OmenCapabilities.FromReturnCodes(NotAvailable, NotAvailable, NotAvailable, Ok, "8574", ThermalPolicyVersion.V0);
        var text = caps.ToString();

        Assert.Contains("SystemID=8574", text);
        Assert.Contains("GetFanLevel(0x2D) rc=3", text);
        Assert.Contains("third mode=Cool", text);
    }
}
