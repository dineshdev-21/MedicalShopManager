using System.Diagnostics;
using System.IO;
using System.Text;
using MedicalShopManager.Core.Enums;
using MedicalShopManager.Core.Modules.Finance;

namespace MedicalShopManager.App.Modules.Finance.ViewModels;

/// <summary>An option of a drop-down: value plus the text the user sees.</summary>
public sealed record FinanceOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Shared UI helpers of the Finance screens: error wording, option lists, rupee entry, date presets.</summary>
internal static class FinanceUi
{
    /// <summary>User-facing messages for an exception (validation messages as written; anything else names the action).</summary>
    public static IReadOnlyList<string> Messages(Exception ex, string action) => ex switch
    {
        FinanceValidationException v => v.Errors.Count > 0 ? v.Errors : new[] { v.Message },
        ArgumentException or InvalidOperationException => new[] { ex.Message },
        _ => new[] { $"Could not {action}: {ex.Message}" }
    };

    public static string Message(Exception ex, string action) => string.Join(Environment.NewLine, Messages(ex, action));

    /// <summary>Rupees typed on screen to paisa (rounded once, half away from zero).</summary>
    public static long Paisa(decimal rupees) => FinanceRules.ToPaisa(rupees);

    public static decimal Rupees(long paisa) => paisa / 100m;

    public static string Money(long paisa) => FinanceRules.Rupees(paisa);

    public static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public static DateTime MonthStart(DateTime day) => new(day.Year, day.Month, 1);

    /// <summary>1 April of the Indian financial year containing <paramref name="day"/>.</summary>
    public static DateTime FinancialYearStart(DateTime day) => new(day.Month >= 4 ? day.Year : day.Year - 1, 4, 1);

    public static IReadOnlyList<FinanceOption<FinanceAccountType>> AccountTypes { get; } =
        Enum.GetValues<FinanceAccountType>().Select(t => new FinanceOption<FinanceAccountType>(t, t.ToLabel())).ToList();

    /// <summary>Modes of a receipt / payment (cheque included).</summary>
    public static IReadOnlyList<FinanceOption<FinancePaymentMode>> VoucherModes { get; } =
        Enum.GetValues<FinancePaymentMode>().Select(m => new FinanceOption<FinancePaymentMode>(m, m.ToLabel())).ToList();

    /// <summary>Modes of an expense (a cheque expense is recorded as a bank transfer).</summary>
    public static IReadOnlyList<FinanceOption<FinancePaymentMode>> ExpenseModes { get; } =
        VoucherModes.Where(m => m.Value != FinancePaymentMode.Cheque).ToList();

    public static IReadOnlyList<FinanceOption<FinanceMovementKind>> MovementKinds { get; } =
        Enum.GetValues<FinanceMovementKind>().Select(k => new FinanceOption<FinanceMovementKind>(k, k.ToLabel())).ToList();

    public static IReadOnlyList<FinanceOption<decimal>> GstRates { get; } =
        FinanceRules.ExpenseGstRates.Select(r => new FinanceOption<decimal>(r, r == 0 ? "No GST" : $"{r:0.##} %")).ToList();

    public static IReadOnlyList<FinanceOption<bool?>> GstFilters { get; } = new[]
    {
        new FinanceOption<bool?>(null, "All expenses"),
        new FinanceOption<bool?>(true, "With GST (input tax)"),
        new FinanceOption<bool?>(false, "Without GST")
    };

    public static IReadOnlyList<FinanceOption<FinanceVoucherKind?>> VoucherKindFilters { get; } = new[]
    {
        new FinanceOption<FinanceVoucherKind?>(null, "Payment In and Out"),
        new FinanceOption<FinanceVoucherKind?>(FinanceVoucherKind.Receipt, "Payment In (receipts)"),
        new FinanceOption<FinanceVoucherKind?>(FinanceVoucherKind.Payment, "Payment Out (to suppliers)")
    };

    public static IReadOnlyList<FinanceOption<ChequeDirection?>> ChequeDirections { get; } = new[]
    {
        new FinanceOption<ChequeDirection?>(null, "Received and issued"),
        new FinanceOption<ChequeDirection?>(ChequeDirection.Received, "Received (pay in)"),
        new FinanceOption<ChequeDirection?>(ChequeDirection.Issued, "Issued (pay out)")
    };

    public static IReadOnlyList<FinanceOption<ChequeStatus?>> ChequeStatuses { get; } =
        new[] { new FinanceOption<ChequeStatus?>(null, "Any status") }
            .Concat(Enum.GetValues<ChequeStatus>().Select(s => new FinanceOption<ChequeStatus?>(s, s.ToLabel()))).ToList();

    public static IReadOnlyList<FinanceOption<PartyType>> LedgerPartyTypes { get; } = new[]
    {
        new FinanceOption<PartyType>(PartyType.Customer, "Customers (receivable)"),
        new FinanceOption<PartyType>(PartyType.Supplier, "Suppliers (payable)")
    };

    /// <summary>Methods that bills record and that book into an account (credit moves no money).</summary>
    public static IReadOnlyList<PaymentMethod> MappedMethods { get; } = new[]
    {
        PaymentMethod.Cash, PaymentMethod.UPI, PaymentMethod.Card, PaymentMethod.BankTransfer, PaymentMethod.Split, PaymentMethod.Other
    };
}

/// <summary>File prompts for CSV exports (kept behind an interface so the ViewModels have no window code).</summary>
public interface IFinanceFileDialog
{
    /// <summary>Asks where to save a .csv file; null when the user cancels.</summary>
    string? AskCsvPath(string suggestedName);

    /// <summary>Opens a saved file with its default program (Excel).</summary>
    void Open(string path);
}

public sealed class FinanceFileDialog : IFinanceFileDialog
{
    public string? AskCsvPath(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export to Excel (CSV)",
            Filter = "CSV file (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = SafeFileName(suggestedName),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void Open(string path)
    {
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return string.IsNullOrEmpty(clean) ? "Export.csv" : clean + ".csv";
    }

    /// <summary>Writes CSV as UTF-8 with BOM so Excel shows ₹ and Indian names correctly.</summary>
    public static Task WriteAsync(string path, string csv) => File.WriteAllTextAsync(path, csv, new UTF8Encoding(true));
}
