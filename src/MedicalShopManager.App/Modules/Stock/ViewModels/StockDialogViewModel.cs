using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.Core.Modules.Stock;

namespace MedicalShopManager.App.Modules.Stock.ViewModels;

/// <summary>
/// Base of the overlay dialogs hosted by the Stock workspace (add/edit item, add batch, item settings).
/// The host listens to <see cref="Saved"/> (with the affected medicine id) and shows the overlay while <see cref="IsOpen"/>.
/// </summary>
public abstract partial class StockDialogViewModel : ObservableObject
{
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _title = string.Empty;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Raised after a successful save with the medicine id that changed.</summary>
    public event EventHandler<long>? Saved;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    public void Close()
    {
        IsOpen = false;
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var medicineId = await SaveCoreAsync();
            IsOpen = false;
            Saved?.Invoke(this, medicineId);
        }
        catch (StockValidationException ex)
        {
            ErrorMessage = ex.Message;
        }
        catch (ArgumentException ex)
        {
            // Item-master validation (MedicineService.Validate) reports through ArgumentException.
            ErrorMessage = ex.Message.Split(" (Parameter")[0];
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not save: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Validates and saves; returns the medicine id to reselect. Throw <see cref="StockValidationException"/> for user errors.</summary>
    protected abstract Task<long> SaveCoreAsync();

    protected static long ToPaisa(decimal rupees) => (long)Math.Round(rupees * 100m, MidpointRounding.AwayFromZero);

    protected static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
