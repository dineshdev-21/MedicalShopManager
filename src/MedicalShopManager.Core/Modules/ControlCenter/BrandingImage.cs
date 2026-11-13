namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>Images printed on invoices, stored in table ControlCenterAssets (inside the database, so backed up).</summary>
public enum BrandingImageKind
{
    Logo = 1,
    Signature = 2
}

/// <summary>A stored branding image.</summary>
public sealed record BrandingImage(BrandingImageKind Kind, string ContentType, byte[] Data, DateTime UpdatedAt);

/// <summary>Validation of uploaded logo / signature files (pure).</summary>
public static class BrandingImageRules
{
    /// <summary>Invoices print the image small; 512 KB keeps the database and backups lean.</summary>
    public const int MaxBytes = 512 * 1024;

    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";

    public static string AssetKey(BrandingImageKind kind) => kind switch
    {
        BrandingImageKind.Logo => ControlCenterSettingKeys.LogoAsset,
        BrandingImageKind.Signature => ControlCenterSettingKeys.SignatureAsset,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>Content type from the file's magic bytes (PNG or JPEG only), or null.</summary>
    public static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
            && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A)
            return Png;
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF)
            return Jpeg;
        return null;
    }

    /// <summary>Null when the bytes are an acceptable image, else a user-facing message.</summary>
    public static string? Validate(byte[]? data)
    {
        if (data == null || data.Length == 0) return "The selected file is empty.";
        if (data.Length > MaxBytes) return $"The image is {data.Length / 1024} KB; please use one under {MaxBytes / 1024} KB.";
        return DetectContentType(data) == null ? "Only PNG or JPEG images can be used." : null;
    }
}
