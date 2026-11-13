using System.Text.RegularExpressions;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// Format checks for Indian business identifiers (pure, no I/O). Inputs are trimmed and upper-cased by the
/// caller where that is the canonical form (<see cref="Normalize"/>).
/// </summary>
public static partial class IndianIdentifiers
{
    private const string Base36 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Trim and upper-case (GSTIN, PAN, IFSC are case-insensitive but printed in capitals).</summary>
    public static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    [GeneratedRegex("^[0-9]{2}[A-Z]{5}[0-9]{4}[A-Z][1-9A-Z]Z[0-9A-Z]$")]
    private static partial Regex GstinPattern();

    [GeneratedRegex("^[A-Z]{5}[0-9]{4}[A-Z]$")]
    private static partial Regex PanPattern();

    [GeneratedRegex("^[A-Z]{4}0[A-Z0-9]{6}$")]
    private static partial Regex IfscPattern();

    [GeneratedRegex("^[1-9][0-9]{5}$")]
    private static partial Regex PincodePattern();

    [GeneratedRegex("^[0-9]{14}$")]
    private static partial Regex FssaiPattern();

    [GeneratedRegex("^[0-9]{9,18}$")]
    private static partial Regex BankAccountPattern();

    [GeneratedRegex(@"^[A-Za-z0-9._\-]{2,256}@[A-Za-z][A-Za-z0-9]{1,63}$")]
    private static partial Regex UpiPattern();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$")]
    private static partial Regex EmailPattern();

    /// <summary>
    /// GSTIN check character (15th): mod-36 Luhn variant over the first 14 characters
    /// (factor 1 / 2 alternating, sum of quotient + remainder of each product by 36).
    /// Verified against published GSTINs, e.g. 27AAPFU0939F1ZV and 29AAGCB7383J1Z4.
    /// </summary>
    public static char GstinCheckCharacter(string first14)
    {
        if (first14 is null || first14.Length < 14) throw new ArgumentException("Need the first 14 GSTIN characters.", nameof(first14));
        var sum = 0;
        for (var i = 0; i < 14; i++)
        {
            var value = Base36.IndexOf(char.ToUpperInvariant(first14[i]));
            if (value < 0) throw new ArgumentException($"'{first14[i]}' is not a GSTIN character.", nameof(first14));
            var product = value * (i % 2 == 0 ? 1 : 2);
            sum += product / 36 + product % 36;
        }
        return Base36[(36 - sum % 36) % 36];
    }

    /// <summary>Null when <paramref name="gstin"/> is a well-formed GSTIN with a correct check character, else the reason.</summary>
    public static string? ValidateGstin(string? gstin)
    {
        var value = Normalize(gstin);
        if (value.Length != 15) return "GSTIN must be exactly 15 characters.";
        if (!GstinPattern().IsMatch(value)) return "GSTIN format is invalid (expected e.g. 27AAPFU0939F1ZV).";
        if (!IndianStates.IsValidCode(value[..2])) return $"GSTIN state code '{value[..2]}' is not a valid GST state code.";
        return GstinCheckCharacter(value) == value[14] ? null : "GSTIN check digit does not match. Please re-check the number.";
    }

    /// <summary>The PAN embedded in a GSTIN (characters 3–12), or empty when the GSTIN is too short.</summary>
    public static string PanFromGstin(string? gstin)
    {
        var value = Normalize(gstin);
        return value.Length >= 12 ? value[2..12] : string.Empty;
    }

    public static bool IsValidPan(string? pan) => PanPattern().IsMatch(Normalize(pan));
    public static bool IsValidIfsc(string? ifsc) => IfscPattern().IsMatch(Normalize(ifsc));
    public static bool IsValidPincode(string? pincode) => PincodePattern().IsMatch((pincode ?? string.Empty).Trim());
    public static bool IsValidFssai(string? fssai) => FssaiPattern().IsMatch((fssai ?? string.Empty).Trim());
    public static bool IsValidBankAccount(string? account) => BankAccountPattern().IsMatch((account ?? string.Empty).Trim());
    public static bool IsValidUpiId(string? vpa) => UpiPattern().IsMatch((vpa ?? string.Empty).Trim());
    public static bool IsValidEmail(string? email) => EmailPattern().IsMatch((email ?? string.Empty).Trim());

    /// <summary>
    /// Phone: a 10-digit mobile starting 6–9, or a landline with STD code (10–12 digits starting 0). Spaces, '-' and a
    /// leading +91 are ignored.
    /// </summary>
    public static bool IsValidPhone(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        var raw = (phone ?? string.Empty).Trim();
        if (raw.StartsWith("+91", StringComparison.Ordinal) && digits.Length == 12) digits = digits[2..];
        if (raw.Any(c => !(char.IsDigit(c) || c is ' ' or '-' or '+' or '(' or ')'))) return false;
        if (digits.Length == 10 && digits[0] is >= '6' and <= '9') return true;
        return digits.Length is >= 10 and <= 12 && digits[0] == '0';
    }
}
