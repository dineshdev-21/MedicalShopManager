using System.Globalization;
using System.Text;

namespace MedicalShopManager.Core.Modules.DoctorsFollowups;

/// <summary>One medicine in a refill reminder message.</summary>
public sealed record RefillMessageItem(string MedicineName, string? CompositionName, DateTime DueDate);

/// <summary>Text, WhatsApp link and call link for a patient's refill reminder (nothing is sent by the app).</summary>
public sealed record FollowupMessage(
    string PatientName,
    string? Mobile,
    string Message,
    string? WhatsAppLink,
    string? CallLink,
    IReadOnlyList<long> FollowupIds)
{
    public bool HasMobile => Mobile is not null;
}

/// <summary>
/// Refill reminder text and deep links. The message follows Localwell's refill template (01-shell-dashboard §7) with
/// the software credit line dropped; links use <c>https://api.whatsapp.com/send/?phone=91&lt;mobile&gt;&amp;text=…</c>
/// and <c>tel:+91&lt;mobile&gt;</c>. The app only builds and copies them; sending is the user's action.
/// </summary>
public static class FollowupMessageBuilder
{
    private static readonly CultureInfo Dates = CultureInfo.InvariantCulture;

    /// <summary>
    /// A 10-digit Indian number from user text ("+91 98765-43210", "098765 43210", "080 2345 6789"), or null.
    /// Accepts mobiles and landlines with STD code (10 digits once the 0 / 91 prefix is removed).
    /// </summary>
    public static string? NormalizeIndianPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;
        var digits = new string(phone.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 13 && digits.StartsWith("091", StringComparison.Ordinal)) digits = digits[3..];
        else if (digits.Length == 12 && digits.StartsWith("91", StringComparison.Ordinal)) digits = digits[2..];
        else if (digits.Length == 11 && digits[0] == '0') digits = digits[1..];
        return digits.Length == 10 && digits[0] != '0' ? digits : null;
    }

    /// <summary>A 10-digit Indian mobile (starts 6–9), or null for landlines and invalid text.</summary>
    public static string? NormalizeIndianMobile(string? phone) =>
        NormalizeIndianPhone(phone) is { } digits && digits[0] is >= '6' and <= '9' ? digits : null;

    /// <summary>Localwell's refill reminder (one block per medicine).</summary>
    public static string RefillMessage(string patientName, string shopName, string? shopPhone, IReadOnlyList<RefillMessageItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) throw new ArgumentException("At least one medicine is needed for a reminder.", nameof(items));

        var text = new StringBuilder();
        text.Append("Greetings *").Append(Clean(patientName, "Customer")).Append("*,\n\n");
        text.Append("This is a friendly *Medicine Refill Reminder* from your Trusted Pharmacy *")
            .Append(Clean(shopName, "our pharmacy")).Append("*\n");
        foreach (var item in items)
        {
            text.Append('\n');
            text.Append("Brand Name: *").Append(Clean(item.MedicineName, "-")).Append("*\n");
            if (!string.IsNullOrWhiteSpace(item.CompositionName)) text.Append("Generic Name: ").Append(item.CompositionName.Trim()).Append('\n');
            text.Append("Due on: *").Append(item.DueDate.ToString("MMM dd, yyyy", Dates)).Append("*\n");
        }
        text.Append('\n');
        text.Append(string.IsNullOrWhiteSpace(shopPhone)
            ? "To *Reorder*, You can *Reply Back* to this Message"
            : $"To *Reorder*, You can either *Reply Back* to this Message or Call us on *{shopPhone.Trim()}*");
        return text.ToString();
    }

    /// <summary>WhatsApp deep link with the message pre-filled; null when the number is not an Indian mobile.</summary>
    public static string? WhatsAppLink(string? phone, string message)
    {
        var mobile = NormalizeIndianMobile(phone);
        return mobile is null ? null : $"https://api.whatsapp.com/send/?phone=91{mobile}&text={Uri.EscapeDataString(message ?? string.Empty)}";
    }

    /// <summary>tel: link for a call; null when the number is invalid.</summary>
    public static string? CallLink(string? phone) => NormalizeIndianPhone(phone) is { } digits ? $"tel:+91{digits}" : null;

    /// <summary>
    /// The reminder for a set of follow-ups of one patient, or the reason it cannot be built
    /// (nothing selected, several patients, no mobile number).
    /// </summary>
    public static (FollowupMessage? Message, string? Error) Build(IReadOnlyList<PatientFollowup> selected, string shopName, string? shopPhone)
    {
        ArgumentNullException.ThrowIfNull(selected);
        if (selected.Count == 0) return (null, "Please select a follow-up to continue.");
        var patients = selected.Select(f => (f.CustomerId, f.PatientKey)).Distinct().Count();
        if (patients > 1) return (null, "Select follow-ups of one patient at a time to build a reminder.");

        var first = selected[0];
        var items = selected
            .OrderBy(f => f.DueDate).ThenBy(f => f.MedicineName, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new RefillMessageItem(f.MedicineName, f.CompositionName, f.DueDate))
            .ToList();
        var message = RefillMessage(first.PatientDisplay, shopName, shopPhone, items);
        var mobile = NormalizeIndianMobile(first.CustomerPhone);
        return (new FollowupMessage(first.PatientDisplay, mobile, message, WhatsAppLink(first.CustomerPhone, message),
            CallLink(first.CustomerPhone), selected.Select(f => f.Id).ToList()), null);
    }

    private static string Clean(string? text, string fallback) => string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();
}
