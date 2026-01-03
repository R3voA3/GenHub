using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenHub.Core.Interfaces.Common;
using GenHub.Core.Interfaces.GameInstallations;
using GenHub.Core.Interfaces.GameProfiles;
using GenHub.Core.Interfaces.GeneralsOnline;
using GenHub.Core.Interfaces.Notifications;
using GenHub.Features.GameProfiles.Services;
using GenHub.Core.Models.Enums;
using GenHub.Core.Models.Notifications;
using GenHub.Features.AppUpdate.Interfaces;
using GenHub.Features.AppUpdate.Views;
using GenHub.Features.Downloads.ViewModels;
using GenHub.Features.GameProfiles.ViewModels;
using GenHub.Features.GeneralsOnline.ViewModels;
using GenHub.Features.Notifications.ViewModels;
using GenHub.Features.Settings.ViewModels;
using GenHub.Features.Tools.ViewModels;
using Microsoft.Extensions.Logging;

namespace GenHub.Common.ViewModels;

/// <summary>
/// Main ViewModel for the application shell.
/// Orchestrates navigation and shared application state.
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _initializationCts = new();
    private readonly IGameInstallationDetectionOrchestrator _gameInstallationDetectionOrchestrator;
    private readonly IConfigurationProviderService _configurationProvider;
    private readonly IUserSettingsService _userSettingsService;
    private readonly IProfileEditorFacade _profileEditorFacade;
    private readonly IVelopackUpdateManager _velopackUpdateManager;
    private readonly ProfileResourceService _profileResourceService;
    private readonly IGeneralsOnlineAuthService _generalsOnlineAuthService;
    private readonly ILogger<MainViewModel>? _logger;
    private readonly INotificationService _notificationService;
    private readonly NotificationFeedViewModel _notificationFeedViewModel;

    [ObservableProperty]
    private NavigationTab _selectedTab;

    [ObservableProperty]
    private bool _hasUpdateAvailable;

    /// <summary>
    /// Initializes a new instance of the <see cref="MainViewModel"/> class.
    /// </summary>
    /// <param name="gameProfilesViewModel">Game profiles view model.</param>
    /// <param name="downloadsViewModel">Downloads view model.</param>
    /// <param name="toolsViewModel">Tools view model.</param>
    /// <param name="settingsViewModel">Settings view model.</param>
    /// <param name="notificationManager">Notification manager view model.</param>
    /// <param name="generalsOnlineViewModel">Generals Online view model.</param>
    /// <param name="gameInstallationDetectionOrchestrator">Game installation orchestrator.</param>
    /// <param name="configurationProvider">Configuration provider service.</param>
    /// <param name="userSettingsService">User settings service for persistence operations.</param>
    /// <param name="profileEditorFacade">Profile editor facade for automatic profile creation.</param>
    /// <param name="velopackUpdateManager">The Velopack update manager for checking updates.</param>
    /// <param name="profileResourceService">Service for accessing profile resources.</param>
    /// <param name="notificationService">Service for showing notifications.</param>
    /// <param name="notificationFeedViewModel">Notification feed view model.</param>
    /// <param name="generalsOnlineAuthService">Generals Online authentication service.</param>
    /// <param name="logger">Logger instance.</param>
    public MainViewModel(
        GameProfileLauncherViewModel gameProfilesViewModel,
        DownloadsViewModel downloadsViewModel,
        ToolsViewModel toolsViewModel,
        SettingsViewModel settingsViewModel,
        NotificationManagerViewModel notificationManager,
        GeneralsOnlineViewModel generalsOnlineViewModel,
        IGameInstallationDetectionOrchestrator gameInstallationDetectionOrchestrator,
        IConfigurationProviderService configurationProvider,
        IUserSettingsService userSettingsService,
        IProfileEditorFacade profileEditorFacade,
        IVelopackUpdateManager velopackUpdateManager,
        ProfileResourceService profileResourceService,
        INotificationService notificationService,
        NotificationFeedViewModel notificationFeedViewModel,
        IGeneralsOnlineAuthService generalsOnlineAuthService,
        ILogger<MainViewModel>? logger = null)
    {
        GameProfilesViewModel = gameProfilesViewModel;
        DownloadsViewModel = downloadsViewModel;
        ToolsViewModel = toolsViewModel;
        SettingsViewModel = settingsViewModel;
        NotificationManager = notificationManager;
        GeneralsOnlineViewModel = generalsOnlineViewModel;
        _gameInstallationDetectionOrchestrator = gameInstallationDetectionOrchestrator;
        _configurationProvider = configurationProvider;
        _userSettingsService = userSettingsService;
        _profileEditorFacade = profileEditorFacade ?? throw new ArgumentNullException(nameof(profileEditorFacade));
        _velopackUpdateManager = velopackUpdateManager ?? throw new ArgumentNullException(nameof(velopackUpdateManager));
        _profileResourceService = profileResourceService ?? throw new ArgumentNullException(nameof(profileResourceService));
        _notificationService = notificationService ?? throw new ArgumentNullException(nameof(notificationService));
        _notificationFeedViewModel = notificationFeedViewModel ?? throw new ArgumentNullException(nameof(notificationFeedViewModel));
        _generalsOnlineAuthService = generalsOnlineAuthService ?? throw new ArgumentNullException(nameof(generalsOnlineAuthService));
        _logger = logger;

        // Load initial settings using unified configuration
        _selectedTab = LoadInitialTab();
    }

    /// <summary>
    /// Gets the game profiles view model.
    /// </summary>
    public GameProfileLauncherViewModel GameProfilesViewModel { get; }

    /// <summary>
    /// Gets the downloads view model.
    /// </summary>
    public DownloadsViewModel DownloadsViewModel { get; }

    /// <summary>
    /// Gets the tools view model.
    /// </summary>
    public ToolsViewModel ToolsViewModel { get; }

    /// <summary>
    /// Gets the settings view model.
    /// </summary>
    public SettingsViewModel SettingsViewModel { get; }

    /// <summary>
    /// Gets the notification manager view model.
    /// </summary>
    public NotificationManagerViewModel NotificationManager { get; }

    /// <summary>
    /// Gets the Generals Online view model.
    /// </summary>
    public GeneralsOnlineViewModel GeneralsOnlineViewModel { get; }

    /// <summary>
    /// Gets the notification feed view model.
    /// </summary>
    public NotificationFeedViewModel NotificationFeed => _notificationFeedViewModel;

    /// <summary>
    /// Gets the collection of detected game installations.
    /// </summary>
    public ObservableCollection<string> GameInstallations { get; } = [];

    /// <summary>
    /// Gets the available navigation tabs.
    /// </summary>
    public NavigationTab[] AvailableTabs { get; } =
    [
        NavigationTab.GameProfiles,
        NavigationTab.Downloads,
        NavigationTab.Tools,
        NavigationTab.GeneralsOnline,
        NavigationTab.Settings,
    ];

    // Fixed: Removed 'static' keyword to allow access to instance fields _configurationProvider and _logger
    private NavigationTab LoadInitialTab()
    {
        try
        {
            var tab = _configurationProvider.GetLastSelectedTab();
            if (tab == NavigationTab.Tools)
            {
                tab = NavigationTab.GameProfiles;
            }

            _logger?.LogDebug("Initial settings loaded, selected tab: {Tab}", tab);
            return tab;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to load initial settings");
            return NavigationTab.GameProfiles;
        }
    }

    /// <summary>
    /// Gets the current tab's ViewModel for ContentControl binding.
    /// </summary>
    public object CurrentTabViewModel => SelectedTab switch
    {
        NavigationTab.GameProfiles => GameProfilesViewModel,
        NavigationTab.Downloads => DownloadsViewModel,
        NavigationTab.Tools => ToolsViewModel,
        NavigationTab.Settings => SettingsViewModel,
        NavigationTab.GeneralsOnline => GeneralsOnlineViewModel,
        _ => GameProfilesViewModel,
    };

    /// <summary>
    /// Gets the display name for a navigation tab.
    /// </summary>
    /// <param name="tab">The navigation tab.</param>
    /// <returns>The display name.</returns>
    public static string GetTabDisplayName(NavigationTab tab) => tab switch
    {
        NavigationTab.GameProfiles => "Game Profiles",
        NavigationTab.Downloads => "Downloads",
        NavigationTab.Tools => "Tools",
        NavigationTab.Settings => "Settings",
        NavigationTab.GeneralsOnline => "Generals Online",
        _ => tab.ToString(),
    };

    /// <summary>
    /// Selects the specified navigation tab.
    /// </summary>
    /// <param name="tab">The navigation tab to select.</param>
    [RelayCommand]
    public void SelectTab(NavigationTab tab)
    {
        SelectedTab = tab;
    }

    /// <summary>
    /// Performs asynchronous initialization for the shell and all tabs.
    /// </summary>
    /// <returns>A <see cref="Task"/> representing the asynchronous operation.</returns>
    public async Task InitializeAsync()
    {
        await GameProfilesViewModel.InitializeAsync();
        await DownloadsViewModel.InitializeAsync();
        await ToolsViewModel.InitializeAsync();
        await GeneralsOnlineViewModel.InitializeAsync();
        await _generalsOnlineAuthService.InitializeAsync();

        // Subscribe to authentication changes
        /*
        _generalsOnlineAuthService.IsAuthenticated.Subscribe(isAuthenticated =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (isAuthenticated)
                {
                    if (!AvailableTabs.Contains(NavigationTab.GeneralsOnline))
                    {
                        // Insert before Settings (last item)
                        var settingsIndex = AvailableTabs.IndexOf(NavigationTab.Settings);
                        if (settingsIndex >= 0)
                        {
                            AvailableTabs.Insert(settingsIndex, NavigationTab.GeneralsOnline);
                        }
                        else
                        {
                            AvailableTabs.Add(NavigationTab.GeneralsOnline);
                        }
                    }
                }
                else
                {
                    if (AvailableTabs.Contains(NavigationTab.GeneralsOnline))
                    {
                        AvailableTabs.Remove(NavigationTab.GeneralsOnline);

                        // If user was on this tab, switch to default
                        if (SelectedTab == NavigationTab.GeneralsOnline)
                        {
                            SelectedTab = NavigationTab.GameProfiles;
                        }
                    }
                }
            });
        });
        */
        _logger?.LogInformation("MainViewModel initialized");

        // Start background check with cancellation support
        _ = CheckForUpdatesInBackgroundAsync(_initializationCts.Token);
    }

    /// <summary>
    /// Disposes of managed resources.
    /// </summary>
    public void Dispose()
    {
        _initializationCts?.Cancel();
        _initializationCts?.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Checks for available updates using Velopack.
    /// </summary>
    private async Task CheckForUpdatesAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogDebug("Starting background update check");

        try
        {
            var settings = _userSettingsService.Get();

            // Push settings to update manager (important context for other components)
            if (settings.SubscribedPrNumber.HasValue)
            {
                _velopackUpdateManager.SubscribedPrNumber = settings.SubscribedPrNumber;
            }

            // 1. Check for standard GitHub releases (Default)
            if (string.IsNullOrEmpty(settings.SubscribedBranch))
            {
                var updateInfo = await _velopackUpdateManager.CheckForUpdatesAsync(cancellationToken);
                if (updateInfo != null)
                {
                    _logger?.LogInformation("GitHub release update available: {Version}", updateInfo.TargetFullRelease.Version);
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // notificationService reference needs to be added as a field/property if needed
                        // NotificationService.Show(new NotificationMessage(
                        //     NotificationType.Info,
                        //     "Update Available",
                        //     $"A new version ({updateInfo.TargetFullRelease.Version}) is available.",
                        //     null, // Persistent
                        //     "View Updates",
                        //     () => { SettingsViewModel.OpenUpdateWindowCommand.Execute(null); }));
                        _notificationService.Show(new NotificationMessage(
                            NotificationType.Info,
                            "Update Available",
                            $"A new version ({updateInfo.TargetFullRelease.Version}) is available.",
                            null, // Persistent
                            actions: new List<NotificationAction>
                            {
                                new NotificationAction(
                                    "View Updates",
                                    () => { SettingsViewModel.OpenUpdateWindowCommand.Execute(null); },
                                    NotificationActionStyle.Primary,
                                    dismissOnExecute: true),
                            }));
                    });
                    return;
                }
            }
            else
            {
                // 2. Check for Subscribed Branch Artifacts
                _logger?.LogDebug("User subscribed to branch '{Branch}', checking for artifact updates", settings.SubscribedBranch);
                _velopackUpdateManager.SubscribedBranch = settings.SubscribedBranch;
                _velopackUpdateManager.SubscribedPrNumber = null; // Clear PR to avoid ambiguity

                var artifactUpdate = await _velopackUpdateManager.CheckForArtifactUpdatesAsync(cancellationToken);

                if (artifactUpdate != null)
                {
                    var newVersionBase = artifactUpdate.Version.Split('+')[0];

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        // NotificationService reference needs to be added as a field/property if needed
                        // NotificationService.Show(new NotificationMessage(
                        //     "Branch Update Available",
                        //     $"A new build ({newVersionBase}) is available on branch '{settings.SubscribedBranch}'.",
                        //     null, // Persistent
                        //     "View Updates",
                        //     () => { SettingsViewModel.OpenUpdateWindowCommand.Execute(null); }));
                        _notificationService.Show(new NotificationMessage(
                            NotificationType.Info,
                            "Branch Update Available",
                            $"A new build ({newVersionBase}) is available on branch '{settings.SubscribedBranch}'.",
                            null, // Persistent
                            actions: new List<NotificationAction>
                            {
                                new NotificationAction(
                                    "View Updates",
                                    () => { SettingsViewModel.OpenUpdateWindowCommand.Execute(null); },
                                    NotificationActionStyle.Primary,
                                    dismissOnExecute: true),
                            }));
                    });
                }
        }}
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Exception in CheckForUpdatesAsync");
        }
    }

    private async Task CheckForUpdatesInBackgroundAsync(CancellationToken ct)
    {
        try
        {
            await CheckForUpdatesAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Unhandled exception in background update check");
        }
    }

    private void SaveSelectedTab(NavigationTab selectedTab)
    {
        try
        {
            _userSettingsService.Update(settings =>
            {
                settings.LastSelectedTab = selectedTab;
            });

            _ = _userSettingsService.SaveAsync();
            _logger?.LogDebug("Updated last selected tab to: {Tab}", selectedTab);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to update selected tab setting");
        }
    }

    partial void OnSelectedTabChanged(NavigationTab value)
    {
        OnPropertyChanged(nameof(CurrentTabViewModel));

        // Notify SettingsViewModel when it becomes visible/invisible
        SettingsViewModel.IsViewVisible = value == NavigationTab.Settings;

        // Refresh Tabs when they become visible
        if (value == NavigationTab.GameProfiles)
        {
            GameProfilesViewModel.OnTabActivated();
        }
        else if (value == NavigationTab.Downloads)
        {
            _ = DownloadsViewModel.OnTabActivatedAsync();
        }

        SaveSelectedTab(value);
    }

    // Fixed: Added missing GetMainWindow method
    private Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }
        return null;
    }

    /// <summary>
    /// Shows the update notification dialog.
    /// </summary>
    [RelayCommand]
    private async Task ShowUpdateDialogAsync()
    {
        try
        {
            _logger?.LogInformation("ShowUpdateDialogCommand executed");

            var mainWindow = GetMainWindow();
            if (mainWindow is not null)
            {
                _logger?.LogInformation("Opening update notification window");

                var updateWindow = new UpdateNotificationWindow
                {
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                };

                await updateWindow.ShowDialog(mainWindow);

                _logger?.LogInformation("Update notification window closed");
            }
            else
            {
                _logger?.LogWarning("Could not find main window to show update dialog");
            }
        }
        catch (System.Exception ex)
        {
            _logger?.LogError(ex, "Failed to show update notification window");
        }
    }
}
