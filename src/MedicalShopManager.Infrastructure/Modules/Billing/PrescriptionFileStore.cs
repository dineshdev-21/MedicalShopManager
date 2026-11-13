using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.Billing;

/// <summary>
/// Copies attached prescriptions into "&lt;database folder&gt;/Prescriptions/yyyy-MM/" under a new unique name, so a
/// sale's PrescriptionFilePath stays valid after the source file is moved or deleted. Images and PDFs only, ≤ 15 MB.
/// </summary>
public sealed class PrescriptionFileStore : IPrescriptionFileStore
{
    public const long MaxBytes = 15L * 1024 * 1024;

    public static IReadOnlyCollection<string> AllowedExtensions { get; } =
        new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".pdf" };

    private readonly string _root;
    private readonly ILogger<PrescriptionFileStore> _logger;

    public PrescriptionFileStore(DatabaseManager db, ILogger<PrescriptionFileStore> logger)
        : this(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(db.DatabasePath)) ?? AppContext.BaseDirectory, "Prescriptions"), logger)
    {
    }

    public PrescriptionFileStore(string rootFolder, ILogger<PrescriptionFileStore> logger)
    {
        _root = rootFolder;
        _logger = logger;
    }

    public async Task<string> StoreAsync(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new BillingValidationException("The prescription file was not found.");
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!AllowedExtensions.Contains(extension))
            throw new BillingValidationException("Attach a prescription image (JPG, PNG, BMP, GIF, TIFF) or a PDF.");
        var info = new FileInfo(sourcePath);
        if (info.Length == 0) throw new BillingValidationException("The prescription file is empty.");
        if (info.Length > MaxBytes) throw new BillingValidationException("The prescription file must be 15 MB or smaller.");

        var folder = Path.Combine(_root, DateTime.Now.ToString("yyyy-MM"));
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"Rx_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}" + extension);

        await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        await using (var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            await source.CopyToAsync(destination);
        }

        _logger.LogInformation("Prescription {Source} stored as {Target}", sourcePath, target);
        return target;
    }
}
