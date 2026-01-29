using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GenHub.Core.Models.Providers;

namespace GenHub.Features.Tools.ViewModels.Dialogs;

/// <summary>
/// ViewModel for the Add Artifact dialog.
/// Provides validation and creation of new ReleaseArtifact entries.
/// </summary>
public partial class AddArtifactDialogViewModel : ObservableValidator
{
    private readonly Action<ReleaseArtifact> _onArtifactCreated;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Required(ErrorMessage = "Filename is required")]
    private string _filename = string.Empty;

    [ObservableProperty]
    [NotifyDataErrorInfo]
    [Url(ErrorMessage = "Please enter a valid URL")]
    private string _downloadUrl = string.Empty;

    [ObservableProperty]
    private long _fileSize;

    [ObservableProperty]
    private string _sha256Hash = string.Empty;

    [ObservableProperty]
    private bool _isPrimary = true;

    [ObservableProperty]
    private bool _useExistingUrl = true;

    [ObservableProperty]
    private string? _localFilePath;

    [ObservableProperty]
    private string? _validationError;

    [ObservableProperty]
    private bool _isValid;

    [ObservableProperty]
    private bool _isComputingHash;

    [ObservableProperty]
    private string _fileSizeDisplay = string.Empty;

    private static string FormatFileSize(long bytes)
    {
        string[] suffixes = ["B", "KB", "MB", "GB", "TB"];
        int suffixIndex = 0;
        double size = bytes;

        while (size >= 1024 && suffixIndex < suffixes.Length - 1)
        {
            size /= 1024;
            suffixIndex++;
        }

        return $"{size:0.##} {suffixes[suffixIndex]}";
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="AddArtifactDialogViewModel"/> class.
    /// </summary>
    /// <param name="onArtifactCreated">Callback invoked when artifact is successfully created.</param>
    public AddArtifactDialogViewModel(Action<ReleaseArtifact> onArtifactCreated)
    {
        _onArtifactCreated = onArtifactCreated ?? throw new ArgumentNullException(nameof(onArtifactCreated));

        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Filename) or nameof(DownloadUrl))
            {
                Validate();
            }
        };
    }

    /// <summary>
    /// Sets the local file and extracts filename and size.
    /// </summary>
    /// <param name="filePath">Path to the local file.</param>
    [RelayCommand]
    private async Task SelectLocalFileAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return;
        }

        LocalFilePath = filePath;
        Filename = Path.GetFileName(filePath);

        var fileInfo = new FileInfo(filePath);
        FileSize = fileInfo.Length;
        FileSizeDisplay = FormatFileSize(FileSize);

        // Auto-compute hash
        await ComputeHashAsync();
    }

    /// <summary>
    /// Computes the SHA256 hash from the local file.
    /// </summary>
    [RelayCommand]
    private async Task ComputeHashAsync()
    {
        if (string.IsNullOrWhiteSpace(LocalFilePath) || !File.Exists(LocalFilePath))
        {
            ValidationError = "Please select a local file first";
            return;
        }

        IsComputingHash = true;
        ValidationError = null;

        try
        {
            await using var stream = File.OpenRead(LocalFilePath);
            using var sha256 = SHA256.Create();
            var hashBytes = await sha256.ComputeHashAsync(stream, CancellationToken.None);
            Sha256Hash = Convert.ToHexString(hashBytes).ToLowerInvariant();
        }
        catch (Exception ex)
        {
            ValidationError = $"Failed to compute hash: {ex.Message}";
        }
        finally
        {
            IsComputingHash = false;
        }
    }

    /// <summary>
    /// Creates the artifact if validation passes.
    /// </summary>
    [RelayCommand]
    private void CreateArtifact()
    {
        ValidateAllProperties();

        if (HasErrors)
        {
            ValidationError = string.Join(Environment.NewLine, GetErrors().Select(e => e.ErrorMessage));
            IsValid = false;
            return;
        }

        // Validate URL if using existing URL
        if (UseExistingUrl && !Uri.TryCreate(DownloadUrl, UriKind.Absolute, out var uri))
        {
            ValidationError = "Please enter a valid download URL";
            IsValid = false;
            return;
        }

        var artifact = new ReleaseArtifact
        {
            Filename = Filename.Trim(),
            DownloadUrl = DownloadUrl.Trim(),
            Size = FileSize,
            Sha256 = string.IsNullOrWhiteSpace(Sha256Hash) ? string.Empty : Sha256Hash.Trim(),
            IsPrimary = IsPrimary,
            LocalFilePath = LocalFilePath,
        };

        _onArtifactCreated(artifact);
    }

    /// <summary>
    /// Closes the dialog without saving.
    /// </summary>
    [RelayCommand]
    private void Close()
    {
        _onArtifactCreated(null!);
    }

    private void Validate()
    {
        ValidateAllProperties();
        IsValid = !HasErrors;
        ValidationError = HasErrors
            ? string.Join(Environment.NewLine, GetErrors().Select(e => e.ErrorMessage))
            : null;
    }
}
