namespace MedicalShopManager.Core.Modules.Purchases;

/// <summary>A GST state / union territory: two-digit code (first two digits of a GSTIN) and name.</summary>
public sealed record GstState(string Code, string Name)
{
    public string Label => $"{Code} - {Name}";
    public override string ToString() => Label;
}

/// <summary>GST state codes (CBIC list) for the distributor's STATE field and the IGST decision.</summary>
public static class GstStateCodes
{
    public static IReadOnlyList<GstState> All { get; } = new[]
    {
        new GstState("01", "Jammu and Kashmir"), new GstState("02", "Himachal Pradesh"), new GstState("03", "Punjab"),
        new GstState("04", "Chandigarh"), new GstState("05", "Uttarakhand"), new GstState("06", "Haryana"),
        new GstState("07", "Delhi"), new GstState("08", "Rajasthan"), new GstState("09", "Uttar Pradesh"),
        new GstState("10", "Bihar"), new GstState("11", "Sikkim"), new GstState("12", "Arunachal Pradesh"),
        new GstState("13", "Nagaland"), new GstState("14", "Manipur"), new GstState("15", "Mizoram"),
        new GstState("16", "Tripura"), new GstState("17", "Meghalaya"), new GstState("18", "Assam"),
        new GstState("19", "West Bengal"), new GstState("20", "Jharkhand"), new GstState("21", "Odisha"),
        new GstState("22", "Chhattisgarh"), new GstState("23", "Madhya Pradesh"), new GstState("24", "Gujarat"),
        new GstState("26", "Dadra and Nagar Haveli and Daman and Diu"), new GstState("27", "Maharashtra"),
        new GstState("29", "Karnataka"), new GstState("30", "Goa"), new GstState("31", "Lakshadweep"),
        new GstState("32", "Kerala"), new GstState("33", "Tamil Nadu"), new GstState("34", "Puducherry"),
        new GstState("35", "Andaman and Nicobar Islands"), new GstState("36", "Telangana"), new GstState("37", "Andhra Pradesh"),
        new GstState("38", "Ladakh"), new GstState("97", "Other Territory")
    };

    public static GstState? Find(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : All.FirstOrDefault(s => s.Code == code.Trim());

    public static string? NameOf(string? code) => Find(code)?.Name;
}
