namespace MedicalShopManager.Core.Modules.Billing;

/// <summary>One row of the rate-wise GST summary printed under a tax invoice.</summary>
public sealed record GstRateSummary(decimal GstPercent, long TaxablePaisa, long CgstPaisa, long SgstPaisa, long IgstPaisa)
{
    public long TaxPaisa => CgstPaisa + SgstPaisa + IgstPaisa;
}

/// <summary>Pure text and summary helpers for the sale invoice print (amount in words, GST summary).</summary>
public static class BillingInvoiceText
{
    private static readonly string[] Ones =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve",
        "Thirteen", "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"
    };

    private static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

    /// <summary>
    /// Rupees in words in the Indian numbering system (crore, lakh, thousand), e.g. 125050 paisa →
    /// "Rupees One Thousand Two Hundred Fifty and Fifty Paise Only".
    /// </summary>
    public static string AmountInWords(long paisa)
    {
        var negative = paisa < 0;
        var abs = Math.Abs(paisa);
        var rupees = abs / 100;
        var paise = abs % 100;
        var text = "Rupees " + NumberInWords(rupees);
        if (paise > 0) text += " and " + NumberInWords(paise) + " Paise";
        text += " Only";
        return negative ? "Minus " + text : text;
    }

    /// <summary>Whole number in Indian-system words ("One Crore Two Lakh Three Thousand Four Hundred Five").</summary>
    public static string NumberInWords(long number)
    {
        if (number < 0) throw new ArgumentOutOfRangeException(nameof(number), "Number must not be negative.");
        if (number == 0) return Ones[0];
        var parts = new List<string>();
        var crore = number / 10_000_000;
        number %= 10_000_000;
        if (crore > 0) parts.Add(NumberInWords(crore) + " Crore");
        var lakh = number / 100_000;
        number %= 100_000;
        if (lakh > 0) parts.Add(BelowHundred(lakh) + " Lakh");
        var thousand = number / 1000;
        number %= 1000;
        if (thousand > 0) parts.Add(BelowHundred(thousand) + " Thousand");
        var hundred = number / 100;
        number %= 100;
        if (hundred > 0) parts.Add(Ones[hundred] + " Hundred");
        if (number > 0) parts.Add(BelowHundred(number));
        return string.Join(" ", parts);
    }

    private static string BelowHundred(long n) =>
        n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : string.Empty);

    /// <summary>Taxable value and tax per GST rate, ascending by rate (the GSTR-1 style summary on tax invoices).</summary>
    public static IReadOnlyList<GstRateSummary> GstByRate(IEnumerable<BillingInvoiceLine> lines) =>
        lines.GroupBy(l => l.GstPercent)
            .OrderBy(g => g.Key)
            .Select(g => new GstRateSummary(g.Key, g.Sum(l => l.TaxablePaisa), g.Sum(l => l.CgstPaisa),
                g.Sum(l => l.SgstPaisa), g.Sum(l => l.IgstPaisa)))
            .ToList();
}
