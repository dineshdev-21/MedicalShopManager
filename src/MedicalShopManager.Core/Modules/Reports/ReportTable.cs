namespace MedicalShopManager.Core.Modules.Reports;

/// <summary>A column of a report sheet. <see cref="Total"/> columns are summed in the totals row (numeric kinds only).</summary>
public sealed record ReportColumn(string Header, ReportColumnKind Kind, bool Total = false, double Width = 1)
{
    public static ReportColumn Text(string header, double width = 1.5) => new(header, ReportColumnKind.Text, false, width);
    public static ReportColumn Date(string header) => new(header, ReportColumnKind.Date, false, 1);
    public static ReportColumn Money(string header, bool total = true) => new(header, ReportColumnKind.Money, total, 1.1);
    public static ReportColumn Qty(string header, bool total = true) => new(header, ReportColumnKind.Quantity, total, 0.8);
    public static ReportColumn Count(string header, bool total = true) => new(header, ReportColumnKind.Integer, total, 0.7);
    public static ReportColumn Percent(string header) => new(header, ReportColumnKind.Percent, false, 0.7);
}

/// <summary>
/// One row: values in column order. Types per column kind: Text → string, Date → DateTime, Integer / Quantity →
/// long, Money → long paisa, Percent → decimal. Null = empty cell.
/// </summary>
public sealed record ReportRow(IReadOnlyList<object?> Values, ReportRowStyle Style = ReportRowStyle.Normal)
{
    public static ReportRow Of(params object?[] values) => new(values);
    public static ReportRow Opening(params object?[] values) => new(values, ReportRowStyle.Opening);
    public static ReportRow Subtotal(params object?[] values) => new(values, ReportRowStyle.Subtotal);
}

/// <summary>
/// A sheet of a report (most reports have one; GSTR-1 has one per portal section). <see cref="PortalFormat"/> sheets
/// are exported exactly as the GST offline tool expects (header in row 1, no title, no totals row).
/// </summary>
public sealed record ReportTable(string Name, IReadOnlyList<ReportColumn> Columns, IReadOnlyList<ReportRow> Rows)
{
    public bool PortalFormat { get; init; }
    /// <summary>Show a totals row (viewer footer, print, Excel). Off for registers whose columns must not be added up.</summary>
    public bool ShowTotals { get; init; } = true;

    public bool HasRows => Rows.Count > 0;
    public bool HasTotals => ShowTotals && Columns.Any(c => c.Total && c.Kind.IsNumeric());

    /// <summary>Validates that every row has one value per column and values match the column kinds.</summary>
    public ReportTable EnsureValid()
    {
        foreach (var row in Rows)
        {
            if (row.Values.Count != Columns.Count)
                throw new InvalidOperationException($"Sheet '{Name}': a row has {row.Values.Count} values for {Columns.Count} columns.");
            for (var i = 0; i < Columns.Count; i++)
            {
                var v = row.Values[i];
                if (v == null) continue;
                var ok = Columns[i].Kind switch
                {
                    ReportColumnKind.Text => v is string,
                    ReportColumnKind.Date => v is DateTime,
                    ReportColumnKind.Integer or ReportColumnKind.Quantity or ReportColumnKind.Money => v is long,
                    ReportColumnKind.Percent => v is decimal,
                    _ => false
                };
                if (!ok)
                    throw new InvalidOperationException($"Sheet '{Name}', column '{Columns[i].Header}': value of type {v.GetType().Name} does not fit {Columns[i].Kind}.");
            }
        }
        return this;
    }

    /// <summary>Totals per column (null for non-total columns); subtotal and opening rows are not added.</summary>
    public IReadOnlyList<object?> Totals()
    {
        var totals = new object?[Columns.Count];
        for (var i = 0; i < Columns.Count; i++)
        {
            var column = Columns[i];
            if (!column.Total || !column.Kind.IsNumeric() || column.Kind == ReportColumnKind.Percent) continue;
            long sum = 0;
            foreach (var row in Rows)
            {
                if (row.Style == ReportRowStyle.Normal && row.Values[i] is long l) sum += l;
            }
            totals[i] = sum;
        }
        return totals;
    }

    public long Total(string header)
    {
        var index = IndexOf(header);
        return Totals()[index] as long? ?? 0;
    }

    public int IndexOf(string header)
    {
        for (var i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i].Header, header, StringComparison.OrdinalIgnoreCase)) return i;
        throw new ArgumentException($"Sheet '{Name}' has no column '{header}'.", nameof(header));
    }

    public object? Value(int row, string header) => Rows[row].Values[IndexOf(header)];

    /// <summary>Rows whose text (any column, as displayed) contains every word of <paramref name="text"/>.</summary>
    public ReportTable Filter(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return this;
        var terms = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var rows = Rows.Where(r =>
        {
            var line = string.Join(' ', r.Values.Select((v, i) => ReportFormat.Cell(v, Columns[i].Kind)));
            return terms.All(t => line.Contains(t, StringComparison.OrdinalIgnoreCase));
        }).ToList();
        return this with { Rows = rows };
    }
}

/// <summary>A headline figure shown above the grid (e.g. "Net tax payable").</summary>
public sealed record ReportStat(string Label, object Value, ReportColumnKind Kind = ReportColumnKind.Money)
{
    public string Display => ReportFormat.Cell(Value, Kind);
}

/// <summary>The output of one run.</summary>
public sealed record ReportResult
{
    public required ReportDefinition Definition { get; init; }
    public required ReportParameters Parameters { get; init; }
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
    public IReadOnlyList<ReportTable> Sheets { get; init; } = Array.Empty<ReportTable>();
    public IReadOnlyList<ReportStat> Summary { get; init; } = Array.Empty<ReportStat>();
    /// <summary>Warnings and assumptions to show with the report (e.g. "3 items have no HSN code").</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();

    public string Title => Definition.Name;
    public string PeriodLabel => Parameters.PeriodLabel(Definition);
    public bool IsEmpty => Sheets.All(s => !s.HasRows);
    public ReportTable Sheet(string name) => Sheets.First(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));
}
