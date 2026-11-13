using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.Core.Modules.Stock;

/// <summary>
/// Pure rules of the Stock workspace: batch status, cost / margin per batch, the stock list aggregate and filters,
/// the item timeline, and validation of adjustments, opening batches, item settings and bulk updates.
/// No I/O and no clock: "today" is always passed in. All money goes through <see cref="PricingCalculator"/>.
/// </summary>
public static class StockRules
{
    /// <summary>Days ahead that count as near expiry when the NearExpiryDays setting is missing.</summary>
    public const int DefaultNearExpiryDays = 90;

    /// <summary>Longest batch number accepted (printed batch numbers are rarely over 15 characters).</summary>
    public const int MaxBatchNumberLength = 30;

    /// <summary>Highest GST slab for medicines (MedicineService.Validate uses the same bound).</summary>
    public const decimal MaxGstPercent = 28m;

    public static string MovementLabel(MovementType type) => type switch
    {
        MovementType.PurchaseReceipt => "Purchase",
        MovementType.SaleDispatch => "Sale",
        MovementType.SaleReturn => "Sale Return",
        MovementType.PurchaseReturn => "Purchase Return",
        MovementType.StockAdjustment => "Adjustment",
        MovementType.Damaged => "Damaged",
        MovementType.Expired => "Expired",
        MovementType.WriteOff => "Write-off",
        MovementType.OpeningStock => "Opening Stock",
        _ => "Other"
    };

    // ───────────────────────── Batch status and expiry ─────────────────────────

    /// <summary>A batch is expired from its expiry date onwards (same rule as MedicineBatch.IsExpired).</summary>
    public static bool IsExpired(DateTime expiry, DateTime today) => expiry.Date <= today.Date;

    public static bool IsNearExpiry(DateTime expiry, DateTime today, int nearExpiryDays) =>
        !IsExpired(expiry, today) && expiry.Date <= today.Date.AddDays(Math.Max(nearExpiryDays, 0));

    /// <summary>Active, in stock and not expired: the only batches billing may sell.</summary>
    public static bool IsSellable(MedicineBatch batch, DateTime today) =>
        batch.IsActive && batch.QuantityRemaining > 0 && !IsExpired(batch.ExpiryDate, today);

    /// <summary>Status precedence: inactive, expired, out of stock, near expiry, in stock.</summary>
    public static BatchStockStatus StatusOf(MedicineBatch batch, DateTime today, int nearExpiryDays)
    {
        if (!batch.IsActive) return BatchStockStatus.Inactive;
        if (IsExpired(batch.ExpiryDate, today)) return BatchStockStatus.Expired;
        if (batch.QuantityRemaining <= 0) return BatchStockStatus.OutOfStock;
        return IsNearExpiry(batch.ExpiryDate, today, nearExpiryDays) ? BatchStockStatus.NearExpiry : BatchStockStatus.InStock;
    }

    public static string ExpiryHint(DateTime expiry, DateTime today)
    {
        var days = (expiry.Date - today.Date).Days;
        return days switch
        {
            < 0 => $"Expired {-days} day{(days == -1 ? "" : "s")} ago",
            0 => "Expires today",
            _ => $"Expires in {days} day{(days == 1 ? "" : "s")}"
        };
    }

    // ───────────────────────── Rates, cost and margin ─────────────────────────

    /// <summary>Purchase rate after the batch discount, ex-GST, per pack (Localwell NET RATE).</summary>
    public static long NetRatePaisa(MedicineBatch batch) =>
        PricingCalculator.RateAfterDiscount(Math.Max(batch.PurchaseRatePaisa, 0), Math.Clamp(batch.DiscountPercent, 0m, 100m));

    /// <summary>
    /// Landed cost per pack ex-GST with free units spread over the whole receipt (batch_net_purchase_rate):
    /// the purchase line's NetPurchaseRatePerPackPaisa. Equals <see cref="NetRatePaisa"/> when nothing came free,
    /// and 0 when everything came free.
    /// </summary>
    public static long CostPaisa(MedicineBatch batch)
    {
        if (batch.FreeUnits <= 0) return NetRatePaisa(batch);
        var paid = Math.Max(batch.QuantityReceived - batch.FreeUnits, 0);
        if (paid == 0) return 0;
        var line = PricingCalculator.CalculatePurchaseLine(new PurchaseLineInput
        {
            RatePerPackPaisa = Math.Max(batch.PurchaseRatePaisa, 0),
            Units = paid,
            FreeUnits = batch.FreeUnits,
            UnitsPerPack = Math.Max(batch.UnitsPerPack, 1),
            DiscountPercent = Math.Clamp(batch.DiscountPercent, 0m, 100m)
        });
        return line.NetPurchaseRatePerPackPaisa;
    }

    /// <summary>Selling rate per pack: the batch's sale rate, else its MRP.</summary>
    public static long EffectiveSaleRatePaisa(MedicineBatch batch) => batch.SaleRatePaisa > 0 ? batch.SaleRatePaisa : batch.MrpPaisa;

    /// <summary>Margin % on the selling price excluding GST against the batch cost (PricingCalculator.MarginPercent).</summary>
    public static decimal MarginPercent(MedicineBatch batch) =>
        PricingCalculator.MarginPercent(EffectiveSaleRatePaisa(batch), Math.Clamp(batch.GstRatePercent, 0m, 100m), CostPaisa(batch));

    /// <summary>Value of the batch's remaining units at cost (stock value basis).</summary>
    public static long StockValuePaisa(MedicineBatch batch) =>
        batch.QuantityRemaining <= 0 ? 0 : PricingCalculator.AmountForUnits(CostPaisa(batch), batch.QuantityRemaining, Math.Max(batch.UnitsPerPack, 1));

    /// <summary>Value of the batch's remaining units at MRP.</summary>
    public static long MrpValuePaisa(MedicineBatch batch) =>
        batch.QuantityRemaining <= 0 ? 0 : PricingCalculator.AmountForUnits(batch.MrpPaisa, batch.QuantityRemaining, Math.Max(batch.UnitsPerPack, 1));

    // ───────────────────────── Item aggregate ─────────────────────────

    /// <summary>
    /// The batch whose rates represent the item: the first sellable batch in FEFO order, else the newest batch
    /// that still holds stock, else the newest batch. Null when the item has no batches.
    /// </summary>
    public static MedicineBatch? PrimaryBatch(IEnumerable<MedicineBatch> batches, DateTime today)
    {
        var list = batches.ToList();
        return list.Where(b => IsSellable(b, today)).OrderBy(b => b.ExpiryDate).ThenBy(b => b.Id).FirstOrDefault()
               ?? list.Where(b => b.QuantityRemaining > 0).OrderByDescending(b => b.Id).FirstOrDefault()
               ?? list.OrderByDescending(b => b.Id).FirstOrDefault();
    }

    public static StockBatchRow ToBatchRow(MedicineBatch batch, DateTime today, int nearExpiryDays) => new()
    {
        BatchId = batch.Id,
        BatchNumber = batch.BatchNumber,
        ExpiryDate = batch.ExpiryDate,
        UnitsPerPack = Math.Max(batch.UnitsPerPack, 1),
        QuantityUnits = batch.QuantityRemaining,
        MrpPaisa = batch.MrpPaisa,
        PurchaseRatePaisa = batch.PurchaseRatePaisa,
        NetRatePaisa = NetRatePaisa(batch),
        CostPaisa = CostPaisa(batch),
        SaleRatePaisa = EffectiveSaleRatePaisa(batch),
        PtrPaisa = batch.PtrPaisa,
        GstRatePercent = batch.GstRatePercent,
        MarginPercent = MarginPercent(batch),
        Status = StatusOf(batch, today, nearExpiryDays),
        ExpiryHint = ExpiryHint(batch.ExpiryDate, today),
        SupplierId = batch.SupplierId
    };

    /// <summary>BATCHES tab: sellable batches first in FEFO order, then the rest by expiry. Zero-stock batches only when asked.</summary>
    public static IReadOnlyList<StockBatchRow> BatchRows(IEnumerable<MedicineBatch> batches, DateTime today, int nearExpiryDays, bool showZero) =>
        batches.Where(b => showZero || b.QuantityRemaining != 0)
            .OrderBy(b => IsSellable(b, today) ? 0 : 1)
            .ThenBy(b => b.ExpiryDate)
            .ThenBy(b => b.Id)
            .Select(b => ToBatchRow(b, today, nearExpiryDays))
            .ToList();

    /// <summary>One stock-list card from the item master and all of its batches.</summary>
    public static StockListItem Summarize(Medicine medicine, IReadOnlyList<MedicineBatch> batches, DateTime today, int nearExpiryDays)
    {
        var sellable = batches.Where(b => IsSellable(b, today)).ToList();
        var primary = PrimaryBatch(batches, today);
        return new StockListItem
        {
            MedicineId = medicine.Id,
            Name = medicine.Name,
            Manufacturer = medicine.Manufacturer,
            Composition = medicine.Composition,
            Category = medicine.Category,
            Barcode = medicine.Barcode,
            Schedule = medicine.Schedule,
            PackLabel = medicine.PackLabel,
            UnitsPerPack = Math.Max(medicine.UnitsPerPack, 1),
            RackLocation = medicine.RackLocation,
            HsnCode = medicine.HsnCode,
            GstRatePercent = medicine.GstRatePercent,
            MinStock = medicine.MinStock,
            IsActive = medicine.IsActive,
            StockUnits = sellable.Sum(b => (long)b.QuantityRemaining),
            ExpiredUnits = batches.Where(b => b.IsActive && b.QuantityRemaining > 0 && IsExpired(b.ExpiryDate, today))
                .Sum(b => (long)b.QuantityRemaining),
            BatchCount = batches.Count(b => b.IsActive && b.QuantityRemaining > 0),
            MrpPaisa = primary is { MrpPaisa: > 0 } ? primary.MrpPaisa : medicine.DefaultMrpPaisa,
            NearestExpiry = sellable.Count > 0 ? sellable.Min(b => b.ExpiryDate) : null,
            StockValuePaisa = sellable.Sum(StockValuePaisa),
            MrpValuePaisa = sellable.Sum(MrpValuePaisa),
            HasNearExpiry = sellable.Any(b => IsNearExpiry(b.ExpiryDate, today, nearExpiryDays))
        };
    }

    /// <summary>Header of the item detail pane, priced from the <see cref="PrimaryBatch"/> (item defaults when there is none).</summary>
    public static StockItemHeader BuildHeader(Medicine medicine, IReadOnlyList<MedicineBatch> batches, DateTime today)
    {
        var primary = PrimaryBatch(batches, today);
        var upp = Math.Max(medicine.UnitsPerPack, 1);
        return new StockItemHeader
        {
            MedicineId = medicine.Id,
            Name = medicine.Name,
            Manufacturer = medicine.Manufacturer,
            Composition = medicine.Composition,
            Schedule = medicine.Schedule,
            PackDisplay = string.IsNullOrWhiteSpace(medicine.PackLabel) ? $"{upp}'s" : medicine.PackLabel!,
            RackLocation = medicine.RackLocation,
            Expiry = primary?.ExpiryDate,
            MrpPaisa = primary?.MrpPaisa ?? medicine.DefaultMrpPaisa,
            PurchaseRatePaisa = primary?.PurchaseRatePaisa ?? 0,
            NetRatePaisa = primary == null ? 0 : NetRatePaisa(primary),
            SaleRatePaisa = primary == null ? medicine.DefaultSellingPricePaisa : EffectiveSaleRatePaisa(primary),
            MarginPercent = primary == null ? 0m : MarginPercent(primary),
            GstRatePercent = primary?.GstRatePercent ?? medicine.GstRatePercent,
            HsnCode = medicine.HsnCode,
            StockUnits = batches.Where(b => IsSellable(b, today)).Sum(b => (long)b.QuantityRemaining),
            UnitsPerPack = upp,
            IsActive = medicine.IsActive
        };
    }

    // ───────────────────────── Stock list: search, filter, sort ─────────────────────────

    /// <summary>True when every whitespace-separated search term occurs in the name, composition, manufacturer, barcode or rack.</summary>
    public static bool MatchesSearch(StockListItem item, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var haystack = string.Join(" ", item.Name, item.Composition, item.Manufacturer, item.Barcode, item.RackLocation, item.Category);
        return search.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    public static bool MatchesFilter(StockListItem item, StockFilter filter) => filter switch
    {
        StockFilter.InStock => item.IsInStock,
        StockFilter.OutOfStock => !item.IsInStock,
        StockFilter.LowStock => item.IsLowStock,
        StockFilter.NearExpiry => item.HasNearExpiry,
        StockFilter.Expired => item.HasExpiredStock,
        _ => true
    };

    public static bool Matches(StockListItem item, StockListQuery query) =>
        (query.IncludeInactive || item.IsActive)
        && MatchesFilter(item, query.Filter)
        && (query.Schedule == null || item.Schedule == query.Schedule)
        && (string.IsNullOrWhiteSpace(query.Manufacturer)
            || string.Equals(item.Manufacturer?.Trim(), query.Manufacturer.Trim(), StringComparison.OrdinalIgnoreCase))
        && MatchesSearch(item, query.Search);

    public static IEnumerable<StockListItem> Sort(IEnumerable<StockListItem> items, StockSort sort) => sort switch
    {
        StockSort.NameDesc => items.OrderByDescending(i => i.Name, StringComparer.OrdinalIgnoreCase),
        StockSort.StockHighToLow => items.OrderByDescending(i => i.StockUnits).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        StockSort.StockLowToHigh => items.OrderBy(i => i.StockUnits).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        StockSort.ValueHighToLow => items.OrderByDescending(i => i.StockValuePaisa).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        StockSort.ExpirySoonest => items.OrderBy(i => i.NearestExpiry ?? DateTime.MaxValue).ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase),
        _ => items.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
    };

    public static StockSummary Summarize(IReadOnlyCollection<StockListItem> items) => new()
    {
        ItemCount = items.Count,
        InStockCount = items.Count(i => i.IsInStock),
        OutOfStockCount = items.Count(i => !i.IsInStock),
        LowStockCount = items.Count(i => i.IsLowStock),
        StockValuePaisa = items.Sum(i => i.StockValuePaisa),
        MrpValuePaisa = items.Sum(i => i.MrpValuePaisa)
    };

    /// <summary>Applies search, filters and sort; the summary covers the items shown.</summary>
    public static StockListResult Apply(IEnumerable<StockListItem> items, StockListQuery query)
    {
        var shown = Sort(items.Where(i => Matches(i, query)), query.Sort).ToList();
        return new StockListResult(shown, Summarize(shown));
    }

    // ───────────────────────── Timeline ─────────────────────────

    /// <summary>
    /// TIMELINE tab: movements with the item-level running balance after each one, newest first.
    /// The running balance accumulates signed quantities in chronological order (date, then id).
    /// </summary>
    public static IReadOnlyList<StockTimelineEntry> BuildTimeline(IEnumerable<StockMovement> movements)
    {
        long running = 0;
        var entries = new List<StockTimelineEntry>();
        foreach (var m in movements.OrderBy(m => m.MovementDate).ThenBy(m => m.Id))
        {
            running += m.QuantityIn - m.QuantityOut;
            entries.Add(new StockTimelineEntry
            {
                MovementId = m.Id,
                Date = m.MovementDate,
                MovementType = m.MovementType,
                BatchNumber = m.BatchNumber,
                QuantityIn = m.QuantityIn,
                QuantityOut = m.QuantityOut,
                BatchBalanceAfter = m.BalanceAfter,
                RunningBalance = running,
                Reference = m.Reference,
                Reason = m.Reason
            });
        }
        entries.Reverse();
        return entries;
    }

    // ───────────────────────── Stock adjustments ─────────────────────────

    /// <summary>Checks a request before posting; balances are checked again inside the posting transaction.</summary>
    public static string? ValidateAdjustmentRequest(StockAdjustmentRequest request, DateTime today)
    {
        if (!Enum.IsDefined(request.Reason)) return "Choose a reason for the adjustment.";
        if (request.Lines.Count == 0) return "Add at least one batch to adjust.";
        if (request.AdjustmentDate.Date > today.Date) return "The adjustment date cannot be in the future.";
        if (request.Reason == StockAdjustmentReason.Correction && string.IsNullOrWhiteSpace(request.Notes))
            return "Enter a note explaining the count correction.";
        if (request.Lines.GroupBy(l => l.BatchId).Any(g => g.Count() > 1))
            return "Each batch can appear only once in an adjustment.";

        var sign = request.Reason.RequiredSign();
        foreach (var line in request.Lines)
        {
            if (line.QuantityChange == 0) return "Quantity to adjust cannot be zero.";
            if (sign < 0 && line.QuantityChange > 0)
                return $"'{request.Reason.ToLabel()}' removes stock: the quantity must be negative.";
            if (sign > 0 && line.QuantityChange < 0)
                return $"'{request.Reason.ToLabel()}' adds stock: the quantity must be positive.";
        }
        return null;
    }

    /// <summary>Stock can never go below zero.</summary>
    public static string? ValidateAdjustmentBalance(string batchLabel, long balanceBefore, long quantityChange) =>
        balanceBefore + quantityChange < 0
            ? $"Batch {batchLabel} has only {balanceBefore} unit(s); cannot remove {-quantityChange}."
            : null;

    /// <summary>Signed value of an adjustment line at the batch cost per pack: sign × round(cost × |units| / pack).</summary>
    public static long AdjustmentValuePaisa(long quantityChange, long costPerPackPaisa, int unitsPerPack)
    {
        var value = PricingCalculator.AmountForUnits(costPerPackPaisa, Math.Abs(quantityChange), Math.Max(unitsPerPack, 1));
        return quantityChange < 0 ? -value : value;
    }

    // ───────────────────────── Opening batch ─────────────────────────

    public static string NormalizeBatchNumber(string? batchNumber) => (batchNumber ?? string.Empty).Trim().ToUpperInvariant();

    public static string? ValidateNewBatch(NewBatchRequest r, bool allowLooseSale)
    {
        var batchNo = NormalizeBatchNumber(r.BatchNumber);
        if (r.MedicineId <= 0) return "Choose the item for the new batch.";
        if (batchNo.Length == 0) return "Batch number is required.";
        if (batchNo.Length > MaxBatchNumberLength) return $"Batch number can be at most {MaxBatchNumberLength} characters.";
        if (r.ExpiryMonth is < 1 or > 12) return "Expiry month must be between 1 and 12.";
        var year = r.ExpiryYear < 100 ? r.ExpiryYear + 2000 : r.ExpiryYear;
        if (year is < 2000 or > 2099) return "Enter a valid expiry year (YY or YYYY).";
        if (r.UnitsPerPack < 1) return "Units per pack must be at least 1.";
        if (r.Packs < 0 || r.LooseUnits < 0) return "Quantity cannot be negative.";
        if (r.LooseUnits >= r.UnitsPerPack && r.UnitsPerPack > 1) return $"Loose units must be less than the pack size ({r.UnitsPerPack}).";
        if (r.UnitsPerPack == 1 && r.LooseUnits > 0) return "This pack has 1 unit: enter the quantity as packs.";
        if (!allowLooseSale && r.LooseUnits > 0) return "This item is sold in whole packs only: loose units must be 0.";
        if (r.Packs == 0 && r.LooseUnits == 0) return "Enter the opening quantity.";
        if (r.Packs * r.UnitsPerPack + r.LooseUnits > int.MaxValue) return "Quantity is too large.";
        if (r.MrpPaisa <= 0) return "MRP is required.";
        if (r.PurchaseRatePaisa < 0 || r.PtrPaisa < 0 || r.SaleRatePaisa < 0) return "Rates cannot be negative.";
        if (r.SaleRatePaisa > r.MrpPaisa) return "Sale rate cannot be more than the MRP.";
        if (r.PtrPaisa > r.MrpPaisa) return "PTR cannot be more than the MRP.";
        if (r.PurchaseRatePaisa > r.MrpPaisa) return "Purchase rate cannot be more than the MRP.";
        if (r.GstPercent is < 0 or > MaxGstPercent) return $"GST % must be between 0 and {MaxGstPercent:0}.";
        if (r.DiscountPercent is < 0 or > 100) return "Discount % must be between 0 and 100.";
        return null;
    }

    /// <summary>The batch row for a validated opening batch (expiry = last day of the month).</summary>
    public static MedicineBatch ToBatch(NewBatchRequest r)
    {
        var units = (int)new PackQuantity(r.Packs, r.LooseUnits).ToUnits(r.UnitsPerPack);
        return new MedicineBatch
        {
            MedicineId = r.MedicineId,
            BatchNumber = NormalizeBatchNumber(r.BatchNumber),
            ExpiryDate = MedicineBatch.ExpiryFromMonthYear(r.ExpiryMonth, r.ExpiryYear),
            PurchaseDate = r.EntryDate.Date,
            SupplierId = r.SupplierId,
            QuantityReceived = units,
            QuantityRemaining = units,
            UnitsPerPack = r.UnitsPerPack,
            PurchaseRatePaisa = r.PurchaseRatePaisa,
            PtrPaisa = r.PtrPaisa,
            SaleRatePaisa = r.SaleRatePaisa > 0 ? r.SaleRatePaisa : r.MrpPaisa,
            MrpPaisa = r.MrpPaisa,
            DiscountPercent = r.DiscountPercent,
            GstRatePercent = r.GstPercent,
            IsActive = true
        };
    }

    // ───────────────────────── Item settings and bulk update ─────────────────────────

    public static string? ValidateHsn(string? hsn)
    {
        if (string.IsNullOrWhiteSpace(hsn)) return null;
        var value = hsn.Trim();
        return value.All(char.IsAsciiDigit) && value.Length is 4 or 6 or 8 ? null : "HSN code must be 4, 6 or 8 digits.";
    }

    public static string? ValidateSettings(StockItemSettings s)
    {
        if (s.MedicineId <= 0) return "Choose an item first.";
        if (s.MinStock < 0 || s.MaxStock < 0) return "Min and max stock cannot be negative.";
        if (s.MaxStock > 0 && s.MaxStock < s.MinStock) return "Max stock must be at least the min stock.";
        if (s.MaxDiscountPercent is < 0 or > 100) return "Max discount % must be between 0 and 100.";
        if (s.MinDiscountPercent is < 0 or > 100) return "Min discount % must be between 0 and 100.";
        if (s.MaxDiscountPercent is { } max && s.MinDiscountPercent > max) return "Min discount % cannot be more than the max discount %.";
        if (!Enum.IsDefined(s.Schedule)) return "Choose a valid schedule.";
        if (s.RackLocation is { Length: > 30 }) return "Rack / location can be at most 30 characters.";
        return null;
    }

    public static string? ValidateBulkUpdate(StockBulkUpdate u)
    {
        if (u.MedicineIds.Count == 0) return "Select at least one item to update.";
        if (!u.HasChanges) return "Enter at least one value to set.";
        if (u.GstRatePercent is < 0 or > MaxGstPercent) return $"GST % must be between 0 and {MaxGstPercent:0}.";
        if (ValidateHsn(u.HsnCode) is { } hsnError) return hsnError;
        if (u.MinStock < 0 || u.MaxStock < 0) return "Min and max stock cannot be negative.";
        if (u.MinStock is { } min && u.MaxStock is { } max && max > 0 && max < min) return "Max stock must be at least the min stock.";
        if (u.MaxDiscountPercent is < 0 or > 100) return "Max discount % must be between 0 and 100.";
        if (u.Schedule is { } schedule && !Enum.IsDefined(schedule)) return "Choose a valid schedule.";
        if (u.RackLocation is { Length: > 30 }) return "Rack / location can be at most 30 characters.";
        return null;
    }
}
