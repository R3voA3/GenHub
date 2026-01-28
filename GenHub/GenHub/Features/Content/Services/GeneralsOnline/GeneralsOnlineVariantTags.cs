using GenHub.Core.Constants;

namespace GenHub.Features.Content.Services.GeneralsOnline;

/// <summary>
/// Constants for GeneralsOnline variant detection.
/// </summary>
internal static class GeneralsOnlineVariantTags
{
    /// <summary>Tag indicating 30Hz variant.</summary>
    public const string Tag30Hz = GeneralsOnlineConstants.Variant30HzSuffix;

    /// <summary>Tag indicating 60Hz variant.</summary>
    public const string Tag60Hz = GeneralsOnlineConstants.Variant60HzSuffix;

    /// <summary>Tag indicating QuickMatch MapPack variant.</summary>
    public const string TagQuickMatchMaps = GeneralsOnlineConstants.QuickMatchMapPackSuffix;
}
