using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Billing;

/// <summary>
/// Pure POS rules for the Billing module (spec 02 "Business rules &amp; calculations"): bill computation (all money
/// through <see cref="PricingCalculator"/>), line and sale validation with Localwell's messages, the payment step,
/// batch ordering and expiry flags. No I/O and no clock: callers pass the bill date / today.
/// </summary>
public static class BillingCalculator
{
    /// <summary>Product search starts after this many characters (spec 02 §E "Type Min 3 Characters").</summary>
    public const int MinSearchLength = 3;

    // ───────────────────────── Bill computation ─────────────────────────

    /// <summary>
    /// Computes every line and the bill totals. Line discount % is clamped to the item cap; a bill discount in
    /// percent is added to each line's % (clamped, spec 02 "Bill discount"); a bill discount in value is spread over
    /// the lines pro rata with <see cref="PricingCalculator.DistributeAmount"/>. A bill without GST (bill of supply)
    /// computes no tax. Totals are rounded per <see cref="BillRequest.RoundOff"/>; the custom charge is a non-taxable add-on.
    /// </summary>
    /// <exception cref="BillingValidationException">Negative quantities, an invalid bill discount or charge.</exception>
    public static BillComputation ComputeBill(BillRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = ValidateBillAdjustments(request.BillDiscountKind, request.BillDiscountPercent,
            request.BillDiscountAmountPaisa, request.CustomChargePaisa);
        if (request.Lines.Any(l => l.Packs < 0 || l.Loose < 0 || l.FreeUnits < 0))
            errors.Add("Quantity cannot be negative");
        if (errors.Count > 0) throw new BillingValidationException(errors);

        var prepared = request.Lines.Select(line => Prepare(line, request)).ToList();

        var shares = new long[prepared.Count];
        if (request.BillDiscountKind == BillDiscountKind.Amount && request.BillDiscountAmountPaisa > 0)
        {
            var weights = prepared.Select(p => PricingCalculator.CalculateSaleLine(p.Input).TotalPaisa).ToList();
            if (request.BillDiscountAmountPaisa > weights.Sum())
                throw new BillingValidationException("Discount amount cannot exceed the subtotal");
            shares = PricingCalculator.DistributeAmount(request.BillDiscountAmountPaisa, weights);
        }

        var computed = new List<ComputedBillLine>(prepared.Count);
        for (var i = 0; i < prepared.Count; i++)
        {
            var p = prepared[i];
            var amounts = PricingCalculator.CalculateSaleLine(p.Input with { BillDiscountSharePaisa = shares[i] });
            var margin = p.Line.CostPerPackExGstPaisa > 0
                ? PricingCalculator.MarginPercent(amounts.DiscountedRatePerPackPaisa, p.Input.GstPercent, p.Line.CostPerPackExGstPaisa)
                : 0m;
            computed.Add(new ComputedBillLine(p.Line, p.Input.Units, p.Input.DiscountPercent, p.Input.GstPercent, amounts, margin));
        }

        var totals = PricingCalculator.SummarizeBill(computed.Select(c => c.Amounts).ToList(), request.RoundOff,
            otherChargesPaisa: request.CustomChargePaisa);
        return new BillComputation(computed, totals);
    }

    private sealed record PreparedLine(BillLine Line, SaleLineInput Input);

    private static PreparedLine Prepare(BillLine line, BillRequest request)
    {
        var unitsPerPack = Math.Max(line.UnitsPerPack, 1);
        var discount = PricingCalculator.ClampDiscount(line.DiscountPercent, 0m, line.MaxDiscountPercent);
        if (request.BillDiscountKind == BillDiscountKind.Percent && request.BillDiscountPercent > 0)
        {
            var max = Math.Min(request.BillDiscountMaxPercent, line.MaxDiscountPercent ?? 100m);
            var min = Math.Min(request.BillDiscountMinPercent, max);
            discount = PricingCalculator.CombineBillDiscount(discount, request.BillDiscountPercent, min, max);
        }

        var input = new SaleLineInput
        {
            RatePerPackPaisa = BaseRate(line),
            MrpPerPackPaisa = line.MrpPerPackPaisa,
            UnitsPerPack = unitsPerPack,
            Units = line.PaidUnits,
            DiscountPercent = discount,
            GstPercent = request.WithGst ? line.GstPercent : 0m,
            TaxMode = TaxMode.Inclusive,
            IsInterState = request.IsInterState,
            CostPerPackExGstPaisa = Math.Max(line.CostPerPackExGstPaisa, 0)
        };
        return new PreparedLine(line, input);
    }

    /// <summary>The per-pack rate a discount applies to: the line rate, else MRP.</summary>
    public static long BaseRate(BillLine line) => line.RatePerPackPaisa > 0 ? line.RatePerPackPaisa : line.MrpPerPackPaisa;

    private static List<string> ValidateBillAdjustments(BillDiscountKind kind, decimal percent, long amountPaisa, long chargePaisa)
    {
        var errors = new List<string>();
        if (kind == BillDiscountKind.Percent)
        {
            if (percent < 0m) errors.Add("Discount cannot be negative");
            else if (percent > 100m) errors.Add("Discount cannot exceed 100%");
        }
        if (kind == BillDiscountKind.Amount && amountPaisa < 0) errors.Add("Discount amount cannot be negative");
        if (chargePaisa < 0) errors.Add("Custom charge cannot be negative");
        return errors;
    }

    /// <summary>Bill discount % ↔ value, relative to the MRP value of the lines (spec 02 §I "computes the other live").</summary>
    public static long BillDiscountAmountFromPercent(decimal percent, long baseValuePaisa) =>
        percent <= 0m || baseValuePaisa <= 0 ? 0 : PricingCalculator.PercentOf(baseValuePaisa, Math.Min(percent, 100m));

    public static decimal BillDiscountPercentFromAmount(long amountPaisa, long baseValuePaisa) =>
        PricingCalculator.PercentFromAmount(Math.Max(amountPaisa, 0), baseValuePaisa);

    // ───────────────────────── Line defaults ─────────────────────────

    /// <summary>
    /// Default DISC % for a new line (spec 02 "Discount rules"): the customer's sales discount, else the batch's
    /// item discount, else the Sale Settings default; never less than the discount implied by a batch sale rate below
    /// MRP (the rate base is MRP); clamped to the item cap.
    /// </summary>
    public static decimal DefaultDiscountPercent(decimal customerDiscountPercent, SaleBatchOption batch, decimal? maxDiscountPercent,
        decimal settingsDefaultPercent = 0m)
    {
        ArgumentNullException.ThrowIfNull(batch);
        var implied = batch.SaleRatePaisa > 0 && batch.MrpPaisa > 0 && batch.SaleRatePaisa < batch.MrpPaisa
            ? PricingCalculator.DiscountPercentFromRate(batch.MrpPaisa, batch.SaleRatePaisa)
            : 0m;
        var candidate = customerDiscountPercent > 0m ? customerDiscountPercent
            : batch.DiscountPercent > 0m ? batch.DiscountPercent
            : settingsDefaultPercent;
        return PricingCalculator.ClampDiscount(Math.Max(candidate, implied), 0m, maxDiscountPercent);
    }

    /// <summary>Builds a bill line for a batch with the default discount and the given quantity.</summary>
    public static BillLine CreateLine(SaleCatalogItem item, SaleBatchOption batch, decimal customerDiscountPercent,
        long packs = 1, long loose = 0, decimal settingsDefaultDiscountPercent = 0m)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(batch);
        var mrp = batch.MrpPaisa > 0 ? batch.MrpPaisa : batch.SaleRatePaisa;
        return new BillLine
        {
            MedicineId = item.MedicineId,
            MedicineName = item.Name,
            Manufacturer = item.Manufacturer,
            Composition = item.Composition,
            Schedule = item.Schedule,
            HsnCode = item.HsnCode,
            RackLocation = item.RackLocation,
            BatchId = batch.BatchId,
            BatchNumber = batch.BatchNumber,
            ExpiryDate = batch.ExpiryDate,
            UnitsPerPack = Math.Max(batch.UnitsPerPack, 1),
            AllowLooseSale = item.AllowLooseSale,
            MrpPerPackPaisa = mrp,
            RatePerPackPaisa = mrp,
            DiscountPercent = DefaultDiscountPercent(customerDiscountPercent, batch, item.MaxDiscountPercent, settingsDefaultDiscountPercent),
            MaxDiscountPercent = item.MaxDiscountPercent,
            GstPercent = batch.GstPercent > 0 ? batch.GstPercent : item.GstPercent,
            Packs = packs,
            Loose = loose,
            CostPerPackExGstPaisa = batch.PurchaseRatePaisa,
            AvailableUnits = batch.QuantityRemaining
        };
    }

    /// <summary>
    /// DISC % for a RATE typed on the grid (Localwell shows the discounted per-pack rate in RATE): the discount
    /// relative to the line's base rate (MRP), clamped to 0 … the item cap. A rate above MRP gives 0 %.
    /// </summary>
    public static decimal DiscountFromEnteredRate(BillLine line, long enteredRatePerPackPaisa)
    {
        ArgumentNullException.ThrowIfNull(line);
        var baseRate = BaseRate(line);
        if (baseRate <= 0 || enteredRatePerPackPaisa >= baseRate) return 0m;
        var percent = PricingCalculator.DiscountPercentFromRate(baseRate, Math.Max(enteredRatePerPackPaisa, 0));
        return PricingCalculator.ClampDiscount(percent, 0m, line.MaxDiscountPercent);
    }

    /// <summary>
    /// A barcode scan of an item already on the bill: one more pack, or one more loose unit when scans add units
    /// (Sale Settings BARCODE QTY / ADD SCAN QTY) and the item allows loose sale. Loose units roll over into packs.
    /// </summary>
    public static BillLine AddScan(BillLine line, bool inPacks)
    {
        ArgumentNullException.ThrowIfNull(line);
        var pack = Math.Max(line.UnitsPerPack, 1);
        if (inPacks || !line.AllowLooseSale || pack == 1) return line with { Packs = line.Packs + 1 };
        var units = line.PaidUnits + 1;
        return line with { Packs = units / pack, Loose = units % pack };
    }

    /// <summary>Quantity a first scan adds: 1 pack, or 1 loose unit when scans add units and loose sale is allowed.</summary>
    public static (long Packs, long Loose) FirstScanQuantity(SaleCatalogItem item, SaleBatchOption batch, bool inPacks)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(batch);
        return inPacks || !item.AllowLooseSale || Math.Max(batch.UnitsPerPack, 1) == 1 ? (1, 0) : (0, 1);
    }

    // ───────────────────────── Validation ─────────────────────────

    /// <summary>
    /// Errors for one line, in Localwell's order, each suffixed " in Row: N". A batch is blocked when expired on the
    /// bill date or inside the expiry cut-off; quantity above the batch stock is refused unless negative stock is allowed.
    /// </summary>
    public static IReadOnlyList<string> ValidateLine(BillLine line, int rowNumber, DateTime billDate,
        int expiryCutoffMonths = 0, bool allowNegativeStock = false)
    {
        ArgumentNullException.ThrowIfNull(line);
        var errors = new List<string>();
        void Add(string message) => errors.Add($"{message} in Row: {rowNumber}");

        if (line.MedicineId <= 0 || string.IsNullOrWhiteSpace(line.MedicineName)) Add("Please fill Product Name");
        if (line.BatchId <= 0 || string.IsNullOrWhiteSpace(line.BatchNumber)) Add("Please fill Batch No");
        if (line.MrpPerPackPaisa <= 0) Add("Please fill MRP");
        if (BaseRate(line) <= 0) Add("Please fill Sale Rate");
        else if (line.MrpPerPackPaisa > 0 && BaseRate(line) > line.MrpPerPackPaisa) Add("Sale Rate cannot exceed MRP");

        var max = Math.Clamp(line.MaxDiscountPercent ?? 100m, 0m, 100m);
        if (line.DiscountPercent < 0m || line.DiscountPercent > max) Add($"Discount Must be Between 0% and {max:0.##}%");

        if (line.Packs < 0 || line.Loose < 0 || line.FreeUnits < 0 || line.Packs * Math.Max(line.UnitsPerPack, 1) + line.Loose <= 0)
            Add("Please fill valid Qty Packs/Loose");
        else
        {
            if (!line.AllowLooseSale && line.Loose > 0) Add("Loose sale is not allowed for this item");
            if (!allowNegativeStock && line.StockUnits > line.AvailableUnits)
                Add($"Only {BillingFormat.Quantity(line.AvailableUnits, line.UnitsPerPack)} available in batch {line.BatchNumber}");
        }

        if (line.BatchId > 0 && line.ExpiryDate.Date <= billDate.Date)
            Add($"Batch {line.BatchNumber} expired on {line.ExpiryDate:dd-MM-yyyy} and cannot be sold");
        else if (line.BatchId > 0 && IsWithinCutoff(line.ExpiryDate, billDate, expiryCutoffMonths))
            Add($"Batch {line.BatchNumber} expires on {line.ExpiryDate:dd-MM-yyyy}, inside the {expiryCutoffMonths}-month expiry cut-off");
        return errors;
    }

    /// <summary>
    /// Invoice-level checks before the payment step (spec 02 §M "check_sale_payload" + Schedule H1/X capture).
    /// Payment checks are separate (<see cref="ResolvePayment"/>).
    /// </summary>
    public static IReadOnlyList<string> ValidateSale(SalePostingRequest request, BillingSettings settings, DateTime today)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(settings);
        var errors = new List<string>();

        if (request.Lines.Count == 0) errors.Add("Please add Products");
        if (request.InvoiceDate == default) errors.Add("Sale Invoice Date cannot be Empty");
        else if (request.InvoiceDate.Date > today.Date) errors.Add("Sale Invoice Date cannot be in the future");
        if (!request.IsCounterSale && request.CustomerId is not > 0) errors.Add("Please add customer");
        if (request.CustomChargePaisa > 0 && string.IsNullOrWhiteSpace(request.CustomChargeName)) errors.Add("Please add custom charge type");
        errors.AddRange(ValidateBillAdjustments(request.BillDiscountKind, request.BillDiscountPercent,
            request.BillDiscountAmountPaisa, request.CustomChargePaisa));

        for (var i = 0; i < request.Lines.Count; i++)
            errors.AddRange(ValidateLine(request.Lines[i], i + 1, request.InvoiceDate, settings.ExpiryCutoffMonths, settings.AllowNegativeStock));

        foreach (var group in request.Lines.Where(l => l.BatchId > 0).GroupBy(l => l.BatchId).Where(g => g.Count() > 1))
        {
            var first = group.First();
            var available = group.Max(l => l.AvailableUnits);
            if (!settings.AllowNegativeStock && group.Sum(l => l.StockUnits) > available)
                errors.Add($"Batch {first.BatchNumber} of {first.MedicineName} is added more than once and exceeds its stock " +
                           $"({BillingFormat.Quantity(available, first.UnitsPerPack)})");
        }

        errors.AddRange(ValidateScheduleCapture(request, settings));
        return errors;
    }

    /// <summary>
    /// H1 / X / NRx items need patient name, doctor name and Rx number for the statutory register; Schedule H items
    /// need a doctor when <see cref="BillingSettings.RequireDoctorForScheduleH"/> is on, and any prescription-only item
    /// needs an Rx number when <see cref="BillingSettings.RequirePrescriptionReference"/> is on.
    /// </summary>
    public static IReadOnlyList<string> ValidateScheduleCapture(SalePostingRequest request, BillingSettings settings)
    {
        var errors = new List<string>();
        var register = request.Lines.Where(l => l.Schedule.RequiresRegisterEntry()).Select(l => l.MedicineName).Distinct().ToList();
        if (register.Count > 0)
        {
            var items = string.Join(", ", register);
            if (string.IsNullOrWhiteSpace(request.PatientName)) errors.Add($"Patient name is required for Schedule H1/X items ({items})");
            if (string.IsNullOrWhiteSpace(request.DoctorName)) errors.Add($"Doctor name is required for Schedule H1/X items ({items})");
            if (string.IsNullOrWhiteSpace(request.PrescriptionNumber)) errors.Add($"Prescription (Rx) number is required for Schedule H1/X items ({items})");
        }
        else
        {
            if (settings.RequireDoctorForScheduleH
                && request.Lines.Any(l => l.Schedule == DrugSchedule.H)
                && string.IsNullOrWhiteSpace(request.DoctorName))
                errors.Add("Invoice has Schedule H drugs: please fill Doctor name");
            if (settings.RequirePrescriptionReference
                && request.Lines.Any(l => l.Schedule.RequiresPrescription())
                && string.IsNullOrWhiteSpace(request.PrescriptionNumber))
                errors.Add("Invoice has Prescription drugs: please fill the Prescription (Rx) number");
        }
        return errors;
    }

    // ───────────────────────── Payment step ─────────────────────────

    /// <summary>
    /// Validates the payment step and resolves paid / due / change (spec 02 §M). Tenders cannot exceed the payable
    /// amount; cash handed over above the cash tender is change. A counter sale must be fully paid; a credit or
    /// part-paid sale needs a real customer and a due date on or after the invoice date.
    /// </summary>
    public static (PaymentResolution? Resolution, IReadOnlyList<string> Errors) ResolvePayment(long netPayablePaisa, SalePostingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var errors = new List<string>();
        if (request.Tenders.Any(t => t.AmountPaisa < 0)) errors.Add("Payment Amount cannot be negative");
        var tenders = request.Tenders.Where(t => t.AmountPaisa > 0).ToList();
        var paid = tenders.Sum(t => t.AmountPaisa);

        if (request.IsCredit && request.IsCounterSale) errors.Add("A Cash/Counter sale cannot be on credit; select a customer");
        if (request.IsCredit && tenders.Count > 0) errors.Add("Remove payments for an UNPAID/CREDIT sale, or choose PAID/CASH");
        if (!request.IsCredit && paid == 0 && netPayablePaisa > 0) errors.Add("Please Fill Payment Amount");
        if (paid > netPayablePaisa) errors.Add("Payment Amount > Payable Amount");
        if (request.IsCounterSale && paid < netPayablePaisa && !request.IsCredit && paid > 0)
            errors.Add("Cash/Counter sale must be paid in full; select a customer to give credit");

        foreach (var cheque in tenders.Where(t => t.Mode == TenderMode.Cheque))
        {
            var digits = cheque.Reference?.Trim() ?? string.Empty;
            if (digits.Length < 6 || !digits.All(char.IsAsciiDigit)) errors.Add("Cheque number must be at least 6 digits");
        }

        var cashTender = tenders.Where(t => t.Mode == TenderMode.Cash).Sum(t => t.AmountPaisa);
        var cashGiven = request.CashTenderedPaisa > 0 ? request.CashTenderedPaisa : cashTender;
        if (cashGiven < cashTender) errors.Add("Cash received is less than the cash payment amount");

        var due = Math.Max(netPayablePaisa - paid, 0);
        if (due > 0 && errors.Count == 0)
        {
            if (request.DueDate is not { } dueDate || dueDate.Date < request.InvoiceDate.Date)
                errors.Add("Please provide a valid payment due date");
        }

        if (errors.Count > 0) return (null, errors);

        var status = netPayablePaisa == 0 || due == 0 ? PaymentStatus.Paid : paid == 0 ? PaymentStatus.Unpaid : PaymentStatus.Partial;
        var method = tenders.Select(t => t.Mode).Distinct().Count() switch
        {
            0 => due > 0 ? PaymentMethod.Credit : PaymentMethod.Cash,
            1 => ToPaymentMethod(tenders[0].Mode),
            _ => PaymentMethod.Split
        };
        return (new PaymentResolution
        {
            AmountPaidPaisa = paid,
            BalanceDuePaisa = due,
            CashTenderedPaisa = cashTender > 0 ? cashGiven : 0,
            ChangePaisa = cashTender > 0 ? cashGiven - cashTender : 0,
            Status = status,
            Method = method,
            Tenders = tenders
        }, errors);
    }

    /// <summary>Shared payment method a tender is stored as (cheque = bank).</summary>
    public static PaymentMethod ToPaymentMethod(TenderMode mode) => mode switch
    {
        TenderMode.Cash => PaymentMethod.Cash,
        TenderMode.Upi => PaymentMethod.UPI,
        TenderMode.Card => PaymentMethod.Card,
        TenderMode.BankTransfer or TenderMode.Cheque => PaymentMethod.BankTransfer,
        _ => PaymentMethod.Other
    };

    /// <summary>Default due date: invoice date + the customer's credit days.</summary>
    public static DateTime DefaultDueDate(DateTime invoiceDate, int creditDays) => invoiceDate.Date.AddDays(Math.Max(creditDays, 0));

    /// <summary>"Due Today" / "Due in N days" / "Overdue by N days" (spec 02 §M step 1 DUE DAYS).</summary>
    public static string DueDaysLabel(DateTime dueDate, DateTime today)
    {
        var days = (dueDate.Date - today.Date).Days;
        return days switch
        {
            0 => "Due Today",
            > 0 => $"Due in {days} day{(days == 1 ? "" : "s")}",
            _ => $"Overdue by {-days} day{(days == -1 ? "" : "s")}"
        };
    }

    // ───────────────────────── Batches and expiry ─────────────────────────

    /// <summary>Expired from the expiry date onwards (as <c>MedicineBatch.IsExpired</c>); near expiry within <paramref name="nearExpiryMonths"/>.</summary>
    public static BatchExpiryState ExpiryStateOf(DateTime expiryDate, DateTime billDate, int nearExpiryMonths)
    {
        if (expiryDate.Date <= billDate.Date) return BatchExpiryState.Expired;
        return expiryDate.Date < billDate.Date.AddMonths(Math.Max(nearExpiryMonths, 0)) ? BatchExpiryState.NearExpiry : BatchExpiryState.Ok;
    }

    public static string ExpiryHint(DateTime expiryDate, DateTime today, BatchExpiryState state)
    {
        var months = (expiryDate.Year - today.Year) * 12 + expiryDate.Month - today.Month;
        return state switch
        {
            BatchExpiryState.Expired => months < 0 ? $"Expired {-months} month{(months == -1 ? "" : "s")} back" : "Expired",
            BatchExpiryState.NearExpiry => months <= 0 ? "Expiring this month" : $"Expiring in {months} month{(months == 1 ? "" : "s")}",
            _ => $"{months} months to expiry"
        };
    }

    /// <summary>True when a not-yet-expired batch expires within the cut-off window (blocked for sale).</summary>
    public static bool IsWithinCutoff(DateTime expiryDate, DateTime billDate, int cutoffMonths) =>
        cutoffMonths > 0 && expiryDate.Date > billDate.Date && expiryDate.Date <= billDate.Date.AddMonths(cutoffMonths);

    /// <summary>Sellable batches first, in FEFO (expiry, then received) or LIFO (latest received) order.</summary>
    public static IReadOnlyList<SaleBatchOption> OrderBatches(IEnumerable<SaleBatchOption> batches, BatchPickOrder order)
    {
        var sorted = order == BatchPickOrder.Lifo
            ? batches.OrderByDescending(b => b.IsSellable).ThenByDescending(b => b.CreatedAt).ThenBy(b => b.ExpiryDate)
            : batches.OrderByDescending(b => b.IsSellable).ThenBy(b => b.ExpiryDate).ThenBy(b => b.CreatedAt);
        return sorted.ThenBy(b => b.BatchId).ToList();
    }

    /// <summary>The batch auto-picked for a product: the first sellable one in pick order (FEFO by default), or null.</summary>
    public static SaleBatchOption? PickDefaultBatch(IEnumerable<SaleBatchOption> batches, BatchPickOrder order = BatchPickOrder.Fefo) =>
        OrderBatches(batches, order).FirstOrDefault(b => b.IsSellable);

    /// <summary>Annotates batches with expiry state, the expiry cut-off and margin at the batch sale rate.</summary>
    public static IReadOnlyList<SaleBatchOption> Annotate(IEnumerable<SaleBatchOption> batches, DateTime billDate, int nearExpiryMonths,
        int expiryCutoffMonths = 0) =>
        batches.Select(b => b with
        {
            ExpiryState = ExpiryStateOf(b.ExpiryDate, billDate, nearExpiryMonths),
            BlockedByCutoff = IsWithinCutoff(b.ExpiryDate, billDate, expiryCutoffMonths),
            MarginPercent = b.PurchaseRatePaisa > 0
                ? PricingCalculator.MarginPercent(b.SaleRatePaisa > 0 ? b.SaleRatePaisa : b.MrpPaisa, Math.Clamp(b.GstPercent, 0m, 100m), b.PurchaseRatePaisa)
                : 0m
        }).ToList();

    /// <summary>A draft older than the retention window is purged (spec 02 §O: 7 days).</summary>
    public static bool IsDraftExpired(DateTime updatedAt, DateTime now, int retentionDays) => updatedAt < now.AddDays(-Math.Max(retentionDays, 1));

    /// <summary>Barcode scans are digit strings of 8+ characters (EAN-8/13, UPC); they add the item immediately.</summary>
    public static bool LooksLikeBarcode(string? text)
    {
        var t = text?.Trim();
        return t is { Length: >= 8 } && t.All(char.IsAsciiLetterOrDigit) && t.Count(char.IsAsciiDigit) >= 8;
    }
}
