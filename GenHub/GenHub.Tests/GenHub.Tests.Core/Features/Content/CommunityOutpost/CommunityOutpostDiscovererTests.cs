using GenHub.Core.Constants;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Models.Content;
using GenHub.Core.Models.Enums;
using GenHub.Features.Content.Services.CommunityOutpost;
using Microsoft.Extensions.Logging;
using Moq;

namespace GenHub.Tests.Core.Features.Content.CommunityOutpost;

/// <summary>
/// Tests for CommunityOutpostDiscoverer to verify Community Patch discovery.
/// </summary>
public class CommunityOutpostDiscovererTests
{
    [Fact]
    public void CommunityPatchRegex_MatchesGeneralsZhDateFilePattern()
    {
        // Arrange
        var htmlContent = @"<a href=""https://legi.cc/patch/generalszh-2026-01-28.zip"">Download Latest</a>";
        var regex = typeof(CommunityOutpostDiscoverer)
            .GetMethod("CommunityPatchRegex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, null) as System.Text.RegularExpressions.Regex;

        // Act
        var match = regex?.Match(htmlContent);

        // Assert
        Assert.NotNull(match);
        Assert.True(match.Success);
        Assert.Contains("generalszh-2026-01-28.zip", match.Groups[1].Value);
        Assert.Equal("2026-01-28", match.Groups[2].Value);
    }

    [Fact]
    public void CommunityPatchRegex_MatchesWeeklyFilenamePattern()
    {
        // Arrange
        var htmlContent = @"<a href=""generalszh-weekly-2026-01-28.zip"">Download</a>";
        var regex = typeof(CommunityOutpostDiscoverer)
            .GetMethod("CommunityPatchRegex", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.Invoke(null, null) as System.Text.RegularExpressions.Regex;

        // Act
        var match = regex?.Match(htmlContent);

        // Assert
        Assert.NotNull(match);
        Assert.True(match.Success);
        Assert.Contains("generalszh-weekly-2026-01-28.zip", match.Groups[1].Value);
        Assert.Equal("2026-01-28", match.Groups[2].Value);
    }

    [Fact]
    public void CommunityPatchId_FollowsFiveSegmentFormat()
    {
        // Arrange
        var versionDate = "2026-01-28";
        var providerName = CommunityOutpostConstants.PublisherType;
        var expectedId = $"1.{versionDate.Replace("-", string.Empty)}.{providerName}.gameclient.community-patch";

        // Act
        var segments = expectedId.Split('.');

        // Assert
        Assert.Equal(5, segments.Length);
        Assert.Equal("1", segments[0]); // schema version
        Assert.Equal("20260128", segments[1]); // user version (date)
        Assert.Equal("communityoutpost", segments[2]); // publisher
        Assert.Equal("gameclient", segments[3]); // content type
        Assert.Equal("community-patch", segments[4]); // content name
    }
}
