using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using GenHub.Core.Interfaces.GameProfiles;
using GenHub.Core.Interfaces.Manifest;
using GenHub.Core.Interfaces.Storage;
using GenHub.Core.Interfaces.Workspace;
using GenHub.Core.Models.GameClients;
using GenHub.Core.Models.GameProfile;
using GenHub.Core.Models.Manifest;
using GenHub.Core.Models.Results;
using GenHub.Core.Models.Storage;
using GenHub.Features.Content.Services;
using GenHub.Features.Storage.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace GenHub.Tests.Core.Integration;

/// <summary>
/// Integration tests for local content update orchestration.
/// </summary>
public class LocalContentUpdateIntegrationTests
{
    private readonly Mock<IGameProfileManager> _profileManagerMock;
    private readonly Mock<IWorkspaceManager> _workspaceManagerMock;
    private readonly Mock<IContentManifestPool> _manifestPoolMock;
    private readonly Mock<ICasService> _casServiceMock;
    private readonly Mock<ILogger<ContentReconciliationService>> _loggerMock;
    private readonly CasReferenceTracker _casReferenceTracker;
    private readonly ContentReconciliationService _service;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalContentUpdateIntegrationTests"/> class.
    /// </summary>
    public LocalContentUpdateIntegrationTests()
    {
        _profileManagerMock = new Mock<IGameProfileManager>();
        _workspaceManagerMock = new Mock<IWorkspaceManager>();
        _manifestPoolMock = new Mock<IContentManifestPool>();
        _casServiceMock = new Mock<ICasService>();
        _loggerMock = new Mock<ILogger<ContentReconciliationService>>();

        var casConfig = new Mock<IOptions<CasConfiguration>>();
        casConfig.Setup(x => x.Value).Returns(new CasConfiguration { CasRootPath = "test-cas" });
        _casReferenceTracker = new CasReferenceTracker(casConfig.Object, Mock.Of<ILogger<CasReferenceTracker>>());

        _service = new ContentReconciliationService(
            _profileManagerMock.Object,
            _workspaceManagerMock.Object,
            _manifestPoolMock.Object,
            _casReferenceTracker,
            _casServiceMock.Object,
            _loggerMock.Object);

        // Default mock behaviors
        _profileManagerMock.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
             .ReturnsAsync(ProfileOperationResult<IReadOnlyList<GameProfile>>.CreateSuccess([]));

        _manifestPoolMock.Setup(x => x.RemoveManifestAsync(It.IsAny<ManifestId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<bool>.CreateSuccess(true));

        _manifestPoolMock.Setup(x => x.AddManifestAsync(It.IsAny<ContentManifest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<bool>.CreateSuccess(true));
    }

    /// <summary>
    /// Verifies that profile update orchestration correctly adds the new manifest to the pool and updates the profile's game client.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task OrchestrateLocalUpdateAsync_WhenIdChanges_ShouldAddManifestToPoolAndUpdateProfile()
    {
        // Arrange
        var oldId = "1.0.local.gameclient.old";
        var newId = "1.0.local.gameclient.new";

        var newManifest = new ContentManifest
        {
            Id = ManifestId.Create(newId),
            Name = "New Content",
            TargetGame = GenHub.Core.Models.Enums.GameType.ZeroHour,
            ContentType = GenHub.Core.Models.Enums.ContentType.GameClient,
        };

        var profile = new GameProfile
        {
            Id = "profile-1",
            Name = "Test Profile",
            GameClient = new GameClient { Id = oldId, Name = "Old Content" },
            EnabledContentIds = [],
        };

        _profileManagerMock.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<IReadOnlyList<GameProfile>>.CreateSuccess([profile]));

        _profileManagerMock.Setup(x => x.UpdateProfileAsync(It.IsAny<string>(), It.IsAny<UpdateProfileRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<GameProfile>.CreateSuccess(profile));

        // Mock GetManifestAsync to return the new manifest (simulating successful addition)
        _manifestPoolMock.Setup(x => x.GetManifestAsync(It.Is<ManifestId>(id => id.Value == newId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<ContentManifest?>.CreateSuccess(newManifest));

        // Act
        var result = await _service.OrchestrateLocalUpdateAsync(oldId, newManifest);

        // Assert
        result.Success.Should().BeTrue();

        // 1. Verify Manifest added to pool
        _manifestPoolMock.Verify(
            x => x.AddManifestAsync(newManifest, It.IsAny<CancellationToken>()),
            Times.Once,
            "Should explicitly add new manifest to pool before reconciling");

        // 2. Verify Profile updated with NEW GameClient manifest
        _profileManagerMock.Verify(
            x => x.UpdateProfileAsync(
                profile.Id,
                It.Is<UpdateProfileRequest>(req => req.GameClient != null && req.GameClient.Id == newId),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Should update profile definition to point to new GameClient ID");
    }

    /// <summary>
    /// Verifies that profile update orchestration skips updating the profile if the new manifest cannot be resolved from the pool.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task OrchestrateLocalUpdateAsync_WhenManifestResolutionFails_ShouldSkipProfileUpdate()
    {
         // Arrange
        var oldId = "1.0.local.gameclient.old";
        var newId = "1.0.local.gameclient.new";

        var newManifest = new ContentManifest
        {
            Id = ManifestId.Create(newId),
            Name = "New Content",
            TargetGame = GenHub.Core.Models.Enums.GameType.ZeroHour,
            ContentType = GenHub.Core.Models.Enums.ContentType.GameClient,
        };

        var profile = new GameProfile
        {
            Id = "profile-1",
            Name = "Test Profile",
            GameClient = new GameClient { Id = oldId, Name = "Old Content" },
            EnabledContentIds = [],
        };

        _profileManagerMock.Setup(x => x.GetAllProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProfileOperationResult<IReadOnlyList<GameProfile>>.CreateSuccess([profile]));

        // Mock GetManifestAsync to FAIL or return NULL (Simulating race condition or storage failure)
        _manifestPoolMock.Setup(x => x.GetManifestAsync(It.Is<ManifestId>(id => id.Value == newId), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<ContentManifest?>.CreateFailure("Not found"));

        // OR .ReturnsAsync(OperationResult<ContentManifest?>.CreateSuccess(null));

        // Act
        var result = await _service.OrchestrateLocalUpdateAsync(oldId, newManifest);

        // Assert
        result.Success.Should().BeTrue(); // The orchestration itself succeeds (best effort)

        // 1. Verify AddManifest called
        _manifestPoolMock.Verify(x => x.AddManifestAsync(newManifest, It.IsAny<CancellationToken>()), Times.Once);

        // 2. Verify UpdateProfileAsync is NEVER called (Safety Check)
        _profileManagerMock.Verify(
            x => x.UpdateProfileAsync(
                It.IsAny<string>(),
                It.IsAny<UpdateProfileRequest>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "Should NOT update profile if new manifest cannot be resolved, to prevent corruption");
    }
}
