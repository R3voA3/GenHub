using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Interfaces.GameProfiles;
using GenHub.Core.Interfaces.Manifest;
using GenHub.Core.Interfaces.Storage;
using GenHub.Core.Interfaces.Workspace;
using GenHub.Core.Models.Enums;
using GenHub.Core.Models.GameProfile;
using GenHub.Core.Models.Results;
using GenHub.Features.Content.Services;
using GenHub.Features.Storage.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace GenHub.Tests.Core.Features.Reconciliation;

/// <summary>
/// Tests to verify that workspace strategies are preserved during profile reconciliation.
/// This addresses the critical requirement that profiles must maintain their WorkspaceStrategy
/// (e.g., HardLink) when being updated through reconciliation processes.
/// </summary>
public class ReconciliationStrategyTests
{
    private readonly Mock<IGameProfileManager> _profileManagerMock;
    private readonly Mock<IWorkspaceManager> _workspaceManagerMock;
    private readonly Mock<IContentManifestPool> _manifestPoolMock;
    private readonly Mock<ICasService> _casServiceMock;
    private readonly Mock<ILogger<ContentReconciliationService>> _loggerMock;
    private readonly ContentReconciliationService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="ReconciliationStrategyTests"/> class.
    /// </summary>
    public ReconciliationStrategyTests()
    {
        _profileManagerMock = new Mock<IGameProfileManager>();
        _workspaceManagerMock = new Mock<IWorkspaceManager>();
        _manifestPoolMock = new Mock<IContentManifestPool>();
        _casServiceMock = new Mock<ICasService>();
        _loggerMock = new Mock<ILogger<ContentReconciliationService>>();

        _service = new ContentReconciliationService(
            _profileManagerMock.Object,
            _workspaceManagerMock.Object,
            _manifestPoolMock.Object,
            null!, // CasReferenceTracker not needed for bulk reconciliation
            _casServiceMock.Object,
            _loggerMock.Object);
    }

    /// <summary>
    /// Verifies that bulk manifest replacement preserves the HardLink strategy for a profile.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task ReconcileBulkManifestReplacement_WithHardLinkProfile_ShouldNotSetPreferredStrategy()
    {
        // Arrange
        var profileId = "profile_hardlink";
        var originalProfile = new GameProfile
        {
            Id = profileId,
            Name = "My HardLink Profile",
            WorkspaceStrategy = WorkspaceStrategy.HardLink,
            EnabledContentIds = ["old_manifest_id"],
        };

        _profileManagerMock.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<IReadOnlyList<GameProfile>>.CreateSuccess([originalProfile]));

        _profileManagerMock.Setup(x => x.UpdateProfileAsync(It.IsAny<string>(), It.IsAny<UpdateProfileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<GameProfile>.CreateSuccess(originalProfile));

        var replacements = new Dictionary<string, string> { { "old_manifest_id", "new_manifest_id" } };

        // Act
        await _service.ReconcileBulkManifestReplacementAsync(replacements);

        // Assert - Verify PreferredStrategy is NOT set (null), which preserves existing strategy
        _profileManagerMock.Verify(
            x => x.UpdateProfileAsync(
                profileId,
                It.Is<UpdateProfileRequest>(req => req.PreferredStrategy == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that bulk manifest replacement preserves the SymlinkOnly strategy for a profile.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task ReconcileBulkManifestReplacement_WithSymlinkProfile_ShouldNotSetPreferredStrategy()
    {
        // Arrange
        var profileId = "profile_symlink";
        var originalProfile = new GameProfile
        {
            Id = profileId,
            Name = "My Symlink Profile",
            WorkspaceStrategy = WorkspaceStrategy.SymlinkOnly,
            EnabledContentIds = ["old_manifest_id"],
        };

        _profileManagerMock.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<IReadOnlyList<GameProfile>>.CreateSuccess([originalProfile]));

        _profileManagerMock.Setup(x => x.UpdateProfileAsync(It.IsAny<string>(), It.IsAny<UpdateProfileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<GameProfile>.CreateSuccess(originalProfile));

        var replacements = new Dictionary<string, string> { { "old_manifest_id", "new_manifest_id" } };

        // Act
        await _service.ReconcileBulkManifestReplacementAsync(replacements);

        // Assert - Verify PreferredStrategy is NOT set, preserving Symlink strategy
        _profileManagerMock.Verify(
            x => x.UpdateProfileAsync(
                profileId,
                It.Is<UpdateProfileRequest>(req => req.PreferredStrategy == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that bulk manifest replacement preserves the FullCopy strategy for a profile.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task ReconcileBulkManifestReplacement_WithCopyProfile_ShouldNotSetPreferredStrategy()
    {
        // Arrange
        var profileId = "profile_copy";
        var originalProfile = new GameProfile
        {
            Id = profileId,
            Name = "My Copy Profile",
            WorkspaceStrategy = WorkspaceStrategy.FullCopy,
            EnabledContentIds = ["old_manifest_id"],
        };

        _profileManagerMock.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<IReadOnlyList<GameProfile>>.CreateSuccess([originalProfile]));

        _profileManagerMock.Setup(x => x.UpdateProfileAsync(It.IsAny<string>(), It.IsAny<UpdateProfileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<GameProfile>.CreateSuccess(originalProfile));

        var replacements = new Dictionary<string, string> { { "old_manifest_id", "new_manifest_id" } };

        // Act
        await _service.ReconcileBulkManifestReplacementAsync(replacements);

        // Assert - Verify PreferredStrategy is NOT set, preserving Copy strategy
        _profileManagerMock.Verify(
            x => x.UpdateProfileAsync(
                profileId,
                It.Is<UpdateProfileRequest>(req => req.PreferredStrategy == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Verifies that bulk manifest replacement preserves different strategies across multiple profiles.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task ReconcileBulkManifestReplacement_WithMultipleProfiles_ShouldPreserveAllStrategies()
    {
        // Arrange
        var profiles = new[]
        {
            new GameProfile
            {
                Id = "profile_1",
                Name = "HardLink Profile",
                WorkspaceStrategy = WorkspaceStrategy.HardLink,
                EnabledContentIds = ["old_manifest_id"],
            },
            new GameProfile
            {
                Id = "profile_2",
                Name = "Symlink Profile",
                WorkspaceStrategy = WorkspaceStrategy.SymlinkOnly,
                EnabledContentIds = ["old_manifest_id"],
            },
            new GameProfile
            {
                Id = "profile_3",
                Name = "Copy Profile",
                WorkspaceStrategy = WorkspaceStrategy.FullCopy,
                EnabledContentIds = ["old_manifest_id"],
            },
        };

        _profileManagerMock.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<IReadOnlyList<GameProfile>>.CreateSuccess(profiles));

        foreach (var profile in profiles)
        {
            var capturedProfile = profile; // Capture for closure
            _profileManagerMock.Setup(x => x.UpdateProfileAsync(capturedProfile.Id, It.IsAny<UpdateProfileRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(ProfileOperationResult<GameProfile>.CreateSuccess(capturedProfile));
        }

        var replacements = new Dictionary<string, string> { { "old_manifest_id", "new_manifest_id" } };

        // Act
        await _service.ReconcileBulkManifestReplacementAsync(replacements);

        // Assert - Verify all profiles were updated without setting PreferredStrategy
        _profileManagerMock.Verify(
            x => x.UpdateProfileAsync(
                It.IsAny<string>(),
                It.Is<UpdateProfileRequest>(req => req.PreferredStrategy == null),
                It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    /// <summary>
    /// Verifies that manifest removal preserves the workspace strategy.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task ReconcileManifestRemoval_ShouldNotSetPreferredStrategy()
    {
        // Arrange
        var profileId = "profile_hardlink";
        var originalProfile = new GameProfile
        {
            Id = profileId,
            Name = "My HardLink Profile",
            WorkspaceStrategy = WorkspaceStrategy.HardLink,
            EnabledContentIds = ["manifest_to_remove", "other_manifest"],
        };

        _profileManagerMock.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<IReadOnlyList<GameProfile>>.CreateSuccess([originalProfile]));

        _profileManagerMock.Setup(x => x.UpdateProfileAsync(It.IsAny<string>(), It.IsAny<UpdateProfileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<GameProfile>.CreateSuccess(originalProfile));

        // Act
        await _service.ReconcileManifestRemovalAsync("manifest_to_remove");

        // Assert - Verify PreferredStrategy is NOT set during removal
        _profileManagerMock.Verify(
            x => x.UpdateProfileAsync(
                profileId,
                It.Is<UpdateProfileRequest>(req => req.PreferredStrategy == null),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
