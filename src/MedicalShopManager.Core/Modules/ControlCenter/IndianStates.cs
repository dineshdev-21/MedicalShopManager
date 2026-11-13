namespace MedicalShopManager.Core.Modules.ControlCenter;

/// <summary>A state or union territory with its two-digit GST state code (first two characters of a GSTIN).</summary>
public sealed record IndianState(string Code, string Name)
{
    public string Display => $"{Code} - {Name}";
}

/// <summary>
/// GST state codes (CBIC list). The business state code decides intra-state (CGST + SGST) vs inter-state (IGST)
/// supply through <c>PricingCalculator.IsInterStateSupply</c>. Code 25 (Daman &amp; Diu) was merged into 26 in 2020
/// and 28 (undivided Andhra Pradesh) is no longer issued, so neither is offered for new profiles.
/// </summary>
public static class IndianStates
{
    public static IReadOnlyList<IndianState> All { get; } = new[]
    {
        new IndianState("01", "Jammu and Kashmir"),
        new IndianState("02", "Himachal Pradesh"),
        new IndianState("03", "Punjab"),
        new IndianState("04", "Chandigarh"),
        new IndianState("05", "Uttarakhand"),
        new IndianState("06", "Haryana"),
        new IndianState("07", "Delhi"),
        new IndianState("08", "Rajasthan"),
        new IndianState("09", "Uttar Pradesh"),
        new IndianState("10", "Bihar"),
        new IndianState("11", "Sikkim"),
        new IndianState("12", "Arunachal Pradesh"),
        new IndianState("13", "Nagaland"),
        new IndianState("14", "Manipur"),
        new IndianState("15", "Mizoram"),
        new IndianState("16", "Tripura"),
        new IndianState("17", "Meghalaya"),
        new IndianState("18", "Assam"),
        new IndianState("19", "West Bengal"),
        new IndianState("20", "Jharkhand"),
        new IndianState("21", "Odisha"),
        new IndianState("22", "Chhattisgarh"),
        new IndianState("23", "Madhya Pradesh"),
        new IndianState("24", "Gujarat"),
        new IndianState("26", "Dadra and Nagar Haveli and Daman and Diu"),
        new IndianState("27", "Maharashtra"),
        new IndianState("29", "Karnataka"),
        new IndianState("30", "Goa"),
        new IndianState("31", "Lakshadweep"),
        new IndianState("32", "Kerala"),
        new IndianState("33", "Tamil Nadu"),
        new IndianState("34", "Puducherry"),
        new IndianState("35", "Andaman and Nicobar Islands"),
        new IndianState("36", "Telangana"),
        new IndianState("37", "Andhra Pradesh"),
        new IndianState("38", "Ladakh"),
        new IndianState("97", "Other Territory")
    };

    public static IndianState? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var normalized = code.Trim().PadLeft(2, '0');
        return All.FirstOrDefault(s => s.Code == normalized);
    }

    public static bool IsValidCode(string? code) => Find(code) != null;
}
