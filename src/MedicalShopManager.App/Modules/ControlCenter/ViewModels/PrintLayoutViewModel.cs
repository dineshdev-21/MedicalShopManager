using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.Modules.ControlCenter.Printing;
using MedicalShopManager.App.Printing;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Interfaces;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>
/// Control Center ▸ Print Layout: default paper size per registered print template (key Print.PaperSize.&lt;name&gt;,
/// read by IPrintService) and the invoice print toggles (Localwell invoice_settings). Preview renders the real sale
/// invoice template with a sample bill on the chosen paper. Keys: Ctrl+S save, Ctrl+P preview the sale invoice.
/// </summary>
public partial class PrintLayoutViewModel : BaseViewModel
{
    private readonly IControlCenterSettingsService _settings;
    private readonly PrintTemplateCatalog _catalog;
    private readonly IPrintService _print;
    private readonly IDocumentNumberService _numbers;

    public PrintLayoutViewModel(IControlCenterSettingsService settings, PrintTemplateCatalog catalog, IPrintService print,
        IDocumentNumberService numbers)
    {
        _settings = settings;
        _catalog = catalog;
        _print = print;
        _numbers = numbers;
    }

    public ObservableCollection<TemplatePaperSizeItem> Templates { get; } = new();
    public ObservableCollection<InvoiceFieldGroupItem> FieldGroups { get; } = new();
    public FieldErrorMap Errors { get; } = new();

    [ObservableProperty] private string _termsText = string.Empty;
    [ObservableProperty] private string _footerNote = string.Empty;
    [ObservableProperty] private string _contactNumber = string.Empty;

    public bool HasTemplates => Templates.Count > 0;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        Errors.Clear();
        try
        {
            Templates.Clear();
            foreach (var entry in _catalog.GetTemplates())
            {
                var configured = await _settings.GetPaperSizeAsync(entry.Name);
                var size = configured is { } c && entry.SupportedPaperSizes.Contains(c) ? c : entry.DefaultPaperSize;
                Templates.Add(new TemplatePaperSizeItem(entry, size));
            }

            var invoice = await _settings.GetInvoicePrintSettingsAsync();
            FieldGroups.Clear();
            foreach (var group in InvoiceField.All.GroupBy(f => f.Group))
            {
                FieldGroups.Add(new InvoiceFieldGroupItem(GroupTitle(group.Key),
                    group.Select(f => new InvoiceFieldItem(f, invoice.IsEnabled(f.Key))).ToList()));
            }
            TermsText = invoice.TermsText;
            FooterNote = invoice.FooterNote;
            ContactNumber = invoice.ContactNumber;
        }
        catch (Exception ex)
        {
            SetError($"Could not load print settings: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasTemplates));
        }
    }

    private static string GroupTitle(InvoiceFieldGroup group) => group switch
    {
        InvoiceFieldGroup.Header => "Header",
        InvoiceFieldGroup.Party => "Patient / customer",
        InvoiceFieldGroup.Items => "Item columns",
        InvoiceFieldGroup.Totals => "Totals",
        InvoiceFieldGroup.Footer => "Footer",
        _ => group.ToString()
    };

    private InvoicePrintSettings BuildInvoiceSettings() => new()
    {
        Fields = FieldGroups.SelectMany(g => g.Items).ToDictionary(i => i.Key, i => i.IsEnabled),
        TermsText = TermsText ?? string.Empty,
        FooterNote = FooterNote ?? string.Empty,
        ContactNumber = ContactNumber ?? string.Empty
    };

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ClearStatus();
        try
        {
            await _settings.SaveInvoicePrintSettingsAsync(BuildInvoiceSettings());
            if (Templates.Count > 0)
                await _settings.SavePaperSizesAsync(Templates.ToDictionary(t => t.Name, t => t.SelectedSize));
            Errors.Clear();
            SetStatus("Print layout saved. The next print uses these settings.");
        }
        catch (ControlCenterValidationException ex)
        {
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Could not save print settings: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task DiscardChangesAsync() => LoadAsync();

    [RelayCommand]
    private void SetAllFields(string? state)
    {
        var enable = string.Equals(state, "on", StringComparison.OrdinalIgnoreCase);
        foreach (var item in FieldGroups.SelectMany(g => g.Items)) item.IsEnabled = enable;
    }

    [RelayCommand]
    private void ResetFieldsToDefaults()
    {
        foreach (var item in FieldGroups.SelectMany(g => g.Items)) item.IsEnabled = item.Definition.DefaultValue;
        TermsText = InvoicePrintSettings.DefaultTerms;
        FooterNote = new InvoicePrintSettings().FooterNote;
        ContactNumber = string.Empty;
    }

    /// <summary>Preview of a template on its selected paper; without a row, the sale invoice.</summary>
    [RelayCommand]
    private async Task PreviewAsync(TemplatePaperSizeItem? item)
    {
        item ??= Templates.FirstOrDefault(t => t.CanPreview);
        ClearStatus();
        if (item == null)
        {
            SetError("No sale invoice template is registered, so there is nothing to preview.");
            return;
        }
        if (!item.CanPreview || item.Entry.Template is not IDocumentTemplate<Sale> template)
        {
            SetError($"{item.DisplayName} can be previewed from its own screen (it needs a real document).");
            return;
        }

        try
        {
            var number = await _numbers.PeekNextAsync(DocumentType.Sale);
            var invoice = SampleInvoiceFactory.Create(DateTime.Now, number, (await _settings.GetSaleSettingsAsync()).RoundOffMode);
            await _print.PreviewAsync(template, invoice, new PrintOptions(PaperSize: item.SelectedSize, CopyLabel: "SAMPLE"));
        }
        catch (Exception ex)
        {
            SetError($"Preview failed: {ex.Message}");
        }
    }
}
