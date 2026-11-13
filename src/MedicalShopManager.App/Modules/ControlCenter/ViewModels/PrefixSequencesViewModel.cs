using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MedicalShopManager.App.ViewModels;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Models;
using MedicalShopManager.Core.Modules.ControlCenter;
using MedicalShopManager.Core.Numbering;

namespace MedicalShopManager.App.Modules.ControlCenter.ViewModels;

/// <summary>One document type in the Prefix &amp; Sequences grid.</summary>
public sealed record SequenceRow(SequenceOverview Overview)
{
    public DocumentType DocumentType => Overview.Sequence.DocumentType;
    public string DocumentName => SequenceChangeRules.DisplayName(DocumentType);
    public string Prefix => Overview.Sequence.Prefix;
    public string Template => Overview.Sequence.Template;
    public int Padding => Overview.Sequence.Padding;
    public string NextNumberPreview => Overview.NextNumberPreview;
    public string LastIssued => Overview.LastIssuedPreview ?? "None yet";
    public string ResetLabel => Overview.Sequence.ResetEachFinancialYear ? "Every April" : "Never";
}

/// <summary>
/// Control Center ▸ Prefix &amp; Sequences (Localwell <c>business_get_prefix_sequence</c> /
/// <c>business_update_prefix_sequence {is_financial_year_reset, prefixes_sequences}</c>) over IDocumentNumberService.
/// The editor shows a live preview, the last issued number and refuses a next number that would repeat one.
/// Keys: Enter / F2 edit the selected row, Ctrl+S save, Esc cancel.
/// </summary>
public partial class PrefixSequencesViewModel : BaseViewModel
{
    private readonly ISequenceSettingsService _sequences;

    public PrefixSequencesViewModel(ISequenceSettingsService sequences) => _sequences = sequences;

    public ObservableCollection<SequenceRow> Rows { get; } = new();
    public IReadOnlyList<string> TemplatePresets => SequenceChangeRules.TemplatePresets;
    public IReadOnlyList<int> PaddingChoices { get; } = Enumerable.Range(1, 8).ToList();
    public FieldErrorMap Errors { get; } = new();

    public string FinancialYearLabel => $"Current financial year: {FinancialYear.For(DateTime.Today).Label}";

    [ObservableProperty] private SequenceRow? _selectedRow;
    [ObservableProperty] private bool _isEditorOpen;
    [ObservableProperty] private string _editTitle = string.Empty;
    [ObservableProperty] private string _editPrefix = string.Empty;
    [ObservableProperty] private string _editTemplate = DocumentNumberFormatter.DefaultTemplate;
    [ObservableProperty] private int _editPadding = 5;
    [ObservableProperty] private string _editNextNumber = "1";
    [ObservableProperty] private bool _editResetEachFinancialYear = true;
    [ObservableProperty] private string _livePreview = string.Empty;
    [ObservableProperty] private string _editLastIssued = string.Empty;

    private SequenceRow? _editing;

    public bool HasRows => Rows.Count > 0;

    public override async Task LoadAsync()
    {
        IsLoading = true;
        ClearStatus();
        try
        {
            var selected = SelectedRow?.DocumentType;
            Rows.Clear();
            foreach (var overview in await _sequences.GetOverviewAsync())
                Rows.Add(new SequenceRow(overview));
            SelectedRow = Rows.FirstOrDefault(r => r.DocumentType == selected) ?? Rows.FirstOrDefault();
        }
        catch (Exception ex)
        {
            SetError($"Could not load document numbering: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(HasRows));
        }
    }

    [RelayCommand]
    private void Edit(SequenceRow? row)
    {
        row ??= SelectedRow;
        if (row == null) return;
        _editing = row;
        var s = row.Overview.Sequence;
        EditTitle = $"Edit numbering: {row.DocumentName}";
        EditPrefix = s.Prefix;
        EditTemplate = s.Template;
        EditPadding = s.Padding;
        EditNextNumber = s.NextNumber.ToString(CultureInfo.InvariantCulture);
        EditResetEachFinancialYear = s.ResetEachFinancialYear;
        EditLastIssued = row.Overview.LastIssuedPreview is { } last
            ? $"Last issued: {last} (number {row.Overview.LastIssuedNumber})"
            : "No number issued yet in this period.";
        Errors.Clear();
        ClearStatus();
        UpdatePreview();
        IsEditorOpen = true;
    }

    [RelayCommand]
    private void CancelEdit()
    {
        IsEditorOpen = false;
        Errors.Clear();
        _editing = null;
    }

    partial void OnEditPrefixChanged(string value) => UpdatePreview();
    partial void OnEditTemplateChanged(string value) => UpdatePreview();
    partial void OnEditPaddingChanged(int value) => UpdatePreview();
    partial void OnEditNextNumberChanged(string value) => UpdatePreview();
    partial void OnEditResetEachFinancialYearChanged(bool value) => UpdatePreview();

    private DocumentSequence? BuildProposed(out string? parseError)
    {
        parseError = null;
        if (_editing == null) return null;
        if (!long.TryParse(EditNextNumber?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var next))
        {
            parseError = "Next number must be a whole number.";
            next = 0;
        }
        return new DocumentSequence
        {
            DocumentType = _editing.DocumentType,
            Prefix = EditPrefix?.Trim() ?? string.Empty,
            Template = EditTemplate?.Trim() ?? string.Empty,
            Padding = EditPadding,
            ResetEachFinancialYear = EditResetEachFinancialYear,
            NextNumber = next
        };
    }

    private void UpdatePreview()
    {
        var proposed = BuildProposed(out var parseError);
        if (proposed == null) return;
        var preview = parseError == null ? SequenceChangeRules.Preview(proposed, proposed.NextNumber, DateTime.Today) : null;
        var formatError = DocumentNumberFormatterSafe(proposed);
        LivePreview = preview != null && formatError == null
            ? $"Next {_editing!.DocumentName.ToLowerInvariant()} will be numbered:  {preview}"
            : $"Preview unavailable: {parseError ?? formatError ?? "invalid format"}";
    }

    private static string? DocumentNumberFormatterSafe(DocumentSequence s)
    {
        try
        {
            return DocumentNumberFormatter.Validate(s.Template, s.Prefix, s.Padding, s.ResetEachFinancialYear);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveEditAsync()
    {
        if (!IsEditorOpen || IsLoading) return;
        var proposed = BuildProposed(out var parseError);
        if (proposed == null) return;
        if (parseError != null)
        {
            Errors.Set(new[] { new SettingsError(nameof(DocumentSequence.NextNumber), parseError) });
            SetError(parseError);
            return;
        }

        IsLoading = true;
        ClearStatus();
        try
        {
            await _sequences.SaveAsync(proposed);
            var name = _editing!.DocumentName;
            IsEditorOpen = false;
            _editing = null;
            Errors.Clear();
            IsLoading = false;
            await LoadAsync();
            SetStatus($"{name} numbering saved.");
        }
        catch (ControlCenterValidationException ex)
        {
            Errors.Set(ex.Errors);
            SetError(FieldErrorMap.Summary(ex));
        }
        catch (Exception ex)
        {
            SetError($"Could not save numbering: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();
}
