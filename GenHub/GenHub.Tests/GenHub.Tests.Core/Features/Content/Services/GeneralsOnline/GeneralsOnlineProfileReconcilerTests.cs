using GenHub.Core.Constants;
using GenHub.Core.Interfaces.Common;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Interfaces.GameProfiles;
using GenHub.Core.Interfaces.Manifest;
using GenHub.Core.Interfaces.Notifications;
using GenHub.Core.Interfaces.Providers;
using GenHub.Core.Models.Common;
using GenHub.Core.Models.Content;
using GenHub.Core.Models.Dialogs;
using GenHub.Core.Models.Enums;
using GenHub.Core.Models.Manifest;
using GenHub.Core.Models.Results;
using GenHub.Core.Models.Results.Content;
using GenHub.Features.Content.Services.GeneralsOnline;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Net.Http;

namespace GenHub.Tests.Core.Features.Content.Services.GeneralsOnline;

/// <summary>
/// Tests for <see cref="GeneralsOnlineProfileReconciler"/>.
/// </summary>
public class GeneralsOnlineProfileReconcilerTests
{
    private readonly Mock<GeneralsOnlineUpdateService> _updateServiceMock;
    private readonly Mock<IContentManifestPool> _manifestPoolMock;
    private readonly Mock<IContentOrchestrator> _contentOrchestratorMock;
    private readonly Mock<IContentReconciliationService> _reconciliationServiceMock;
    private readonly Mock<INotificationService> _notificationServiceMock;
    private readonly Mock<IDialogService> _dialogServiceMock;
    private readonly Mock<IUserSettingsService> _userSettingsServiceMock;
    private readonly Mock<IGameProfileManager> _profileManagerMock;

    private readonly GeneralsOnlineProfileReconciler _reconciler;

    /// <summary>
    /// Initializes a new instance of the <see cref="GeneralsOnlineProfileReconcilerTests"/> class.
    /// </summary>
    public GeneralsOnlineProfileReconcilerTests()
    {
        _manifestPoolMock = new Mock<IContentManifestPool>();

        _updateServiceMock = new Mock<GeneralsOnlineUpdateService>(
            NullLogger<GeneralsOnlineUpdateService>.Instance,
            _manifestPoolMock.Object,
            new Mock<IHttpClientFactory>().Object,
            new Mock<IProviderDefinitionLoader>().Object);

        _contentOrchestratorMock = new Mock<IContentOrchestrator>();
        _reconciliationServiceMock = new Mock<IContentReconciliationService>();
        _notificationServiceMock = new Mock<INotificationService>();
        _dialogServiceMock = new Mock<IDialogService>();
        _userSettingsServiceMock = new Mock<IUserSettingsService>();
        _profileManagerMock = new Mock<IGameProfileManager>();

        _reconciler = new GeneralsOnlineProfileReconciler(
            NullLogger<GeneralsOnlineProfileReconciler>.Instance,
            _updateServiceMock.Object,
            _manifestPoolMock.Object,
            _contentOrchestratorMock.Object,
            _reconciliationServiceMock.Object,
            _notificationServiceMock.Object,
            _dialogServiceMock.Object,
            _userSettingsServiceMock.Object,
            _profileManagerMock.Object);
    }

    /// <summary>
    /// Should ignore local manifests during reconciliation.
    /// </summary>
    /// <returns>A task representing the asynchronous operation.</returns>
    [Fact]
    public async Task CheckAndReconcile_ShouldIgnore_LocalManifests()
    {
        // Arrange
        string latestVersion = "0.0.99";
        _updateServiceMock.Setup(x => x.CheckForUpdatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ContentUpdateCheckResult.CreateUpdateAvailable(latestVersion, "0.0.1"));

        _userSettingsServiceMock.Setup(x => x.Get())
            .Returns(new UserSettings { AutoUpdateGeneralsOnline = true, DeleteOldGeneralsOnlineVersions = true });

        // Setup mocked local manifest that should be ignored
        var localManifest = new ContentManifest
        {
            Id = ManifestId.Create("1.0.local.gameclient.gen-online-copy"),
            Name = "My GeneralsOnline Copy",
            Version = "1.0",
            Publisher = new PublisherInfo { PublisherType = "local" },
        };

        _manifestPoolMock.Setup(x => x.GetAllManifestsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<IEnumerable<ContentManifest>>.CreateSuccess([localManifest]));

        // Setup mock acquisition (simplified for test)
        _contentOrchestratorMock.Setup(
                x => x.SearchAsync(It.IsAny<ContentSearchQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<IEnumerable<ContentSearchResult>>.CreateSuccess(
            [
                new() { Name = "New GO Version", Version = latestVersion },
            ]));

        _contentOrchestratorMock.Setup(x => x.AcquireContentAsync(It.IsAny<ContentSearchResult>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(OperationResult<ContentManifest>.CreateSuccess(new ContentManifest { Id = ManifestId.Create("1.0.generalsonline.gameclient.newversion"), Version = latestVersion }));

        // Act
        await _reconciler.CheckAndReconcileIfNeededAsync("profile1", CancellationToken.None);

        // Assert
        // Verify that RemoveManifestAsync was NEVER called for the local manifest
        _manifestPoolMock.Verify(
            x => x.RemoveManifestAsync(localManifest.Id, It.IsAny<CancellationToken>()),
            Times.Never,
            "Local manifest should not be removed during reconciliation");
    }
}
