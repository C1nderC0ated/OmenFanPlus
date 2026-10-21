using System.Drawing;
using OmenFanPlus.Services;
using Xunit;

namespace OmenFanPlus.Tests;

public class TrayIconRendererTests
{
    [Theory]
    [InlineData(67f, 16)]
    [InlineData(5f, 16)]
    [InlineData(105f, 16)]
    [InlineData(48.6f, 20)]
    [InlineData(99f, 32)]
    public void The_badge_fills_the_requested_size_with_rounded_corners(float temperature, int size)
    {
        using var bitmap = TrayIconRenderer.Render(temperature, size);

        Assert.Equal(size, bitmap.Width);
        Assert.Equal(size, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(0, 0).A);                                  // corner is cut off
        Assert.True(bitmap.GetPixel(size / 2, 1).A > 200, "the top edge is filled");
        Assert.True(bitmap.GetPixel(1, size / 2).A > 200, "the left edge is filled");
    }

    [Fact]
    public void The_digits_are_drawn_in_white_over_the_purple()
    {
        using var bitmap = TrayIconRenderer.Render(88f, 32);
        var white = 0;
        var purple = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var c = bitmap.GetPixel(x, y);
            if (c.A < 200) continue;
            if (c.R > 230 && c.G > 230 && c.B > 230) white++;
            else if (Math.Abs(c.R - TrayIconRenderer.Accent.R) < 8 && Math.Abs(c.B - TrayIconRenderer.Accent.B) < 8) purple++;
        }
        Assert.True(white > 20, $"only {white} white pixels: no digits drawn");
        Assert.True(purple > white, "the badge should be mostly purple");
    }

    [Fact]
    public void Odd_temperatures_are_clamped_and_rounded_rather_than_refused()
    {
        using var low = TrayIconRenderer.Render(-12f, 16);
        using var high = TrayIconRenderer.Render(1234f, 16);
        using var fraction = TrayIconRenderer.Render(66.5f, 16);
        Assert.Equal(16, low.Width);
        Assert.Equal(16, high.Width);
        Assert.Equal(16, fraction.Width);
    }

    [Fact]
    public void A_badge_too_small_for_digits_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TrayIconRenderer.Render(50f, 4));
    }
}
