using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Purchases;

namespace MedicalShopManager.App.Modules.Purchases.ViewModels;

/// <summary>Save draft, confirm (Alt+S), edit / delete with stock reversal, payments and printing.</summary>
public sealed partial class PurchaseEntryViewModel
{
    private bool _allowDuplicateOnce;

    private long? UserId => _auth.CurrentUser?.Id;

    /// <summary>Saves without touching stock (spec: drafts can be completed later).</summary>
    [RelayCommand]
    private async Task SaveDraftAsync()
    {
        if (!CanSaveDraft || IsLoading) return;
        IsLoading = true;
        ClearErrors();
        try
        {
            var bill = BuildBill();
            bill.Status = PurchaseStatus.Draft;
            var id = await _entry.SaveDraftAsync(bill, UserId);
            await OpenAsync(id, edit: false);
            SetStatus($"Draft saved ({bill.Lines.Count} rows, ₹{bill.GrandTotalPaisa / 100m:N2}). Stock is added when you save it with Alt+S.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving purchase draft failed");
            ShowErrors(PurchaseUi.Messages(ex, "save the draft"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Save (Alt+S): a new / draft purchase is validated and posted to stock in one transaction (batches created or merged
    /// by item + batch + expiry), with the payment made now; an edited confirmed purchase is reversed and re-posted.
    /// </summary>
    [RelayCommand]
    private async Task SaveAsync()
    {
        if (!IsEditing || IsLoading) return;
        IsLoading = true;
        ClearErrors();
        var allowDuplicate = _allowDuplicateOnce;
        _allowDuplicateOnce = false;
        try
        {
            if (Editor.HasProduct)
            {
                ShowErrors(new[] { "The entry row still has an item. Press Add (Enter) to put it on the bill, or Esc to clear it." });
                return;
            }

            var bill = BuildBill();
            PurchasePostingResult result;
            if (Status == PurchaseStatus.Confirmed)
            {
                result = await _entry.UpdateConfirmedAsync(bill, UserId, allowDuplicate);
                SetStatus($"Purchase {result.PurchaseNumber} updated. Stock was reversed and re-posted.");
            }
            else
            {
                var payment = BuildInitialPayment(out var paymentError);
                if (paymentError != null)
                {
                    ShowErrors(new[] { paymentError });
                    return;
                }
                bill.Status = PurchaseStatus.Draft;
                result = await _entry.ConfirmAsync(bill, payment, UserId, allowDuplicate);
                SetStatus($"Purchase {result.PurchaseNumber} saved: {bill.TotalUnits} units added to stock"
                          + (payment is { AmountPaisa: > 0 } ? $", ₹{payment.AmountPaisa / 100m:N2} paid." : " on credit."));
            }

            var message = StatusMessage;
            await OpenAsync(result.PurchaseId, edit: false);
            if (message != null) SetStatus(message);
        }
        catch (DuplicatePurchaseInvoiceException duplicate)
        {
            DuplicateMessage = duplicate.Message + " Save it anyway?";
            IsDuplicateDialogOpen = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Saving purchase failed");
            ShowErrors(PurchaseUi.Messages(ex, "save the purchase"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    private PurchasePaymentInput? BuildInitialPayment(out string? error)
    {
        error = null;
        if (PaymentAmount <= 0) return null;
        if (PaymentMethod is null)
        {
            error = "Choose how the payment was made.";
            return null;
        }
        return new PurchasePaymentInput(PurchaseUi.Paisa(PaymentAmount), PaymentMethod.Value,
            string.IsNullOrWhiteSpace(PaymentReference) ? null : PaymentReference.Trim(), PaymentDate ?? DateTime.Today);
    }

    /// <summary>"This Bill is Duplicate" → the user chose to save anyway (spec: <c>overwrite_warning</c>).</summary>
    [RelayCommand]
    private async Task ConfirmDuplicateAsync()
    {
        IsDuplicateDialogOpen = false;
        _allowDuplicateOnce = true;
        await SaveAsync();
    }

    [RelayCommand]
    private void CancelDuplicate() => IsDuplicateDialogOpen = false;

    /// <summary>Pay the full balance now (fills the amount box).</summary>
    [RelayCommand]
    private void PayFull() => PaymentAmount = (IsConfirmed ? BalancePaisa : GrandTotalPaisa) / 100m;

    [RelayCommand]
    private void StartEdit()
    {
        if (!CanEdit) return;
        IsEditing = true;
        SetStatus("Editing: saving reverses this purchase's stock and posts the new rows. Stock already sold cannot be reversed.");
    }

    [RelayCommand]
    private async Task CancelEditAsync()
    {
        if (PurchaseId > 0) await OpenAsync(PurchaseId, edit: false);
        else await ResetAsync();
        ClearStatus();
        ClearErrors();
    }

    [RelayCommand]
    private void AskDelete()
    {
        if (!CanDelete) return;
        DeleteReason = string.Empty;
        IsDeleteDialogOpen = true;
    }

    [RelayCommand]
    private void CancelDelete() => IsDeleteDialogOpen = false;

    /// <summary>Deletes a draft, or cancels a confirmed purchase and takes its stock back out.</summary>
    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (!CanDelete) return;
        IsLoading = true;
        try
        {
            var wasDraft = Status == PurchaseStatus.Draft;
            var number = DocumentNumber;
            await _entry.DeleteAsync(PurchaseId, DeleteReason, UserId);
            IsDeleteDialogOpen = false;
            if (wasDraft)
            {
                await ResetAsync();
                SetStatus("Draft deleted.");
            }
            else
            {
                await OpenAsync(PurchaseId, edit: false);
                SetStatus($"Purchase {number} deleted; its stock was taken back out.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deleting purchase failed");
            IsDeleteDialogOpen = false;
            ShowErrors(PurchaseUi.Messages(ex, "delete the purchase"));
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenPaymentDialog()
    {
        if (!CanRecordPayment) return;
        PaymentAmount = BalancePaisa / 100m;
        PaymentReference = string.Empty;
        PaymentDate = DateTime.Today;
        PaymentMethod = PurchaseUi.PaymentMethods[0];
        IsPaymentDialogOpen = true;
    }

    [RelayCommand]
    private void ClosePaymentDialog() => IsPaymentDialogOpen = false;

    [RelayCommand]
    private async Task RecordPaymentAsync()
    {
        try
        {
            var payment = BuildInitialPayment(out var error);
            if (error != null || payment == null)
            {
                ShowErrors(new[] { error ?? "Enter the amount paid." });
                return;
            }
            await _entry.RecordPaymentAsync(PurchaseId, payment, UserId);
            IsPaymentDialogOpen = false;
            await OpenAsync(PurchaseId, edit: false);
            SetStatus($"Payment of ₹{payment.AmountPaisa / 100m:N2} recorded.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording purchase payment failed");
            ShowErrors(PurchaseUi.Messages(ex, "record the payment"));
        }
    }

    [RelayCommand]
    private async Task PrintAsync()
    {
        if (PurchaseId == 0)
        {
            SetError("Save the purchase before printing it.");
            return;
        }
        try
        {
            var bill = await _entry.GetAsync(PurchaseId) ?? throw new PurchaseRuleException("This purchase no longer exists.");
            await _print.PreviewAsync(_voucherTemplate, bill);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Printing purchase failed");
            SetError(PurchaseUi.Message(ex, "print the purchase"));
        }
    }

    /// <summary>Purchase return against this bill (Ctrl+Alt+R with the distributor and bill pre-selected).</summary>
    [RelayCommand]
    private void ReturnItems()
    {
        if (!IsConfirmed || SelectedDistributor is null) return;
        _navigation.NavigateTo(PurchasePages.Return, new PurchaseReturnRequest(SelectedDistributor.Id, PurchaseId));
    }
}
