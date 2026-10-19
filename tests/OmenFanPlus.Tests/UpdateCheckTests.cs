using System.Text.Json;
using OmenFanPlus.Services;
using Xunit;

namespace OmenFanPlus.Tests;

public class UpdateCheckTests
{
    // The shape of builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json, reduced to what is read.
    private const string Releases = """
        {
          "latest-runtime": "10.0.13",
          "releases": [
            { "release-version": "10.0.13", "security": false, "runtime": { "version": "10.0.13" } },
            { "release-version": "10.0.12", "security": true,  "runtime": { "version": "10.0.12" } },
            { "release-version": "10.0.11", "security": false, "runtime": { "version": "10.0.11" } },
            { "release-version": "10.0.0-rc.2.25502.107", "security": true, "runtime": { "version": "10.0.0-rc.2.25502.107" } }
          ]
        }
        """;

    private static JsonElement Root(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_skipped_security_release_between_current_and_latest_counts()
    {
        // 10.0.12 was a security release, so someone on 10.0.10 should be told so even though 10.0.13 isn't one.
        Assert.True(UpdateCheck.NewerReleaseHasSecurityFixes(Root(Releases), new Version(10, 0, 10)));
    }

    [Fact]
    public void Only_releases_newer_than_the_current_runtime_count()
    {
        // From 10.0.12 only 10.0.13 is newer, and that one has no security fixes.
        Assert.False(UpdateCheck.NewerReleaseHasSecurityFixes(Root(Releases), new Version(10, 0, 12)));
    }

    [Fact]
    public void Preview_versions_are_ignored_even_when_flagged_as_security()
    {
        const string json = """
            { "releases": [ { "release-version": "10.0.0-rc.2.25502.107", "security": true, "runtime": { "version": "10.0.0-rc.2.25502.107" } } ] }
            """;
        Assert.False(UpdateCheck.NewerReleaseHasSecurityFixes(Root(json), new Version(10, 0, 0)));
    }

    [Fact]
    public void A_missing_releases_array_means_no_security_claim()
    {
        Assert.False(UpdateCheck.NewerReleaseHasSecurityFixes(Root("{ \"latest-runtime\": \"10.0.13\" }"), new Version(10, 0, 1)));
    }

    [Fact]
    public void The_release_version_is_used_when_the_runtime_block_is_missing()
    {
        const string json = """
            { "releases": [ { "release-version": "10.0.9", "security": true } ] }
            """;
        Assert.True(UpdateCheck.NewerReleaseHasSecurityFixes(Root(json), new Version(10, 0, 5)));
        Assert.False(UpdateCheck.NewerReleaseHasSecurityFixes(Root(json), new Version(10, 0, 9)));
    }

    [Theory]
    [InlineData("2.2.0", "2.1.0", true)]
    [InlineData("v2.2.0", "2.2.0", false)]   // GitHub tags may carry a "v"
    [InlineData("2.2.0", "2.2.0", false)]
    [InlineData("10.0.13", "10.0.9", true)]  // numeric, not textual, comparison
    public void Release_tags_compare_as_versions(string tag, string installed, bool newer)
    {
        Assert.True(Version.TryParse(tag.TrimStart('v', 'V'), out var latest));
        Assert.Equal(newer, latest > Version.Parse(installed));
    }
}
