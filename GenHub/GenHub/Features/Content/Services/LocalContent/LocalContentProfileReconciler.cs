using System;
using System.Threading;
using System.Threading.Tasks;
using GenHub.Core.Interfaces.Content;
using GenHub.Core.Interfaces.Notifications;
using GenHub.Core.Models.Results;
using Microsoft.Extensions.Logging;

namespace GenHub.Features.Content.Services.LocalContent;

/// <summary>
/// Service for reconciling game profiles when local content is modified.
/// </summary>
public class LocalContentProfileReconciler(
    IContentReconciliationService reconciliationService,
    INotificationService notificationService,
    ILogger<LocalContentProfileReconciler> logger)
    : ILocalContentProfileReconciler
{
    /// <inheritdoc />
    public async Task<OperationResult<int>> ReconcileProfilesAsync(
        string oldManifestId,
        string newManifestId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await reconciliationService.ReconcileManifestReplacementAsync(
                oldManifestId,
                newManifestId,
                cancellationToken);

            if (result.Success && result.Data > 0)
            {
                notificationService.ShowInfo(
                    "Profiles Updated",
                    $"Updated {result.Data} profile(s) to use the renamed content.",
                    4000);
            }

            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error reconciling profiles for local content update via unified service");
            return OperationResult<int>.CreateFailure($"Reconciliation failed: {ex.Message}");
        }
    }
}
