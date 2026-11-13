using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.App.Modules.Billing.Printing;
using MedicalShopManager.App.Printing;
using MedicalShopManager.Core.Modules.Billing;
using MedicalShopManager.Core.Pricing;

namespace MedicalShopManager.App.Modules.Billing.ViewModels;

/// <summary>Save (Alt+S) → payment wizard → post; view mode actions (F2 show, F4 edit, Del delete, replicate, print).</summary>
public sealed partial class BillingViewModel
{
    private IReadOnlyList<SaleTender> _editTenders = Array.Empty<SaleTender>();
    private bool _editWasCredit;
    private DateTime? _editDueDate;

    // ───────────────────────── Save → payment ─────────────────────────

    /// <summary>Alt+S: validates the bill (spec 02 check_sale_payload + H1/X capture) and opens the payment wizard.</summary>
    [RelayCommand]
    private void Save()
    {
        if (!IsEditable || IsAnyDialogOpen || IsLoading) return;
        IsProductDropdownOpen = false;
        var request = BuildRequest();
        var errors = _billing.Validate(request, _settings);
        ShowValidation(errors);
        if (errors.Count > 0)
        {
            SetError(errors.Count == 1 ? errors[0] : $"Please fix {errors.Count} problems before saving (listed above the totals).");
            return;
        }

        BillComputation bill;
        try
        {
            bill = _billing.Compute(request, _settings);
        }
        catch (BillingValidationException ex)
        {
            ShowValidation(ex.Errors);
            SetError(ex.Message);
            return;
        }
        ClearError();

        var invoiceDate = request.InvoiceDate;
        var defaultDue = IsEditMode && _editDueDate is { } due
            ? due
            : BillingCalculator.DefaultDueDate(invoiceDate, Customer?.CreditDays is > 0 ? Customer.CreditDays : _settings.DefaultDueDays);
        Payment.Start(new BillingPaymentViewModel.Context(
            bill.Totals.NetPayablePaisa,
            Customer?.Name ?? "Cash Sale",
            DocumentNumber,
            invoiceDate,
            request.IsCounterSale,
            IsEditMode ? _editWasCredit : _settings.NewSaleIsCredit,
            defaultDue,
            IsEditMode ? _editTenders : Array.Empty<SaleTender>()));
        IsPaymentOpen = true;
    }

    private async Task PostAsync(BillingPaymentViewModel.Outcome outcome)
    {
        var request = BuildRequest() with
        {
            IsCredit = outcome.IsCredit,
            DueDate = outcome.DueDate,
            Tenders = outcome.Tenders,
            CashTenderedPaisa = outcome.CashTenderedPaisa
        };
        try
        {
            if (request.CustomChargePaisa > 0 && request.CustomChargeName is { } chargeName
                && !CustomChargeOptions.Any(c => string.Equals(c.Name, chargeName, StringComparison.OrdinalIgnoreCase)))
            {
                await _charges.EnsureAsync(chargeName);
            }

            CancelDraftSave();
            var wasEdit = IsEditMode;
            var posted = await _billing.SaveAsync(request);
            IsPaymentOpen = false;
            IsBillSaved = true;
            _draftId = null;

            var change = posted.ChangePaisa > 0 ? $"  Return change ₹{posted.ChangePaisa / 100m:N2}." : string.Empty;
            var due = posted.BalanceDuePaisa > 0 ? $"  Due ₹{posted.BalanceDuePaisa / 100m:N2}." : string.Empty;
            LastSavedMessage = $"{(wasEdit ? "Sale Successfully Updated" : "Sale Successfully Added")}: {posted.InvoiceNumber}, ₹{posted.NetPayablePaisa / 100m:N2}.{change}{due}";

            await LoadChargesAsync();
            await OpenSaleAsync(posted.SaleId);
            SetStatus(LastSavedMessage + "  F2 show invoice, Ctrl+P print, Alt+N new bill.");
            await PreviewInvoiceAsync();
        }
        catch (BillingValidationException ex)
        {
            Payment.ShowErrors(ex.Errors);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Saving the sale failed");
            Payment.ShowErrors(new[] { $"The sale could not be saved: {ex.Message}" });
        }
    }

    // ───────────────────────── View mode ─────────────────────────

    /// <summary>Shows a saved sale read-only (spec 02 §N); stored amounts are displayed, nothing is recomputed.</summary>
    private async Task OpenSaleAsync(long saleId)
    {
        var invoice = await _billing.GetInvoiceAsync(saleId);
        if (invoice is null)
        {
            await StartNewBillAsync();
            SetError($"Sale {saleId} was not found.");
            return;
        }

        var keepSaved = IsBillSaved;
        var savedMessage = LastSavedMessage;
        _suppressRecompute = true;
        try
        {
            ResetState(ScreenMode.View);
            _viewInvoice = invoice;
            _saleId = invoice.Id;
            DocumentNumber = invoice.InvoiceNumber;
            InvoiceDate = invoice.InvoiceDate.Date;
            IsCounterSale = invoice.IsCounterSale || invoice.CustomerId is not > 0;
            if (!IsCounterSale && invoice.CustomerId is { } customerId)
                Customer = await _customers.GetAsync(customerId) ?? new BillingCustomer { Id = customerId, Name = invoice.CustomerName ?? "Customer" };
            PatientName = invoice.PatientName;
            PatientAge = invoice.PatientAge;
            PatientGender = invoice.PatientGender;
            PatientAddress = invoice.PatientAddress;
            _doctorId = invoice.DoctorId;
            DoctorName = invoice.DoctorName;
            DoctorRegNo = invoice.DoctorRegNo;
            PrescriptionNumber = invoice.PrescriptionNumber;
            PrescriptionFilePath = invoice.PrescriptionFilePath;
            WithGst = invoice.WithGst;
            Notes = invoice.Notes;
            CustomChargeName = invoice.CustomChargeName;
            CustomChargeAmount = invoice.CustomChargePaisa / 100m;
            AppliedBillDiscountPaisa = invoice.Lines.Sum(l => l.BillDiscountPaisa);
            foreach (var saved in invoice.Lines)
            {
                var vm = AddLineViewModel(ToBillLine(saved));
                vm.ApplyStored(saved.LineTotalPaisa, saved.DiscountPercent);
            }
            IsInterState = invoice.IsInterState;
            Totals = new BillTotals
            {
                GrossPaisa = invoice.SubtotalPaisa,
                DiscountPaisa = invoice.DiscountPaisa,
                TaxablePaisa = invoice.TaxablePaisa,
                CgstPaisa = invoice.CgstPaisa,
                SgstPaisa = invoice.SgstPaisa,
                IgstPaisa = invoice.IgstPaisa,
                TaxPaisa = invoice.TaxPaisa,
                OtherChargesPaisa = invoice.CustomChargePaisa,
                RoundOffPaisa = invoice.RoundOffPaisa,
                NetPayablePaisa = invoice.GrandTotalPaisa,
                MrpValuePaisa = invoice.MrpTotalPaisa,
                SavingsPaisa = invoice.SavingsPaisa,
                ProfitPaisa = invoice.TaxablePaisa - invoice.Lines.Sum(l => l.CogsPaisa)
            };
            IsBillSaved = keepSaved;
            LastSavedMessage = savedMessage;
        }
        finally
        {
            _suppressRecompute = false;
        }
        OnModeChanged(Mode);
        RaiseTotals();
        if (invoice.Status == SaleStatus.Cancelled) SetStatus($"Invoice {invoice.InvoiceNumber} is deleted (stock restored, payments reversed).");
    }

    private static BillLine ToBillLine(BillingInvoiceLine saved) => new()
    {
        MedicineId = saved.MedicineId,
        MedicineName = saved.MedicineName,
        Manufacturer = saved.Manufacturer,
        Schedule = saved.Schedule,
        HsnCode = saved.HsnCode,
        RackLocation = saved.RackLocation,
        BatchId = saved.BatchId,
        BatchNumber = saved.BatchNumber,
        ExpiryDate = saved.ExpiryDate,
        UnitsPerPack = Math.Max(saved.UnitsPerPack, 1),
        MrpPerPackPaisa = saved.MrpPerPackPaisa,
        RatePerPackPaisa = saved.RatePerPackPaisa,
        DiscountPercent = saved.DiscountPercent,
        GstPercent = saved.GstPercent,
        Packs = saved.Packs,
        Loose = saved.Loose,
        FreeUnits = saved.FreeUnits,
        AvailableUnits = saved.Quantity
    };

    /// <summary>F4: switches a saved, active sale into edit mode.</summary>
    [RelayCommand]
    private async Task EditAsync()
    {
        if (!IsViewMode || _saleId is not { } saleId || IsAnyDialogOpen) return;
        if (IsCancelled)
        {
            SetError("A deleted invoice cannot be edited.");
            return;
        }
        try
        {
            var request = await _billing.LoadForEditAsync(saleId);
            if (request is null)
            {
                SetError("This sale can no longer be edited.");
                return;
            }
            var number = DocumentNumber;
            await PopulateAsync(request, ScreenMode.Edit);
            DocumentNumber = number;
            SetStatus("Editing. Alt+S saves the changes, Esc cancels.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Opening sale {SaleId} for edit failed", saleId);
            SetError($"The sale could not be opened for editing: {ex.Message}");
        }
    }

    /// <summary>Esc outside dialogs: closes the product dropdown, or leaves edit mode back to the saved sale.</summary>
    [RelayCommand]
    private async Task EscapeAsync()
    {
        if (IsDeleteConfirmOpen) { IsDeleteConfirmOpen = false; return; }
        if (IsBatchPickerOpen) { CloseBatchPicker(); return; }
        if (IsCustomerPickerOpen) { CustomerPicker.CloseCommand.Execute(null); return; }
        if (IsPaymentOpen) { Payment.BackCommand.Execute(null); return; }
        if (IsDraftsOpen) { IsDraftsOpen = false; return; }
        if (IsProductDropdownOpen) { IsProductDropdownOpen = false; return; }
        if (IsEditMode && _saleId is { } saleId)
        {
            await OpenSaleAsync(saleId);
            SetStatus("Edit cancelled.");
        }
    }

    /// <summary>Del in view mode: asks for confirmation (and an optional reason).</summary>
    [RelayCommand]
    private void Delete()
    {
        if (!IsViewMode || _saleId is null || IsAnyDialogOpen) return;
        if (IsCancelled)
        {
            SetError("This invoice is already deleted.");
            return;
        }
        DeleteReason = string.Empty;
        IsDeleteConfirmOpen = true;
    }

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (_saleId is not { } saleId) return;
        IsDeleteConfirmOpen = false;
        try
        {
            await _billing.DeleteAsync(saleId, _auth.CurrentUser?.Id, DeleteReason);
            await OpenSaleAsync(saleId);
            SetStatus("Sale Successfully Deleted. Stock was restored and its payments reversed.");
        }
        catch (BillingValidationException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Deleting sale {SaleId} failed", saleId);
            SetError($"The sale could not be deleted: {ex.Message}");
        }
    }

    [RelayCommand]
    private void CancelDelete() => IsDeleteConfirmOpen = false;

    /// <summary>Replicate Invoice: a new bill with the same items on currently available batches.</summary>
    [RelayCommand]
    private async Task ReplicateAsync()
    {
        if (_saleId is not { } saleId || IsAnyDialogOpen) return;
        await ReplicateFromAsync(saleId);
    }

    private async Task ReplicateFromAsync(long saleId)
    {
        try
        {
            var (request, warnings) = await _billing.ReplicateAsync(saleId);
            await PopulateAsync(request, ScreenMode.New);
            await PeekNumberAsync();
            SetStatus(warnings.Count == 0 ? "Invoice replicated." : "Invoice replicated: " + string.Join("; ", warnings));
        }
        catch (BillingValidationException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Replicating sale {SaleId} failed", saleId);
            SetError($"The invoice could not be replicated: {ex.Message}");
        }
    }

    // ───────────────────────── Printing ─────────────────────────

    /// <summary>F2 in view mode: print preview of the invoice (Tax Invoice or Bill of Supply).</summary>
    [RelayCommand]
    private Task ShowInvoiceAsync() => PreviewInvoiceAsync();

    private async Task PreviewInvoiceAsync()
    {
        var model = await BuildPrintModelAsync();
        if (model is null) return;
        try
        {
            await _print.PreviewAsync(_invoiceTemplate, model);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Invoice preview failed");
            SetError($"The invoice preview could not be shown: {ex.Message}");
        }
    }

    /// <summary>Ctrl+P: prints the saved invoice on the default paper size (Control Center ▸ Print Layout).</summary>
    [RelayCommand]
    private async Task PrintAsync()
    {
        var model = await BuildPrintModelAsync();
        if (model is null) return;
        try
        {
            if (await _print.PrintAsync(_invoiceTemplate, model)) SetStatus($"Invoice {model.Invoice.InvoiceNumber} sent to the printer.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing the invoice failed");
            SetError($"The invoice could not be printed: {ex.Message}");
        }
    }

    private async Task<SaleInvoicePrintModel?> BuildPrintModelAsync()
    {
        if (!IsViewMode || _saleId is not { } saleId)
        {
            SetError("Save the bill first (Alt+S); then F2 shows and Ctrl+P prints the invoice.");
            return null;
        }
        try
        {
            var invoice = await _billing.GetInvoiceAsync(saleId);
            if (invoice is null)
            {
                SetError("The sale was not found.");
                return null;
            }
            var printSettings = await _controlCenter.GetInvoicePrintSettingsAsync();
            var profile = await _controlCenter.GetBusinessProfileAsync();
            return new SaleInvoicePrintModel(invoice, printSettings, profile, _auth.CurrentUser?.DisplayName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Loading sale {SaleId} for printing failed", saleId);
            SetError($"The invoice could not be prepared for printing: {ex.Message}");
            return null;
        }
    }

    // ───────────────────────── Prescription ─────────────────────────

    /// <summary>Called by the view after the user picks a prescription file; the file is copied into the app data folder.</summary>
    public async Task AttachPrescriptionAsync(string sourcePath)
    {
        if (!IsEditable) return;
        try
        {
            PrescriptionFilePath = await _prescriptions.StoreAsync(sourcePath);
            SetStatus("Prescription attached.");
        }
        catch (BillingValidationException ex)
        {
            SetError(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Attaching prescription {Path} failed", sourcePath);
            SetError($"The prescription could not be attached: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RemovePrescription()
    {
        if (!IsEditable) return;
        PrescriptionFilePath = null;
    }
}
