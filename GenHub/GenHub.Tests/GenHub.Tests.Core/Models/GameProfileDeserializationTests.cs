using System.Text.Json;
using GenHub.Core.Constants;
using GenHub.Core.Models.Enums;
using Xunit;
using GameProfileModel = GenHub.Core.Models.GameProfile.GameProfile;

namespace GenHub.Tests.Core.Models;

/// <summary>
/// Tests to verify that GameProfile correctly applies default values during deserialization.
/// This addresses the bug where WorkspaceStrategy was defaulting to SymlinkOnly (enum default 0)
/// instead of the configured default (HardLink) when loading from JSON.
/// </summary>
public class GameProfileDeserializationTests
{
    /// <summary>
    /// Verifies that deserialization defaults to HardLink when WorkspaceStrategy is missing.
    /// </summary>
    [Fact]
    public void Deserialize_ProfileWithoutWorkspaceStrategy_ShouldDefaultToHardLink()
    {
        // Arrange - JSON without WorkspaceStrategy property
        var json = """
        {
            "Id": "test_profile",
            "Name": "Test Profile",
            "Description": "Test description"
        }
        """;

        // Act
        var profile = JsonSerializer.Deserialize<GameProfileModel>(json);

        // Assert
        Assert.NotNull(profile);
        Assert.Equal(WorkspaceStrategy.HardLink, profile.WorkspaceStrategy);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceStrategy, profile.WorkspaceStrategy);
    }

    /// <summary>
    /// Verifies that SymlinkOnly is overridden to HardLink during deserialization.
    /// </summary>
    [Fact]
    public void Deserialize_ProfileWithSymlinkOnly_ShouldOverrideToHardLink()
    {
        // Arrange - JSON with explicit SymlinkOnly (which is the problematic default)
        var json = """
        {
            "Id": "test_profile",
            "Name": "Test Profile",
            "WorkspaceStrategy": 0
        }
        """;

        // Act
        var profile = JsonSerializer.Deserialize<GameProfileModel>(json);

        // Assert
        Assert.NotNull(profile);

        // The OnDeserialized hook should override SymlinkOnly to HardLink
        Assert.Equal(WorkspaceStrategy.HardLink, profile.WorkspaceStrategy);
    }

    /// <summary>
    /// Verifies that explicit HardLink is preserved during deserialization.
    /// </summary>
    [Fact]
    public void Deserialize_ProfileWithExplicitHardLink_ShouldPreserveHardLink()
    {
        // Arrange - JSON with explicit HardLink
        var json = """
        {
            "Id": "test_profile",
            "Name": "Test Profile",
            "WorkspaceStrategy": 2
        }
        """;

        // Act
        var profile = JsonSerializer.Deserialize<GameProfileModel>(json);

        // Assert
        Assert.NotNull(profile);
        Assert.Equal(WorkspaceStrategy.HardLink, profile.WorkspaceStrategy);
    }

    /// <summary>
    /// Verifies that FullCopy strategy is preserved during deserialization.
    /// </summary>
    [Fact]
    public void Deserialize_ProfileWithCopyStrategy_ShouldPreserveCopy()
    {
        // Arrange - JSON with explicit Copy strategy
        var json = """
        {
            "Id": "test_profile",
            "Name": "Test Profile",
            "WorkspaceStrategy": 1
        }
        """;

        // Act
        var profile = JsonSerializer.Deserialize<GameProfileModel>(json);

        // Assert
        Assert.NotNull(profile);
        Assert.Equal(WorkspaceStrategy.FullCopy, profile.WorkspaceStrategy);
    }

    /// <summary>
    /// Verifies that WorkspaceStrategy is preserved after a serialization round-trip.
    /// </summary>
    [Fact]
    public void Serialize_ThenDeserialize_ShouldPreserveWorkspaceStrategy()
    {
        // Arrange
        var originalProfile = new GameProfileModel
        {
            Id = "test_profile",
            Name = "Test Profile",
            WorkspaceStrategy = WorkspaceStrategy.HardLink,
        };

        // Act - Round trip through JSON
        var json = JsonSerializer.Serialize(originalProfile);
        var deserializedProfile = JsonSerializer.Deserialize<GameProfileModel>(json);

        // Assert
        Assert.NotNull(deserializedProfile);
        Assert.Equal(WorkspaceStrategy.HardLink, deserializedProfile.WorkspaceStrategy);
        Assert.Equal(originalProfile.WorkspaceStrategy, deserializedProfile.WorkspaceStrategy);
    }

    /// <summary>
    /// Verifies that a new profile instance defaults to HardLink.
    /// </summary>
    [Fact]
    public void NewProfile_ShouldDefaultToHardLink()
    {
        // Arrange & Act
        var profile = new GameProfileModel
        {
            Id = "test_profile",
            Name = "Test Profile",
        };

        // Assert
        Assert.Equal(WorkspaceStrategy.HardLink, profile.WorkspaceStrategy);
        Assert.Equal(WorkspaceConstants.DefaultWorkspaceStrategy, profile.WorkspaceStrategy);
    }
}
