namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>
/// Shop-level GST settings for the returns: the shop's state (place of supply of walk-in sales), the B2C-large
/// threshold (invoice value strictly above it, inter-state, unregistered buyer; ₹1,00,000 since 1 Aug 2024,
/// Notification 12/2024-CT) and the UQC printed in the HSN summary.
/// </summary>
public sealed record GstReturnContext(string ShopStateCode, long B2clThresholdPaisa = GstReturnContext.DefaultB2clThresholdPaisa, string Uqc = "NOS")
{
    public const long DefaultB2clThresholdPaisa = 100_000_00;
}

/// <summary>One (sale invoice, GST rate) group of an active sale.</summary>
public sealed record GstInvoiceLine
{
    public long SaleId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime InvoiceDate { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerGstin { get; init; }
    public bool IsInterState { get; init; }
    /// <summary>False for a bill of supply (no GST charged): reported as exempt.</summary>
    public bool WithGst { get; init; } = true;
    /// <summary>Whole invoice value (all rates), as on the bill.</summary>
    public long InvoiceValuePaisa { get; init; }
    public decimal Rate { get; init; }
    public long TaxablePaisa { get; init; }
    public long IgstPaisa { get; init; }
    public long CgstPaisa { get; init; }
    public long SgstPaisa { get; init; }
    public long TaxPaisa => IgstPaisa + CgstPaisa + SgstPaisa;
}

/// <summary>One (credit note, GST rate) group of an active sale return.</summary>
public sealed record GstNoteLine
{
    public long ReturnId { get; init; }
    public string NoteNumber { get; init; } = string.Empty;
    public DateTime NoteDate { get; init; }
    public string? CustomerName { get; init; }
    public string? CustomerGstin { get; init; }
    public bool IsInterState { get; init; }
    public long NoteValuePaisa { get; init; }
    /// <summary>Value of the original bill when the return was made against one (decides CDNUR B2CL).</summary>
    public long? OriginalInvoiceValuePaisa { get; init; }
    public decimal Rate { get; init; }
    public long TaxablePaisa { get; init; }
    public long IgstPaisa { get; init; }
    public long CgstPaisa { get; init; }
    public long SgstPaisa { get; init; }
    public long TaxPaisa => IgstPaisa + CgstPaisa + SgstPaisa;
}

/// <summary>One (HSN, rate, B2B?) group of goods sold (positive) or returned (negative values and units).</summary>
public sealed record GstHsnLine
{
    public string? Hsn { get; init; }
    public string? Description { get; init; }
    public decimal Rate { get; init; }
    public bool IsB2b { get; init; }
    public long Units { get; init; }
    public long TaxablePaisa { get; init; }
    public long IgstPaisa { get; init; }
    public long CgstPaisa { get; init; }
    public long SgstPaisa { get; init; }
}

/// <summary>One (purchase bill or debit note, GST rate) group of inward supplies.</summary>
public sealed record GstInwardLine
{
    public bool IsReturn { get; init; }
    public bool IsInterState { get; init; }
    public decimal Rate { get; init; }
    public long TaxablePaisa { get; init; }
    public long IgstPaisa { get; init; }
    public long CgstPaisa { get; init; }
    public long SgstPaisa { get; init; }
}

/// <summary>Documents issued in the period for GSTR-1 table 13 (first / last number by issue order, cancelled count).</summary>
public sealed record GstDocumentSummary(string Nature, string? FirstNumber, string? LastNumber, long Total, long Cancelled);
