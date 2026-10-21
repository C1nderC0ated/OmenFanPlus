using OmenFanPlus.Forms;
using OmenFanPlus.Lib;
using Xunit;

namespace OmenFanPlus.Tests;

public class TrayTextTests
{
    [Fact]
    public void Temperatures_go_on_their_own_line_above_the_status()
    {
        var text = BackgroundForm.TrayText("Performance, applied 07:59", new TemperatureReading(67.4f, 61f, CpuTemperatureSource.CpuSensor));
        Assert.Equal("CPU 67 °C  GPU 61 °C\nOmenFanPlus - Performance, applied 07:59", text);
    }

    [Fact]
    public void A_missing_sensor_shows_as_not_available()
    {
        var text = BackgroundForm.TrayText("Default, applied 08:00", new TemperatureReading(55f, null, CpuTemperatureSource.AcpiThermalZone));
        Assert.StartsWith("CPU 55 °C  GPU n/a\n", text);
    }

    [Fact]
    public void Without_a_reading_only_the_status_is_shown()
    {
        Assert.Equal("OmenFanPlus - Auto: Default at 60 °C, applied 08:01", BackgroundForm.TrayText("Auto: Default at 60 °C, applied 08:01", null));
        Assert.Equal("OmenFanPlus", BackgroundForm.TrayText("", null));
        Assert.Equal("OmenFanPlus", BackgroundForm.TrayText("", new TemperatureReading(null, null, CpuTemperatureSource.None)));
    }

    [Fact]
    public void The_tooltip_never_exceeds_the_shells_limit()
    {
        var text = BackgroundForm.TrayText(new string('x', 200), new TemperatureReading(70f, 70f, CpuTemperatureSource.CpuSensor));
        Assert.Equal(127, text.Length);
    }
}
