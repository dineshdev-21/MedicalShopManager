namespace MedicalShopManager.Core.Modules.Finance;

/// <summary>
/// Amount in words in the Indian system (lakh / crore), as printed on receipts and payment vouchers:
/// 125050 paisa → "Rupees One Thousand Two Hundred Fifty and Fifty Paise Only".
/// </summary>
public static class FinanceAmountWords
{
    private static readonly string[] Ones =
    {
        "Zero", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen",
        "Fourteen", "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"
    };

    private static readonly string[] Tens = { "", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety" };

    public static string Rupees(long paisa)
    {
        var negative = paisa < 0;
        var abs = Math.Abs(paisa);
        var rupees = abs / 100;
        var paise = abs % 100;
        var text = "Rupees " + Number(rupees);
        if (paise > 0) text += " and " + Number(paise) + " Paise";
        return (negative ? "Minus " : "") + text + " Only";
    }

    public static string Number(long number)
    {
        if (number < 0) return "Minus " + Number(-number);
        if (number < 20) return Ones[number];
        var parts = new List<string>();
        var crore = number / 1_00_00_000;
        number %= 1_00_00_000;
        var lakh = number / 1_00_000;
        number %= 1_00_000;
        var thousand = number / 1000;
        number %= 1000;
        var hundred = number / 100;
        number %= 100;
        if (crore > 0) parts.Add(Number(crore) + " Crore");
        if (lakh > 0) parts.Add(Number(lakh) + " Lakh");
        if (thousand > 0) parts.Add(Number(thousand) + " Thousand");
        if (hundred > 0) parts.Add(Ones[hundred] + " Hundred");
        if (number > 0) parts.Add(number < 20 ? Ones[number] : Tens[number / 10] + (number % 10 > 0 ? " " + Ones[number % 10] : ""));
        return string.Join(" ", parts);
    }
}
