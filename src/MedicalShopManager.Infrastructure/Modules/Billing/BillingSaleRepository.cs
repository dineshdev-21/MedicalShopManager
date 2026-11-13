using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Pricing;
using MedicalShopManager.Infrastructure.Database;
using MedicalShopManager.Infrastructure.Numbering;

namespace MedicalShopManager.Infrastructure.Modules.Billing;

/// <summary>
/// Posts, edits, cancels and reads pack-based sales. Every write runs in one SQLite transaction: invoice number
/// (allocated inside it, so a rollback never consumes a number), header, lines, guarded stock movement per batch
/// (a batch can never go negative even with two tills), stock ledger rows and sale payment rows.
/// </summary>
public sealed class BillingSaleRepository : IBillingSaleRepository
{
    private readonly DatabaseManager _db;

    public BillingSaleRepository(DatabaseManager db) => _db = db;

    public async Task<(long SaleId, string InvoiceNumber)> InsertAsync(SalePosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        var request = posting.Request;
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var number = await DocumentSequenceAllocator.AllocateAsync(conn, tx, DocumentType.Sale, request.InvoiceDate);
            long saleId;
            using (var cmd = conn.Command(tx, $@"
                INSERT INTO Sales (InvoiceNumber, CreatedByUserId, CreatedAt, {HeaderColumns})
                VALUES (@InvoiceNumber, @UserId, @CreatedAt, {HeaderValues});
                SELECT last_insert_rowid();"))
            {
                cmd.Add("@InvoiceNumber", number);
                cmd.Add("@UserId", request.UserId);
                cmd.Add("@CreatedAt", DateTime.UtcNow.ToString("o"));
                AddHeader(cmd, posting);
                saleId = Convert.ToInt64(await cmd.ExecuteScalarAsync());
            }

            await WriteLinesAsync(conn, tx, saleId, number, posting);
            await InsertPaymentsAsync(conn, tx, saleId, number, posting);
            if (request.DraftId is > 0)
            {
                using var draft = conn.Command(tx, "DELETE FROM BillingSaleDrafts WHERE Id = @Id");
                draft.Add("@Id", request.DraftId.Value);
                await draft.ExecuteNonQueryAsync();
            }

            tx.Commit();
            return (saleId, number);
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task UpdateAsync(long saleId, SalePosting posting)
    {
        ArgumentNullException.ThrowIfNull(posting);
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var number = await RequireActiveAsync(conn, tx, saleId);
            await RestoreStockAsync(conn, tx, saleId, number, posting.Request.UserId, $"Edit of invoice {number}: previous lines reversed");

            using (var delete = conn.Command(tx, @"
                DELETE FROM SaleItems WHERE SaleId = @Id;
                DELETE FROM Payments WHERE TransactionId = @Id AND PaymentType = @SalePayment;"))
            {
                delete.Add("@Id", saleId);
                delete.Add("@SalePayment", (int)PaymentType.SalePayment);
                await delete.ExecuteNonQueryAsync();
            }

            using (var cmd = conn.Command(tx, $@"UPDATE Sales SET ({HeaderColumns}) = ({HeaderValues}) WHERE Id = @Id"))
            {
                AddHeader(cmd, posting);
                cmd.Add("@Id", saleId);
                await cmd.ExecuteNonQueryAsync();
            }

            await WriteLinesAsync(conn, tx, saleId, number, posting);
            await InsertPaymentsAsync(conn, tx, saleId, number, posting);
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task CancelAsync(long saleId, long? userId, string reason)
    {
        using var conn = _db.CreateNewConnection();
        using var tx = conn.BeginTransaction();
        try
        {
            var number = await RequireActiveAsync(conn, tx, saleId);
            await RestoreStockAsync(conn, tx, saleId, number, userId, $"Invoice {number} deleted: {reason}");
            using var cmd = conn.Command(tx, @"
                DELETE FROM Payments WHERE TransactionId = @Id AND PaymentType = @SalePayment;
                UPDATE Sales SET Status = @Cancelled, BalanceDuePaisa = 0, AmountPaidPaisa = 0, PaymentStatus = @Refunded,
                    CancelledAt = @Now, CancelReason = @Reason, ModifiedAt = @Now
                WHERE Id = @Id;");
            cmd.Add("@Id", saleId);
            cmd.Add("@SalePayment", (int)PaymentType.SalePayment);
            cmd.Add("@Cancelled", (int)SaleStatus.Cancelled);
            cmd.Add("@Refunded", (int)PaymentStatus.Refunded);
            cmd.AddDateTime("@Now", DateTime.Now);
            cmd.Add("@Reason", reason);
            await cmd.ExecuteNonQueryAsync();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public async Task<BillingInvoice?> GetInvoiceAsync(long saleId)
    {
        using var conn = _db.CreateNewConnection();
        BillingInvoice? invoice;
        using (var cmd = conn.Command(null, @"
            SELECT s.*, c.Address AS CustomerAddressValue, u.DisplayName AS CreatedByName
            FROM Sales s
            LEFT JOIN Customers c ON c.Id = s.CustomerId
            LEFT JOIN Users u ON u.Id = s.CreatedByUserId
            WHERE s.Id = @Id"))
        {
            cmd.Add("@Id", saleId);
            using var r = await cmd.ExecuteReaderAsync();
            invoice = await r.ReadAsync() ? MapHeader(r) : null;
        }
        if (invoice is null) return null;

        using (var cmd = conn.Command(null, "SELECT * FROM SaleItems WHERE SaleId = @Id ORDER BY Id"))
        {
            cmd.Add("@Id", saleId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) invoice.Lines.Add(MapLine(r));
        }

        using (var cmd = conn.Command(null, @"
            SELECT Id, PaymentDate, PaymentMethod, AmountPaisa, ReferenceNumber, Notes FROM Payments
            WHERE TransactionId = @Id AND PaymentType = @SalePayment ORDER BY PaymentDate, Id"))
        {
            cmd.Add("@Id", saleId);
            cmd.Add("@SalePayment", (int)PaymentType.SalePayment);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                invoice.Payments.Add(new BillingPaymentLine(r.Long("Id"), r.Date("PaymentDate"), (PaymentMethod)r.Int("PaymentMethod"),
                    r.Long("AmountPaisa"), r.Str("ReferenceNumber"), r.Str("Notes")));
        }
        return invoice;
    }

    public async Task<bool> HasReturnsAsync(long saleId)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "SELECT COUNT(*) FROM Returns WHERE ReturnType = @SalesReturn AND OriginalTransactionId = @Id");
        cmd.Add("@SalesReturn", (int)ReturnType.SalesReturn);
        cmd.Add("@Id", saleId);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync()) > 0;
    }

    public async Task<IReadOnlyList<H1RegisterEntry>> GetScheduleRegisterAsync(DateTime from, DateTime to)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT * FROM BillingScheduleRegister
            WHERE InvoiceDate >= @From AND InvoiceDate < @To
            ORDER BY InvoiceDate, SaleId, SaleItemId");
        cmd.AddDate("@From", from.Date);
        cmd.AddDate("@To", to.Date.AddDays(1));
        var list = new List<H1RegisterEntry>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new H1RegisterEntry
            {
                SaleId = r.Long("SaleId"),
                SaleItemId = r.Long("SaleItemId"),
                InvoiceNumber = r.Str("InvoiceNumber") ?? string.Empty,
                InvoiceDate = r.Date("InvoiceDate"),
                Schedule = (DrugSchedule)r.Int("Schedule"),
                MedicineName = r.Str("MedicineName") ?? string.Empty,
                Manufacturer = r.Str("Manufacturer"),
                BatchNumber = r.Str("BatchNumber") ?? string.Empty,
                ExpiryDate = r.Date("ExpiryDate"),
                Quantity = r.Int("Quantity"),
                PatientName = r.Str("PatientName"),
                PatientAddress = r.Str("PatientAddress"),
                DoctorName = r.Str("DoctorName"),
                DoctorRegNo = r.Str("DoctorRegNo"),
                PrescriptionNumber = r.Str("PrescriptionNumber"),
                CustomerName = r.Str("CustomerName")
            });
        }
        return list;
    }

    // ───────────────────────── Header ─────────────────────────

    private const string HeaderColumns = @"InvoiceDate, CustomerId, CustomerName, CustomerPhone, SubtotalPaisa, DiscountAmountPaisa,
        TaxableAmountPaisa, CgstAmountPaisa, SgstAmountPaisa, IgstAmountPaisa, GrandTotalPaisa, AmountPaidPaisa, BalanceDuePaisa,
        PaymentMethod, PaymentStatus, CashReceivedPaisa, ChangeReturnedPaisa, Notes, PrescriptionRef, Status, IsCounterSale,
        CustomerGstin, PatientName, PatientAge, PatientGender, PatientAddress, DoctorId, DoctorName, DoctorRegNo,
        PrescriptionNumber, PrescriptionFilePath, WithGst, IsInterState, CustomChargeName, CustomChargePaisa, RoundOffPaisa,
        MrpTotalPaisa, SavingsPaisa, DueDate, ModifiedAt";

    private const string HeaderValues = @"@InvoiceDate, @CustomerId, @CustomerName, @CustomerPhone, @SubtotalPaisa, @DiscountAmountPaisa,
        @TaxableAmountPaisa, @CgstAmountPaisa, @SgstAmountPaisa, @IgstAmountPaisa, @GrandTotalPaisa, @AmountPaidPaisa, @BalanceDuePaisa,
        @PaymentMethod, @PaymentStatus, @CashReceivedPaisa, @ChangeReturnedPaisa, @Notes, @PrescriptionRef, @Status, @IsCounterSale,
        @CustomerGstin, @PatientName, @PatientAge, @PatientGender, @PatientAddress, @DoctorId, @DoctorName, @DoctorRegNo,
        @PrescriptionNumber, @PrescriptionFilePath, @WithGst, @IsInterState, @CustomChargeName, @CustomChargePaisa, @RoundOffPaisa,
        @MrpTotalPaisa, @SavingsPaisa, @DueDate, @ModifiedAt";

    private static void AddHeader(SqliteCommand cmd, SalePosting posting)
    {
        var (request, bill, payment) = (posting.Request, posting.Bill, posting.Payment);
        var totals = bill.Totals;
        var counter = request.IsCounterSale || request.CustomerId is not > 0;
        cmd.AddDateTime("@InvoiceDate", request.InvoiceDate);
        cmd.Add("@CustomerId", counter ? null : request.CustomerId);
        cmd.Add("@CustomerName", Blank(request.CustomerName) ?? (counter ? "Cash / Counter Sale" : null));
        cmd.Add("@CustomerPhone", Blank(request.CustomerPhone));
        cmd.Add("@SubtotalPaisa", totals.GrossPaisa);
        cmd.Add("@DiscountAmountPaisa", totals.DiscountPaisa);
        cmd.Add("@TaxableAmountPaisa", totals.TaxablePaisa);
        cmd.Add("@CgstAmountPaisa", totals.CgstPaisa);
        cmd.Add("@SgstAmountPaisa", totals.SgstPaisa);
        cmd.Add("@IgstAmountPaisa", totals.IgstPaisa);
        cmd.Add("@GrandTotalPaisa", totals.NetPayablePaisa);
        cmd.Add("@AmountPaidPaisa", payment.AmountPaidPaisa);
        cmd.Add("@BalanceDuePaisa", payment.BalanceDuePaisa);
        cmd.Add("@PaymentMethod", (int)payment.Method);
        cmd.Add("@PaymentStatus", (int)payment.Status);
        cmd.Add("@CashReceivedPaisa", payment.CashTenderedPaisa);
        cmd.Add("@ChangeReturnedPaisa", payment.ChangePaisa);
        cmd.Add("@Notes", Blank(request.Notes));
        cmd.Add("@PrescriptionRef", Blank(request.PrescriptionNumber));
        cmd.Add("@Status", (int)SaleStatus.Active);
        cmd.Add("@IsCounterSale", counter ? 1 : 0);
        cmd.Add("@CustomerGstin", Blank(request.CustomerGstin));
        cmd.Add("@PatientName", Blank(request.PatientName));
        cmd.Add("@PatientAge", Blank(request.PatientAge));
        cmd.Add("@PatientGender", (int)request.PatientGender);
        cmd.Add("@PatientAddress", Blank(request.PatientAddress));
        cmd.Add("@DoctorId", request.DoctorId);
        cmd.Add("@DoctorName", Blank(request.DoctorName));
        cmd.Add("@DoctorRegNo", Blank(request.DoctorRegNo));
        cmd.Add("@PrescriptionNumber", Blank(request.PrescriptionNumber));
        cmd.Add("@PrescriptionFilePath", Blank(request.PrescriptionFilePath));
        cmd.Add("@WithGst", request.WithGst ? 1 : 0);
        cmd.Add("@IsInterState", posting.IsInterState ? 1 : 0);
        cmd.Add("@CustomChargeName", request.CustomChargePaisa > 0 ? Blank(request.CustomChargeName) : null);
        cmd.Add("@CustomChargePaisa", request.CustomChargePaisa);
        cmd.Add("@RoundOffPaisa", totals.RoundOffPaisa);
        cmd.Add("@MrpTotalPaisa", totals.MrpValuePaisa);
        cmd.Add("@SavingsPaisa", totals.SavingsPaisa);
        cmd.AddDate("@DueDate", payment.BalanceDuePaisa > 0 ? request.DueDate : null);
        cmd.AddDateTime("@ModifiedAt", DateTime.Now);
    }

    private static async Task<string> RequireActiveAsync(SqliteConnection conn, SqliteTransaction tx, long saleId)
    {
        using var cmd = conn.Command(tx, "SELECT InvoiceNumber, Status FROM Sales WHERE Id = @Id");
        cmd.Add("@Id", saleId);
        using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) throw new KeyNotFoundException($"Sale {saleId} was not found.");
        if (r.Int("Status", (int)SaleStatus.Active) != (int)SaleStatus.Active)
            throw new BillingValidationException($"Invoice {r.Str("InvoiceNumber")} is already deleted.");
        return r.Str("InvoiceNumber") ?? string.Empty;
    }

    // ───────────────────────── Lines and stock ─────────────────────────

    private static async Task WriteLinesAsync(SqliteConnection conn, SqliteTransaction tx, long saleId, string number, SalePosting posting)
    {
        foreach (var computed in posting.Bill.Lines)
        {
            var line = computed.Line;
            var amounts = computed.Amounts;
            var unitsPerPack = Math.Max(line.UnitsPerPack, 1);
            var stockUnits = line.StockUnits;
            if (stockUnits <= 0) throw new BillingValidationException($"Invalid quantity for {line.MedicineName}");

            using (var cmd = conn.Command(tx, @"
                INSERT INTO SaleItems (SaleId, MedicineId, MedicineName, BatchId, BatchNumber, ExpiryDate, HsnCode, Quantity,
                    UnitPricePaisa, DiscountPercent, DiscountAmountPaisa, TaxableValuePaisa, GstRatePercent, CgstAmountPaisa,
                    SgstAmountPaisa, IgstAmountPaisa, TaxAmountPaisa, CogsPaisa, LineTotalPaisa, UnitsPerPack, Packs, LooseUnits,
                    FreeUnits, MrpPerPackPaisa, RatePerPackPaisa, BillDiscountPaisa, Schedule, Manufacturer, RackLocation)
                VALUES (@SaleId, @MedicineId, @MedicineName, @BatchId, @BatchNumber, @ExpiryDate, @HsnCode, @Quantity,
                    @UnitPricePaisa, @DiscountPercent, @DiscountAmountPaisa, @TaxableValuePaisa, @GstRatePercent, @CgstAmountPaisa,
                    @SgstAmountPaisa, @IgstAmountPaisa, @TaxAmountPaisa, @CogsPaisa, @LineTotalPaisa, @UnitsPerPack, @Packs, @LooseUnits,
                    @FreeUnits, @MrpPerPackPaisa, @RatePerPackPaisa, @BillDiscountPaisa, @Schedule, @Manufacturer, @RackLocation);"))
            {
                cmd.Add("@SaleId", saleId);
                cmd.Add("@MedicineId", line.MedicineId);
                cmd.Add("@MedicineName", line.MedicineName);
                cmd.Add("@BatchId", line.BatchId);
                cmd.Add("@BatchNumber", line.BatchNumber);
                cmd.AddDate("@ExpiryDate", line.ExpiryDate);
                cmd.Add("@HsnCode", Blank(line.HsnCode));
                cmd.Add("@Quantity", checked((int)stockUnits));
                // Legacy per-unit column (pre-6.2 readers): the undiscounted per-pack rate ÷ pack, as v1 stored it
                // (its DiscountPercent applies on top). Display only; the per-pack columns are authoritative.
                cmd.Add("@UnitPricePaisa", PricingCalculator.RoundPaisa(PricingCalculator.UnitRatePaisa(BillingCalculator.BaseRate(line), unitsPerPack)));
                cmd.Add("@DiscountPercent", computed.EffectiveDiscountPercent);
                cmd.Add("@DiscountAmountPaisa", amounts.LineDiscountPaisa + amounts.BillDiscountPaisa);
                cmd.Add("@TaxableValuePaisa", amounts.TaxablePaisa);
                cmd.Add("@GstRatePercent", computed.TaxPercent);
                cmd.Add("@CgstAmountPaisa", amounts.CgstPaisa);
                cmd.Add("@SgstAmountPaisa", amounts.SgstPaisa);
                cmd.Add("@IgstAmountPaisa", amounts.IgstPaisa);
                cmd.Add("@TaxAmountPaisa", amounts.TaxPaisa);
                cmd.Add("@CogsPaisa", line.CostPerPackExGstPaisa > 0 ? PricingCalculator.AmountForUnits(line.CostPerPackExGstPaisa, stockUnits, unitsPerPack) : 0);
                cmd.Add("@LineTotalPaisa", amounts.TotalPaisa);
                cmd.Add("@UnitsPerPack", unitsPerPack);
                cmd.Add("@Packs", line.Packs);
                cmd.Add("@LooseUnits", line.Loose);
                cmd.Add("@FreeUnits", line.FreeUnits);
                cmd.Add("@MrpPerPackPaisa", line.MrpPerPackPaisa);
                cmd.Add("@RatePerPackPaisa", BillingCalculator.BaseRate(line));
                cmd.Add("@BillDiscountPaisa", amounts.BillDiscountPaisa);
                cmd.Add("@Schedule", (int)line.Schedule);
                cmd.Add("@Manufacturer", Blank(line.Manufacturer));
                cmd.Add("@RackLocation", Blank(line.RackLocation));
                await cmd.ExecuteNonQueryAsync();
            }

            var balance = await MoveStockAsync(conn, tx, line.BatchId, -stockUnits, posting.AllowNegativeStock)
                ?? throw new BillingValidationException(
                    $"Insufficient stock for {line.MedicineName} (Batch {line.BatchNumber}). Requested {BillingFormat.Quantity(stockUnits, unitsPerPack)}.");
            await InsertMovementAsync(conn, tx, posting.Request.InvoiceDate, line.MedicineId, line.MedicineName, line.BatchId,
                line.BatchNumber, MovementType.SaleDispatch, 0, stockUnits, balance, $"Sale:{saleId}", $"Sale Invoice {number}",
                posting.Request.UserId);
        }
    }

    /// <summary>Puts every line of the sale back into its batch, with a ledger row each.</summary>
    private static async Task RestoreStockAsync(SqliteConnection conn, SqliteTransaction tx, long saleId, string number, long? userId, string reason)
    {
        var lines = new List<(long MedicineId, string Name, long BatchId, string BatchNumber, long Units)>();
        using (var cmd = conn.Command(tx, "SELECT MedicineId, MedicineName, BatchId, BatchNumber, Quantity FROM SaleItems WHERE SaleId = @Id ORDER BY Id"))
        {
            cmd.Add("@Id", saleId);
            using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                lines.Add((r.Long("MedicineId"), r.Str("MedicineName") ?? string.Empty, r.Long("BatchId"), r.Str("BatchNumber") ?? string.Empty, r.Long("Quantity")));
        }

        foreach (var line in lines.Where(l => l.Units > 0))
        {
            var balance = await MoveStockAsync(conn, tx, line.BatchId, line.Units)
                ?? throw new InvalidOperationException($"Batch {line.BatchNumber} of {line.Name} no longer exists; the sale cannot be reversed.");
            await InsertMovementAsync(conn, tx, DateTime.Now, line.MedicineId, line.Name, line.BatchId, line.BatchNumber,
                MovementType.StockAdjustment, line.Units, 0, balance, $"Sale:{saleId}", reason, userId);
        }
    }

    /// <summary>
    /// Adds <paramref name="delta"/> units to a batch; a decrement only succeeds if enough remain, unless negative
    /// stock is allowed (Sale Settings). Null = refused or batch missing.
    /// </summary>
    private static async Task<long?> MoveStockAsync(SqliteConnection conn, SqliteTransaction tx, long batchId, long delta,
        bool allowNegative = false)
    {
        using var cmd = conn.Command(tx, @"
            UPDATE MedicineBatches SET QuantityRemaining = QuantityRemaining + @Delta, UpdatedAt = @Now
            WHERE Id = @BatchId AND (@AllowNegative = 1 OR QuantityRemaining + @Delta >= 0)
            RETURNING QuantityRemaining;");
        cmd.Add("@Delta", delta);
        cmd.Add("@AllowNegative", allowNegative ? 1 : 0);
        cmd.Add("@BatchId", batchId);
        cmd.Add("@Now", DateTime.UtcNow.ToString("o"));
        var result = await cmd.ExecuteScalarAsync();
        return result is null or DBNull ? null : Convert.ToInt64(result);
    }

    private static async Task InsertMovementAsync(SqliteConnection conn, SqliteTransaction tx, DateTime date, long medicineId,
        string medicineName, long batchId, string batchNumber, MovementType type, long quantityIn, long quantityOut,
        long balanceAfter, string reference, string reason, long? userId)
    {
        using var cmd = conn.Command(tx, @"
            INSERT INTO StockMovements (MovementDate, MedicineId, MedicineName, BatchId, BatchNumber, MovementType,
                QuantityIn, QuantityOut, BalanceAfter, Reference, Reason, CreatedByUserId, CreatedAt)
            VALUES (@Date, @MedicineId, @MedicineName, @BatchId, @BatchNumber, @Type,
                @In, @Out, @Balance, @Reference, @Reason, @UserId, @CreatedAt);");
        cmd.AddDateTime("@Date", date);
        cmd.Add("@MedicineId", medicineId);
        cmd.Add("@MedicineName", medicineName);
        cmd.Add("@BatchId", batchId);
        cmd.Add("@BatchNumber", batchNumber);
        cmd.Add("@Type", (int)type);
        cmd.Add("@In", quantityIn);
        cmd.Add("@Out", quantityOut);
        cmd.Add("@Balance", balanceAfter);
        cmd.Add("@Reference", reference);
        cmd.Add("@Reason", reason);
        cmd.Add("@UserId", userId);
        cmd.Add("@CreatedAt", DateTime.UtcNow.ToString("o"));
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task InsertPaymentsAsync(SqliteConnection conn, SqliteTransaction tx, long saleId, string number, SalePosting posting)
    {
        foreach (var tender in posting.Payment.Tenders.Where(t => t.AmountPaisa > 0))
        {
            var mode = tender.Mode == TenderMode.Cheque ? "Cheque" : tender.Mode.ToString();
            var chequeDate = tender.ChequeDate is { } d ? $" dated {d:dd-MM-yyyy}" : string.Empty;
            using var cmd = conn.Command(tx, @"
                INSERT INTO Payments (TransactionId, TransactionReference, PaymentDate, PaymentType, PaymentMethod, AmountPaisa,
                    ReferenceNumber, Notes, CreatedByUserId, CreatedAt)
                VALUES (@SaleId, @Invoice, @Date, @Type, @Method, @Amount, @Reference, @Notes, @UserId, @CreatedAt);");
            cmd.Add("@SaleId", saleId);
            cmd.Add("@Invoice", number);
            cmd.AddDateTime("@Date", posting.Request.InvoiceDate);
            cmd.Add("@Type", (int)PaymentType.SalePayment);
            cmd.Add("@Method", (int)BillingCalculator.ToPaymentMethod(tender.Mode));
            cmd.Add("@Amount", tender.AmountPaisa);
            cmd.Add("@Reference", Blank(tender.Reference) ?? number);
            cmd.Add("@Notes", $"Payment for Invoice {number} ({mode}{chequeDate})");
            cmd.Add("@UserId", posting.Request.UserId);
            cmd.Add("@CreatedAt", DateTime.UtcNow.ToString("o"));
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ───────────────────────── Mapping ─────────────────────────

    private static BillingInvoice MapHeader(SqliteDataReader r) => new()
    {
        Id = r.Long("Id"),
        InvoiceNumber = r.Str("InvoiceNumber") ?? string.Empty,
        InvoiceDate = r.Date("InvoiceDate"),
        Status = (SaleStatus)r.Int("Status", (int)SaleStatus.Active),
        IsCounterSale = r.Bool("IsCounterSale", true),
        CustomerId = r.LongOrNull("CustomerId"),
        CustomerName = r.Str("CustomerName"),
        CustomerPhone = r.Str("CustomerPhone"),
        CustomerAddress = r.Str("CustomerAddressValue"),
        CustomerGstin = r.Str("CustomerGstin"),
        PatientName = r.Str("PatientName"),
        PatientAge = r.Str("PatientAge"),
        PatientGender = (PersonGender)r.Int("PatientGender"),
        PatientAddress = r.Str("PatientAddress"),
        DoctorId = r.LongOrNull("DoctorId"),
        DoctorName = r.Str("DoctorName"),
        DoctorRegNo = r.Str("DoctorRegNo"),
        PrescriptionNumber = r.Str("PrescriptionNumber") ?? r.Str("PrescriptionRef"),
        PrescriptionFilePath = r.Str("PrescriptionFilePath"),
        WithGst = r.Bool("WithGst", true),
        IsInterState = r.Bool("IsInterState"),
        SubtotalPaisa = r.Long("SubtotalPaisa"),
        DiscountPaisa = r.Long("DiscountAmountPaisa"),
        TaxablePaisa = r.Long("TaxableAmountPaisa"),
        CgstPaisa = r.Long("CgstAmountPaisa"),
        SgstPaisa = r.Long("SgstAmountPaisa"),
        IgstPaisa = r.Long("IgstAmountPaisa"),
        CustomChargeName = r.Str("CustomChargeName"),
        CustomChargePaisa = r.Long("CustomChargePaisa"),
        RoundOffPaisa = r.Long("RoundOffPaisa"),
        GrandTotalPaisa = r.Long("GrandTotalPaisa"),
        MrpTotalPaisa = r.Long("MrpTotalPaisa"),
        SavingsPaisa = r.Long("SavingsPaisa"),
        AmountPaidPaisa = r.Long("AmountPaidPaisa"),
        BalanceDuePaisa = r.Long("BalanceDuePaisa"),
        CashReceivedPaisa = r.Long("CashReceivedPaisa"),
        ChangeReturnedPaisa = r.Long("ChangeReturnedPaisa"),
        DueDate = r.DateOrNull("DueDate"),
        PaymentMethod = (PaymentMethod)r.Int("PaymentMethod"),
        PaymentStatus = (PaymentStatus)r.Int("PaymentStatus"),
        Notes = r.Str("Notes"),
        CreatedByUserId = r.LongOrNull("CreatedByUserId"),
        CreatedByName = r.Str("CreatedByName"),
        CreatedAt = r.Date("CreatedAt")
    };

    private static BillingInvoiceLine MapLine(SqliteDataReader r) => new()
    {
        SaleItemId = r.Long("Id"),
        MedicineId = r.Long("MedicineId"),
        MedicineName = r.Str("MedicineName") ?? string.Empty,
        Manufacturer = r.Str("Manufacturer"),
        Schedule = (DrugSchedule)r.Int("Schedule"),
        RackLocation = r.Str("RackLocation"),
        HsnCode = r.Str("HsnCode"),
        BatchId = r.Long("BatchId"),
        BatchNumber = r.Str("BatchNumber") ?? string.Empty,
        ExpiryDate = r.Date("ExpiryDate"),
        UnitsPerPack = Math.Max(r.Int("UnitsPerPack", 1), 1),
        Packs = r.Long("Packs"),
        Loose = r.Long("LooseUnits"),
        FreeUnits = r.Long("FreeUnits"),
        Quantity = r.Int("Quantity"),
        MrpPerPackPaisa = r.Long("MrpPerPackPaisa"),
        RatePerPackPaisa = r.Long("RatePerPackPaisa"),
        DiscountPercent = r.Dec("DiscountPercent"),
        GstPercent = r.Dec("GstRatePercent"),
        DiscountPaisa = r.Long("DiscountAmountPaisa"),
        BillDiscountPaisa = r.Long("BillDiscountPaisa"),
        TaxablePaisa = r.Long("TaxableValuePaisa"),
        CgstPaisa = r.Long("CgstAmountPaisa"),
        SgstPaisa = r.Long("SgstAmountPaisa"),
        IgstPaisa = r.Long("IgstAmountPaisa"),
        TaxPaisa = r.Long("TaxAmountPaisa"),
        LineTotalPaisa = r.Long("LineTotalPaisa"),
        CogsPaisa = r.Long("CogsPaisa")
    };

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
