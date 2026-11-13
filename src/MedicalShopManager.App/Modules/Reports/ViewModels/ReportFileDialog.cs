using System.Diagnostics;
using System.IO;

namespace MedicalShopManager.App.Modules.Reports.ViewModels;

/// <summary>File prompts for report exports (kept behind an interface so the ViewModel has no window code).</summary>
public interface IReportFileDialog
{
    /// <summary>Asks where to save an .xlsx file; null when the user cancels.</summary>
    string? AskExcelPath(string suggestedName);

    /// <summary>Opens a saved file with its default program (Excel).</summary>
    void Open(string path);
}

public sealed class ReportFileDialog : IReportFileDialog
{
    public string? AskExcelPath(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export report to Excel",
            Filter = "Excel workbook (*.xlsx)|*.xlsx",
            DefaultExt = ".xlsx",
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
        return string.IsNullOrEmpty(clean) ? "Report.xlsx" : clean + ".xlsx";
    }
}
