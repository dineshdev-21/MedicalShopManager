using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;

namespace MedicalShopManager.App.ViewModels;

/// <summary>
/// Medicine catalog management ViewModel with search, category filtering, and modal Add/Edit.
/// </summary>
public partial class MedicinesViewModel : BaseViewModel
{
    private readonly IMedicineService _medicineService;

    [ObservableProperty] private ObservableCollection<Medicine> _medicines = new();
    [ObservableProperty] private ObservableCollection<string> _categories = new();
    [ObservableProperty] private Medicine? _selectedMedicine;
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private string _selectedCategory = "All";

    // Add / Edit Modal State
    [ObservableProperty] private bool _isEditModalOpen;
    [ObservableProperty] private string _modalTitle = "Add New Medicine";
    [ObservableProperty] private long _editingId;
    [ObservableProperty] private string _editingName = string.Empty;
    [ObservableProperty] private string _editingComposition = string.Empty;
    [ObservableProperty] private string _editingManufacturer = string.Empty;
    [ObservableProperty] private string _editingBrand = string.Empty;
    [ObservableProperty] private string _editingCategory = "Tablets";
    [ObservableProperty] private DosageForm _editingDosageForm = DosageForm.Tablet;
    [ObservableProperty] private string _editingStrength = string.Empty;
    [ObservableProperty] private string _editingPackLabel = string.Empty;
    [ObservableProperty] private int _editingUnitsPerPack = 1;
    [ObservableProperty] private bool _editingAllowLooseSale = true;
    [ObservableProperty] private string _editingRackLocation = string.Empty;
    [ObservableProperty] private string _editingHsnCode = string.Empty;
    [ObservableProperty] private string _editingBarcode = string.Empty;
    [ObservableProperty] private decimal _editingGstRatePercent = 12;
    [ObservableProperty] private decimal _editingMrp;
    [ObservableProperty] private decimal _editingSellingPrice;
    [ObservableProperty] private decimal? _editingMaxDiscountPercent;
    [ObservableProperty] private int _editingMinStock = 10;
    [ObservableProperty] private int _editingMaxStock;
    [ObservableProperty] private DrugSchedule _editingSchedule = DrugSchedule.None;
    [ObservableProperty] private MedicineStatus _editingStatus = MedicineStatus.Active;

    /// <summary>Medicine being edited; supplies the fields the form does not show so an edit never resets them.</summary>
    private Medicine? _editingSource;

    public IReadOnlyList<DrugSchedule> Schedules { get; } = Enum.GetValues<DrugSchedule>();
    public IReadOnlyList<DosageForm> DosageForms { get; } = Enum.GetValues<DosageForm>();
    public IReadOnlyList<MedicineStatus> Statuses { get; } = Enum.GetValues<MedicineStatus>();

    public MedicinesViewModel(IMedicineService medicineService)
    {
        _medicineService = medicineService;
    }

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            var meds = await _medicineService.GetAllAsync();
            Medicines.Clear();
            foreach (var m in meds)
                Medicines.Add(m);

            var cats = await _medicineService.GetCategoriesAsync();
            Categories.Clear();
            Categories.Add("All");
            foreach (var c in cats)
                Categories.Add(c);
        }
        catch (Exception ex)
        {
            SetError($"Failed to load medicines: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        IsLoading = true;
        try
        {
            var meds = await _medicineService.SearchAsync(SearchQuery);
            Medicines.Clear();

            var filtered = SelectedCategory == "All"
                ? meds
                : meds.Where(m => string.Equals(m.Category, SelectedCategory, StringComparison.OrdinalIgnoreCase));

            foreach (var m in filtered)
                Medicines.Add(m);
        }
        catch (Exception ex)
        {
            SetError($"Search failed: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenAddModal()
    {
        _editingSource = null;
        ModalTitle = "Add New Medicine";
        LoadForm(new Medicine { Category = "Tablets", GstRatePercent = 12 });
        IsEditModalOpen = true;
    }

    [RelayCommand]
    private void OpenEditModal(Medicine? medicine)
    {
        if (medicine == null) return;

        _editingSource = medicine;
        ModalTitle = $"Edit {medicine.Name}";
        LoadForm(medicine);
        IsEditModalOpen = true;
    }

    private void LoadForm(Medicine medicine)
    {
        EditingId = medicine.Id;
        EditingName = medicine.Name;
        EditingComposition = medicine.Composition ?? string.Empty;
        EditingManufacturer = medicine.Manufacturer ?? string.Empty;
        EditingBrand = medicine.Brand ?? string.Empty;
        EditingCategory = medicine.Category ?? "Tablets";
        EditingDosageForm = medicine.DosageForm;
        EditingStrength = medicine.Strength ?? string.Empty;
        EditingPackLabel = medicine.PackLabel ?? string.Empty;
        EditingUnitsPerPack = medicine.UnitsPerPack;
        EditingAllowLooseSale = medicine.AllowLooseSale;
        EditingRackLocation = medicine.RackLocation ?? string.Empty;
        EditingHsnCode = medicine.HsnCode ?? string.Empty;
        EditingBarcode = medicine.Barcode ?? string.Empty;
        EditingGstRatePercent = medicine.GstRatePercent;
        EditingMrp = medicine.DefaultMrp;
        EditingSellingPrice = medicine.DefaultSellingPrice;
        EditingMaxDiscountPercent = medicine.MaxDiscountPercent;
        EditingMinStock = medicine.MinStock;
        EditingMaxStock = medicine.MaxStock;
        EditingSchedule = medicine.Schedule;
        EditingStatus = medicine.Status;
    }

    [RelayCommand]
    private void CloseEditModal()
    {
        IsEditModalOpen = false;
    }

    private static string? Clean(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static long ToPaisa(decimal rupees) => (long)Math.Round(rupees * 100m, MidpointRounding.AwayFromZero);

    [RelayCommand]
    private async Task SaveMedicineAsync()
    {
        IsLoading = true;
        try
        {
            var source = EditingId == 0 ? new Medicine() : _editingSource ?? new Medicine();
            var medicine = new Medicine
            {
                Id = EditingId,
                Name = EditingName.Trim(),
                Composition = Clean(EditingComposition),
                Manufacturer = Clean(EditingManufacturer),
                Brand = Clean(EditingBrand),
                Category = Clean(EditingCategory) ?? "General",
                DosageForm = EditingDosageForm,
                Strength = Clean(EditingStrength),
                PackLabel = Clean(EditingPackLabel),
                UnitsPerPack = EditingUnitsPerPack,
                AllowLooseSale = EditingAllowLooseSale,
                RackLocation = Clean(EditingRackLocation),
                HsnCode = Clean(EditingHsnCode),
                Barcode = Clean(EditingBarcode),
                GstRatePercent = EditingGstRatePercent,
                DefaultMrpPaisa = ToPaisa(EditingMrp),
                DefaultSellingPricePaisa = ToPaisa(EditingSellingPrice),
                MaxDiscountPercent = EditingMaxDiscountPercent,
                MinStock = EditingMinStock,
                MaxStock = EditingMaxStock,
                Schedule = EditingSchedule,
                Status = EditingStatus,

                // Not on the form: carried over from the edited record (defaults for a new one)
                Sku = source.Sku,
                Unit = source.Unit,
                DefaultSupplierId = source.DefaultSupplierId,
                Notes = source.Notes,
                CreatedAt = source.CreatedAt
            };

            if (EditingId == 0)
            {
                await _medicineService.CreateAsync(medicine);
                SetStatus($"Medicine '{medicine.Name}' created successfully.");
            }
            else
            {
                await _medicineService.UpdateAsync(medicine);
                SetStatus($"Medicine '{medicine.Name}' updated successfully.");
            }

            IsEditModalOpen = false;
            await LoadAsync();
        }
        catch (ArgumentException ex)
        {
            // Validation message from MedicineService.Validate
            SetError(ex.Message.Split(" (Parameter")[0]);
        }
        catch (Exception ex)
        {
            SetError($"Failed to save medicine: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DeactivateMedicineAsync(Medicine? medicine)
    {
        if (medicine == null) return;

        try
        {
            await _medicineService.DeactivateAsync(medicine.Id);
            SetStatus($"Medicine '{medicine.Name}' deactivated.");
            await LoadAsync();
        }
        catch (Exception ex)
        {
            SetError($"Failed to deactivate medicine: {ex.Message}");
        }
    }
}
