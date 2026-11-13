using System.Globalization;

namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>
/// Builds the NPCI UPI deep link encoded in the invoice payment QR (<c>upi://pay?pa=&amp;pn=&amp;am=&amp;cu=INR&amp;tn=</c>).
/// Localwell prints <c>invoice_settings.business_bank_detail_upi</c> on the bill; any UPI app scanning the QR opens a
/// pre-filled payment. The amount is the bill's net payable in paisa, rendered as rupees with 2 decimals.
/// </summary>
public static class UpiPaymentLink
{
    /// <summary>Returns null when the shop has no valid UPI ID.</summary>
    public static string? Build(string? upiId, string payeeName, long amountPaisa = 0, string? note = null)
    {
        if (!IndianIdentifiers.IsValidUpiId(upiId)) return null;
        if (amountPaisa < 0) throw new ArgumentOutOfRangeException(nameof(amountPaisa), "Amount cannot be negative.");

        var parts = new List<string>
        {
            "pa=" + Uri.EscapeDataString(upiId!.Trim()),
            "pn=" + Uri.EscapeDataString(string.IsNullOrWhiteSpace(payeeName) ? "Pharmacy" : payeeName.Trim())
        };
        if (amountPaisa > 0)
            parts.Add("am=" + (amountPaisa / 100m).ToString("0.00", CultureInfo.InvariantCulture));
        parts.Add("cu=INR");
        if (!string.IsNullOrWhiteSpace(note))
            parts.Add("tn=" + Uri.EscapeDataString(note.Trim()));
        return "upi://pay?" + string.Join("&", parts);
    }
}
