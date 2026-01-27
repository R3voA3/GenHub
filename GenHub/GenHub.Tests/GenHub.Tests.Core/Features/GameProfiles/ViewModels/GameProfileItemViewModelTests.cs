using GenHub.Core.Interfaces.GameProfiles;
using GenHub.Features.GameProfiles.ViewModels;
using Moq;

namespace GenHub.Tests.Core.ViewModels;

/// <summary>
/// Tests for <see cref="GameProfileItemViewModel"/>.
/// </summary>
public class GameProfileItemViewModelTests
{
    /// <summary>
    /// Verifies construction of <see cref="GameProfileItemViewModel"/>.
    /// </summary>
    [Fact]
    public void CanConstruct()
    {
        var mockProfile = new Mock<IGameProfile>();
        mockProfile.SetupGet(p => p.Version).Returns("1.0");
        mockProfile.SetupGet(p => p.ExecutablePath).Returns("C:/fake/path.exe");
        var vm = new GameProfileItemViewModel("test-profile-id", mockProfile.Object, "icon.png", "cover.jpg");
        Assert.NotNull(vm);
        Assert.Equal("test-profile-id", vm.ProfileId);
    }

    /// <summary>
    /// Verifies that the copy profile command calls the copy action when executed.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous unit test.</returns>
    [Fact]
    public async Task CopyProfileCommand_CallsCopyAction()
    {
        // Arrange
        var mockProfile = new Mock<IGameProfile>();
        mockProfile.SetupGet(p => p.Version).Returns("1.0");
        mockProfile.SetupGet(p => p.ExecutablePath).Returns("C:/fake/path.exe");

        var vm = new GameProfileItemViewModel("test-profile-id", mockProfile.Object, "icon.png", "cover.jpg");

        var copyActionCalled = false;
        vm.CopyProfileAction = _ =>
        {
            copyActionCalled = true;
            return Task.CompletedTask;
        };

        // Act
        await vm.CopyProfileCommand.ExecuteAsync(null);

        // Assert
        Assert.True(copyActionCalled);
    }

    /// <summary>
    /// Verifies that the copy profile command can be executed when copy action is set.
    /// </summary>
    [Fact]
    public void CopyProfileCommand_CanExecute_WhenActionIsSet()
    {
        // Arrange
        var mockProfile = new Mock<IGameProfile>();
        mockProfile.SetupGet(p => p.Version).Returns("1.0");
        mockProfile.SetupGet(p => p.ExecutablePath).Returns("C:/fake/path.exe");

        var vm = new GameProfileItemViewModel("test-profile-id", mockProfile.Object, "icon.png", "cover.jpg");
        vm.CopyProfileAction = _ => Task.CompletedTask;

        // Act & Assert
        Assert.True(vm.CopyProfileCommand.CanExecute(null));
    }

    /// <summary>
    /// Verifies that the copy profile command can be executed even when copy action is null.
    /// </summary>
    [Fact]
    public void CopyProfileCommand_CanExecute_WhenActionIsNull()
    {
        // Arrange
        var mockProfile = new Mock<IGameProfile>();
        mockProfile.SetupGet(p => p.Version).Returns("1.0");
        mockProfile.SetupGet(p => p.ExecutablePath).Returns("C:/fake/path.exe");

        var vm = new GameProfileItemViewModel("test-profile-id", mockProfile.Object, "icon.png", "cover.jpg");

        // Don't set CopyProfileAction

        // Act & Assert
        Assert.True(vm.CopyProfileCommand.CanExecute(null));
    }
}