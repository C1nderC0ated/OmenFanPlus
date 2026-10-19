using OmenFanPlus.Services;
using Xunit;

namespace OmenFanPlus.Tests;

public class StartupTaskTests
{
    private static readonly string ProgramFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    [Fact]
    public void The_copy_under_Program_Files_is_the_installed_one()
    {
        Assert.True(StartupTask.IsInstalledCopy(Path.Combine(ProgramFiles, "OmenFanPlus", "OmenFanPlus.exe")));
    }

    [Fact]
    public void The_check_ignores_letter_case()
    {
        Assert.True(StartupTask.IsInstalledCopy(Path.Combine(ProgramFiles, "OmenFanPlus", "OmenFanPlus.exe").ToUpperInvariant()));
    }

    [Theory]
    [InlineData(@"C:\Users\someone\Downloads\repo\OmenFanPlus\bin\Release\net10.0-windows\win-x64\OmenFanPlus.exe")]
    [InlineData(@"D:\Apps\OmenFanPlus\OmenFanPlus.exe")]
    [InlineData(@"C:\Program Files (x86)\OmenFanPlus\OmenFanPlus.exe")]  // the 32-bit folder is not Program Files
    [InlineData(@"C:\Program FilesX\OmenFanPlus\OmenFanPlus.exe")]       // a sibling folder with the same prefix
    public void Any_other_folder_is_not_installed(string exePath)
    {
        Assert.False(StartupTask.IsInstalledCopy(exePath));
    }
}
