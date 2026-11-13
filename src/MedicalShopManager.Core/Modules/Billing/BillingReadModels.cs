using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Billing;

/// <summary>A product row in the POS search dropdown (spec 02 §F).</summary>
public sealed record SaleCatalogItem
{
    public long MedicineId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Composition { get; init; }
    public string? Manufacturer { get; init; }
    public string? PackLabel { get; init; }
    public int UnitsPerPack { get; init; } = 1;
    public bool AllowLooseSale { get; init; } = true;
    public DrugSchedule Schedule { get; init; }
    public string? HsnCode { get; init; }
    public decimal GstPercent { get; init; }
    public string? RackLocation { get; init; }
    public decimal? MaxDiscountPercent { get; init; }
    public string? Barcode { get; init; }
    /// <summary>Units in non-expired, active batches.</summary>
    public long StockUnits { get; init; }
    public DateTime? NearestExpiry { get; init; }
    /// <summary>MRP per pack of the nearest-expiry batch, else the item default.</summary>
    public long MrpPerPackPaisa { get; init; }
    public int SellableBatchCount { get; init; }

    public bool InStock => StockUnits > 0;
    public string StockDisplay => InStock ? BillingFormat.Quantity(StockUnits, UnitsPerPack) : "-";
    public string StatusLabel => InStock ? $"In Stock{(NearestExpiry is { } e ? $"  Exp: {e:MM/yy}" : string.Empty)}" : "Out of stock";
    public string ScheduleLabel => Schedule.ToLabel();
    public decimal Mrp => MrpPerPackPaisa / 100m;
}

/// <summary>A batch in the batch-selection popup (spec 02 §G).</summary>
public sealed record SaleBatchOption
{
    public long BatchId { get; init; }
    public long MedicineId { get; init; }
    public string BatchNumber { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }
    public int UnitsPerPack { get; init; } = 1;
    public long MrpPaisa { get; init; }
    public long SaleRatePaisa { get; init; }
    public long PurchaseRatePaisa { get; init; }
    public long PtrPaisa { get; init; }
    public decimal DiscountPercent { get; init; }
    public decimal GstPercent { get; init; }
    public long QuantityRemaining { get; init; }
    public DateTime CreatedAt { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>Filled by the service for the bill date and near-expiry window.</summary>
    public BatchExpiryState ExpiryState { get; init; }
    /// <summary>Margin % at the batch sale rate (ex-GST basis); filled by the service.</summary>
    public decimal MarginPercent { get; init; }

    /// <summary>Not expired, but inside the Sale Settings expiry cut-off, so it cannot be sold; filled by the service.</summary>
    public bool BlockedByCutoff { get; init; }

    public bool IsExpired => ExpiryState == BatchExpiryState.Expired;
    public bool IsNearExpiry => ExpiryState == BatchExpiryState.NearExpiry;
    public bool IsSellable => IsActive && QuantityRemaining > 0 && !IsExpired && !BlockedByCutoff;
    /// <summary>"In Stock" / "Expired" / "Expiry cut-off" / "Out of stock" for the batch popup STATUS column.</summary>
    public string StatusLabel => IsExpired ? "Expired" : BlockedByCutoff ? "Expiry cut-off" : QuantityRemaining > 0 ? "In Stock" : "Out of stock";
    public string ExpiryLabel => ExpiryDate.ToString("MM/yy");
    public string StockDisplay => BillingFormat.Quantity(QuantityRemaining, UnitsPerPack);
    public string PackDisplay => $"1 x {UnitsPerPack}";
    public decimal Mrp => MrpPaisa / 100m;
    public decimal SaleRate => (SaleRatePaisa > 0 ? SaleRatePaisa : MrpPaisa) / 100m;

    /// <summary>"Expired" / "Expiring in N months" / "N months to expiry".</summary>
    public string ExpiryHint => BillingCalculator.ExpiryHint(ExpiryDate, DateTime.Today, ExpiryState);
}

/// <summary>A posted sale as the view / edit / print screens use it (header + Billing extras + lines + payments).</summary>
public sealed class BillingInvoice
{
    public long Id { get; set; }
    public string InvoiceNumber { get; set; } = string.Empty;
    public DateTime InvoiceDate { get; set; }
    public SaleStatus Status { get; set; } = SaleStatus.Active;

    public bool IsCounterSale { get; set; }
    public long? CustomerId { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerAddress { get; set; }
    public string? CustomerGstin { get; set; }

    public string? PatientName { get; set; }
    public string? PatientAge { get; set; }
    public PersonGender PatientGender { get; set; }
    public string? PatientAddress { get; set; }
    public long? DoctorId { get; set; }
    public string? DoctorName { get; set; }
    public string? DoctorRegNo { get; set; }
    public string? PrescriptionNumber { get; set; }
    public string? PrescriptionFilePath { get; set; }

    public bool WithGst { get; set; } = true;
    public bool IsInterState { get; set; }
    /// <summary>True when printed as TAX INVOICE (shop charges GST and the bill is with GST); else BILL OF SUPPLY.</summary>
    public bool IsTaxInvoice { get; set; }

    public long SubtotalPaisa { get; set; }
    public long DiscountPaisa { get; set; }
    public long TaxablePaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    public string? CustomChargeName { get; set; }
    public long CustomChargePaisa { get; set; }
    public long RoundOffPaisa { get; set; }
    public long GrandTotalPaisa { get; set; }
    public long MrpTotalPaisa { get; set; }
    public long SavingsPaisa { get; set; }
    public long AmountPaidPaisa { get; set; }
    public long BalanceDuePaisa { get; set; }
    public long CashReceivedPaisa { get; set; }
    public long ChangeReturnedPaisa { get; set; }
    public DateTime? DueDate { get; set; }
    public PaymentMethod PaymentMethod { get; set; }
    public PaymentStatus PaymentStatus { get; set; }
    public string? Notes { get; set; }
    public long? CreatedByUserId { get; set; }
    public string? CreatedByName { get; set; }
    public DateTime CreatedAt { get; set; }

    public List<BillingInvoiceLine> Lines { get; set; } = new();
    public List<BillingPaymentLine> Payments { get; set; } = new();

    public long TaxPaisa => CgstPaisa + SgstPaisa + IgstPaisa;
    public long TotalUnits => Lines.Sum(l => l.Quantity);
    public bool HasScheduledDrugs => Lines.Any(l => l.Schedule.RequiresPrescription());
    public string DocumentTitle => IsTaxInvoice ? "TAX INVOICE" : "BILL OF SUPPLY";
    public string PaymentStatusLabel => Status == SaleStatus.Cancelled ? "Cancelled" : BalanceDuePaisa > 0 ? "Credit" : "Paid";
}

/// <summary>One saved sale line with its pack / loose breakdown.</summary>
public sealed class BillingInvoiceLine
{
    public long SaleItemId { get; set; }
    public long MedicineId { get; set; }
    public string MedicineName { get; set; } = string.Empty;
    public string? Manufacturer { get; set; }
    public DrugSchedule Schedule { get; set; }
    public string? RackLocation { get; set; }
    public string? HsnCode { get; set; }
    public long BatchId { get; set; }
    public string BatchNumber { get; set; } = string.Empty;
    public DateTime ExpiryDate { get; set; }
    public int UnitsPerPack { get; set; } = 1;
    public long Packs { get; set; }
    public long Loose { get; set; }
    public long FreeUnits { get; set; }
    /// <summary>Units that left stock (paid + free).</summary>
    public int Quantity { get; set; }
    public long MrpPerPackPaisa { get; set; }
    public long RatePerPackPaisa { get; set; }
    public decimal DiscountPercent { get; set; }
    public decimal GstPercent { get; set; }
    /// <summary>Line discount + bill-discount share.</summary>
    public long DiscountPaisa { get; set; }
    /// <summary>This line's share of a bill discount entered in rupees.</summary>
    public long BillDiscountPaisa { get; set; }
    public long TaxablePaisa { get; set; }
    public long CgstPaisa { get; set; }
    public long SgstPaisa { get; set; }
    public long IgstPaisa { get; set; }
    public long TaxPaisa { get; set; }
    public long LineTotalPaisa { get; set; }
    public long CogsPaisa { get; set; }

    public long PaidUnits => Quantity - FreeUnits;
    public string QuantityDisplay => BillingFormat.Quantity(PaidUnits, UnitsPerPack) + (FreeUnits > 0 ? $" +{FreeUnits} free" : string.Empty);
    /// <summary>Per-pack rate after the line discount (what the bill prints as RATE).</summary>
    public long NetRatePerPackPaisa => PricingCalculator.RateAfterDiscount(RatePerPackPaisa, Math.Clamp(DiscountPercent, 0m, 100m));
    public string ScheduleLabel => Schedule.ToLabel();
}

/// <summary>A payment row of a sale (Payments table, PaymentType.SalePayment).</summary>
public sealed record BillingPaymentLine(long Id, DateTime PaymentDate, PaymentMethod Method, long AmountPaisa, string? Reference, string? Notes);

/// <summary>Row of the F8 "Unsaved Drafts" list.</summary>
public sealed record SaleDraftSummary
{
    public long Id { get; init; }
    public string? CustomerName { get; init; }
    public int ItemCount { get; init; }
    public long TotalPaisa { get; init; }
    public long? CreatedByUserId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    public string Title => string.IsNullOrWhiteSpace(CustomerName) ? "Cash / Counter Sale" : CustomerName!;
    public decimal Total => TotalPaisa / 100m;
}

/// <summary>A stored draft: summary plus the JSON-serialised <see cref="SalePostingRequest"/>.</summary>
public sealed record SaleDraft(SaleDraftSummary Summary, string Payload);

/// <summary>One line of the Schedule H1 / X / narcotic register (Drugs &amp; Cosmetics Rules, rule 65).</summary>
public sealed record H1RegisterEntry
{
    public long SaleId { get; init; }
    public long SaleItemId { get; init; }
    public string InvoiceNumber { get; init; } = string.Empty;
    public DateTime InvoiceDate { get; init; }
    public DrugSchedule Schedule { get; init; }
    public string MedicineName { get; init; } = string.Empty;
    public string? Manufacturer { get; init; }
    public string BatchNumber { get; init; } = string.Empty;
    public DateTime ExpiryDate { get; init; }
    public int Quantity { get; init; }
    public string? PatientName { get; init; }
    public string? PatientAddress { get; init; }
    public string? DoctorName { get; init; }
    public string? DoctorRegNo { get; init; }
    public string? PrescriptionNumber { get; init; }
    public string? CustomerName { get; init; }
}

/// <summary>Saved custom-charge names (spec 02 §I card 3, e.g. "Delivery Fee").</summary>
public sealed record CustomChargeOption(long Id, string Name);

/// <summary>
/// Settings the POS applies. Most come from Control Center (<c>IControlCenterSettingsService</c>: Sale Settings and the
/// business profile); the two Billing-owned AppSettings keys are the draft retention and the batch pick order.
/// </summary>
public sealed record BillingSettings
{
    public const string DraftRetentionDaysKey = "Billing.DraftRetentionDays";
    public const string BatchOrderKey = "Billing.BatchOrder";

    /// <summary>Show EXPIRING (orange) when expiry is within this many months (spec default 6).</summary>
    public int NearExpiryMonths { get; init; } = 6;
    /// <summary>Batches expiring within this many months cannot be sold (0 = only expired batches are blocked).</summary>
    public int ExpiryCutoffMonths { get; init; }
    /// <summary>Payment step starts on UNPAID/CREDIT instead of PAID/CASH.</summary>
    public bool NewSaleIsCredit { get; init; }
    /// <summary>Credit period when the customer has no credit days of its own.</summary>
    public int DefaultDueDays { get; init; }
    public int DraftRetentionDays { get; init; } = 7;
    /// <summary>Schedule H items also need a doctor name (H1/X/NRx always do).</summary>
    public bool RequireDoctorForScheduleH { get; init; }
    /// <summary>Bills with prescription-only items need an Rx number.</summary>
    public bool RequirePrescriptionReference { get; init; }
    public BatchPickOrder BatchOrder { get; init; } = BatchPickOrder.Fefo;
    public RoundOffMode RoundOff { get; init; } = RoundOffMode.NearestRupee;
    /// <summary>Allow billing more than a batch's stock (the batch goes negative).</summary>
    public bool AllowNegativeStock { get; init; }
    /// <summary>Discount % pre-filled on new lines when neither the customer nor the batch has one.</summary>
    public decimal DefaultDiscountPercent { get; init; }
    /// <summary>Bounds of the bill-level discount % (Sale Settings).</summary>
    public decimal MinDiscountPercent { get; init; }
    public decimal MaxDiscountPercent { get; init; } = 100m;
    /// <summary>A barcode scan adds a whole pack (true) or one unit (false).</summary>
    public bool BarcodeQtyInPacks { get; init; } = true;
    public long? DefaultDoctorId { get; init; }
    public string? DefaultDoctorName { get; init; }

    /// <summary>Two-digit state code of the shop, for inter-state detection (IGST).</summary>
    public string? ShopStateCode { get; init; }
    /// <summary>Shop is a regular GST taxpayer: sales are Tax Invoices with GST. False = Bill of Supply without GST.</summary>
    public bool ShopChargesGst { get; init; } = true;

    /// <summary>Builds the POS settings from Control Center's typed settings plus the Billing-owned values.</summary>
    public static BillingSettings From(SaleSettings sale, BusinessProfile profile, int draftRetentionDays, BatchPickOrder batchOrder)
    {
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(profile);
        var gstin = profile.EffectiveGstin;
        var state = !string.IsNullOrWhiteSpace(profile.StateCode) ? profile.StateCode.Trim()
            : gstin.Length >= 2 && char.IsAsciiDigit(gstin[0]) && char.IsAsciiDigit(gstin[1]) ? gstin[..2] : null;
        return new BillingSettings
        {
            NearExpiryMonths = Math.Clamp(sale.NearExpiryMonths, 0, 24),
            ExpiryCutoffMonths = Math.Clamp(sale.ExpiryCutoffMonths, 0, 24),
            NewSaleIsCredit = sale.NewSaleIsCredit,
            DefaultDueDays = Math.Clamp(sale.DefaultDueDays, 0, 365),
            DraftRetentionDays = Math.Clamp(draftRetentionDays, 1, 90),
            RequireDoctorForScheduleH = sale.RequireDoctorForScheduleH,
            RequirePrescriptionReference = sale.RequirePrescriptionReference,
            BatchOrder = batchOrder,
            RoundOff = sale.RoundOffMode,
            AllowNegativeStock = sale.AllowNegativeStock,
            DefaultDiscountPercent = Math.Clamp(sale.DefaultDiscountPercent, 0m, 100m),
            MinDiscountPercent = Math.Clamp(sale.MinDiscountPercent, 0m, 100m),
            MaxDiscountPercent = Math.Clamp(sale.MaxDiscountPercent, 0m, 100m),
            BarcodeQtyInPacks = sale.BarcodeQtyInPacks,
            DefaultDoctorId = sale.DefaultDoctorId,
            DefaultDoctorName = string.IsNullOrWhiteSpace(sale.DefaultDoctorName) ? null : sale.DefaultDoctorName.Trim(),
            ShopStateCode = state,
            ShopChargesGst = profile.GstRegistrationType.ChargesGst()
        };
    }
}

/// <summary>
/// Navigation parameters accepted by the "NewBill" page (<c>INavigationService.NavigateTo("NewBill", request)</c>).
/// Other modules (Sales register, search, drafts) open sales through these instead of the Billing ViewModel.
/// A plain <c>long</c> parameter is treated as <see cref="OpenSaleRequest"/>.
/// </summary>
public sealed record OpenSaleRequest(long SaleId);

/// <summary>Reopens an unsaved draft on the billing screen.</summary>
public sealed record OpenSaleDraftRequest(long DraftId);

/// <summary>Starts a new bill pre-filled from an existing sale with currently available batches.</summary>
public sealed record ReplicateSaleRequest(long SaleId);
