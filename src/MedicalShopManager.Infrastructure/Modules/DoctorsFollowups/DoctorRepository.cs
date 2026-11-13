using Microsoft.Data.Sqlite;
using MedicalShopManager.Core.Modules.DoctorsFollowups;
using MedicalShopManager.Infrastructure.Database;

namespace MedicalShopManager.Infrastructure.Modules.DoctorsFollowups;

/// <summary>
/// Doctors table, plus read-only doctor-wise sales over Billing's Sales / SaleItems (this module never writes them).
/// Bills count when active (Sales.Status = 1).
/// </summary>
public sealed class DoctorRepository : IDoctorRepository
{
    private const string SelectDoctor = @"
        SELECT d.*,
               (SELECT COUNT(*) FROM Sales s WHERE s.DoctorId = d.Id AND s.Status = 1) AS BillCount,
               (SELECT MAX(s.InvoiceDate) FROM Sales s WHERE s.DoctorId = d.Id AND s.Status = 1) AS LastBillDate
        FROM Doctors d";

    private readonly DatabaseManager _db;

    public DoctorRepository(DatabaseManager db) => _db = db;

    public async Task<IReadOnlyList<Doctor>> SearchAsync(string? text, bool includeInactive, int limit = 500)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectDoctor + @"
            WHERE (@All = 1 OR d.IsActive = 1)
              AND (@Search IS NULL OR d.Name LIKE @Search ESCAPE '\' OR d.RegistrationNo LIKE @Search ESCAPE '\'
                   OR d.Phone LIKE @Search ESCAPE '\' OR d.ClinicName LIKE @Search ESCAPE '\'
                   OR d.Specialization LIKE @Search ESCAPE '\' OR d.City LIKE @Search ESCAPE '\')
            ORDER BY d.IsActive DESC, d.Name COLLATE NOCASE, d.Id
            LIMIT @Limit");
        cmd.Add("@All", includeInactive ? 1 : 0);
        cmd.Add("@Search", DoctorsFollowupsSql.LikeOrNull(StripPrefixForSearch(text)));
        cmd.Add("@Limit", Math.Clamp(limit, 1, 5000));
        return await ReadDoctorsAsync(cmd);
    }

    public async Task<Doctor?> GetAsync(long id)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectDoctor + " WHERE d.Id = @Id");
        cmd.Add("@Id", id);
        return (await ReadDoctorsAsync(cmd)).FirstOrDefault();
    }

    public async Task<Doctor?> FindByRegistrationNoAsync(string registrationNo, long? excludeId = null)
    {
        if (string.IsNullOrWhiteSpace(registrationNo)) return null;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectDoctor + @"
            WHERE d.RegistrationNo = @Reg COLLATE NOCASE AND (@Exclude IS NULL OR d.Id <> @Exclude) LIMIT 1");
        cmd.Add("@Reg", registrationNo.Trim());
        cmd.Add("@Exclude", excludeId);
        return (await ReadDoctorsAsync(cmd)).FirstOrDefault();
    }

    public async Task<Doctor?> FindByNameAndPhoneAsync(string name, string? phone)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, SelectDoctor + @"
            WHERE d.Name = @Name COLLATE NOCASE AND COALESCE(d.Phone, '') = COALESCE(@Phone, '')
            ORDER BY d.IsActive DESC, d.Id LIMIT 1");
        cmd.Add("@Name", name.Trim());
        cmd.Add("@Phone", DoctorsFollowupsSql.Blank(phone));
        return (await ReadDoctorsAsync(cmd)).FirstOrDefault();
    }

    public async Task<long> InsertAsync(Doctor doctor)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            INSERT INTO Doctors (Name, RegistrationNo, Qualification, Specialization, ClinicName, Phone, Email, Address, City,
                CommissionPercent, CommissionOnProfit, Notes, IsActive, CreatedAt, UpdatedAt)
            VALUES (@Name, @Reg, @Qualification, @Specialization, @Clinic, @Phone, @Email, @Address, @City,
                @Commission, @OnProfit, @Notes, @IsActive, @Now, @Now);
            SELECT last_insert_rowid();");
        Bind(cmd, doctor);
        cmd.AddDateTime("@Now", DateTime.Now);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    public async Task<bool> UpdateAsync(Doctor doctor)
    {
        ArgumentNullException.ThrowIfNull(doctor);
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            UPDATE Doctors SET Name = @Name, RegistrationNo = @Reg, Qualification = @Qualification, Specialization = @Specialization,
                ClinicName = @Clinic, Phone = @Phone, Email = @Email, Address = @Address, City = @City,
                CommissionPercent = @Commission, CommissionOnProfit = @OnProfit, Notes = @Notes, IsActive = @IsActive, UpdatedAt = @Now
            WHERE Id = @Id");
        Bind(cmd, doctor);
        cmd.Add("@Id", doctor.Id);
        cmd.AddDateTime("@Now", DateTime.Now);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<bool> SetActiveAsync(long id, bool isActive)
    {
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, "UPDATE Doctors SET IsActive = @Active, UpdatedAt = @Now WHERE Id = @Id");
        cmd.Add("@Active", isActive ? 1 : 0);
        cmd.AddDateTime("@Now", DateTime.Now);
        cmd.Add("@Id", id);
        return await cmd.ExecuteNonQueryAsync() > 0;
    }

    public async Task<IReadOnlyList<DoctorSalesRow>> GetSalesSummaryAsync(DateTime from, DateTime to, bool includeUnlinked)
    {
        const string Measures = @"
               COUNT(s.Id) AS BillCount,
               COUNT(DISTINCT COALESCE(s.CustomerId, 0) || '|' || LOWER(TRIM(COALESCE(NULLIF(TRIM(s.PatientName), ''), s.CustomerName, '')))) AS PatientCount,
               COALESCE(SUM(s.GrandTotalPaisa), 0) AS SalesPaisa,
               COALESCE(SUM(s.TaxableAmountPaisa), 0) AS TaxablePaisa,
               COALESCE(SUM((SELECT COALESCE(SUM(si.CogsPaisa), 0) FROM SaleItems si WHERE si.SaleId = s.Id)), 0) AS CostPaisa";
        var sql = $@"
            SELECT d.Id AS DoctorId, d.Name AS DoctorName, d.RegistrationNo, d.Specialization, d.CommissionPercent, d.CommissionOnProfit,
                   {Measures}
            FROM Sales s JOIN Doctors d ON d.Id = s.DoctorId
            WHERE s.Status = 1 AND s.InvoiceDate >= @From AND s.InvoiceDate < @To
            GROUP BY d.Id";
        if (includeUnlinked)
        {
            sql += $@"
            UNION ALL
            SELECT NULL AS DoctorId, MAX(TRIM(s.DoctorName)) AS DoctorName, MAX(s.DoctorRegNo) AS RegistrationNo, NULL AS Specialization,
                   0 AS CommissionPercent, 0 AS CommissionOnProfit,
                   {Measures}
            FROM Sales s
            WHERE s.Status = 1 AND s.InvoiceDate >= @From AND s.InvoiceDate < @To
              AND TRIM(COALESCE(s.DoctorName, '')) <> ''
              AND (s.DoctorId IS NULL OR NOT EXISTS (SELECT 1 FROM Doctors d WHERE d.Id = s.DoctorId))
            GROUP BY LOWER(TRIM(s.DoctorName))";
        }

        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, sql);
        cmd.AddDate("@From", from.Date);
        cmd.AddDate("@To", to.Date.AddDays(1));
        var rows = new List<DoctorSalesRow>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            rows.Add(new DoctorSalesRow
            {
                DoctorId = r.LongOrNull("DoctorId"),
                DoctorName = r.Text("DoctorName"),
                RegistrationNo = r.Str("RegistrationNo"),
                Specialization = r.Str("Specialization"),
                CommissionPercent = r.Dec("CommissionPercent"),
                CommissionOnProfit = r.Bool("CommissionOnProfit"),
                BillCount = r.Int("BillCount"),
                PatientCount = r.Int("PatientCount"),
                SalesPaisa = r.Long("SalesPaisa"),
                TaxablePaisa = r.Long("TaxablePaisa"),
                CostPaisa = r.Long("CostPaisa")
            });
        }
        return rows;
    }

    public async Task<IReadOnlyList<DoctorSaleBill>> GetBillsAsync(long? doctorId, string? doctorName, DateTime from, DateTime to)
    {
        if (doctorId is null && string.IsNullOrWhiteSpace(doctorName)) return Array.Empty<DoctorSaleBill>();
        using var conn = _db.CreateNewConnection();
        using var cmd = conn.Command(null, @"
            SELECT s.Id, s.InvoiceNumber, s.InvoiceDate, s.CustomerName, s.PatientName, s.GrandTotalPaisa, s.TaxableAmountPaisa,
                   (SELECT COUNT(*) FROM SaleItems si WHERE si.SaleId = s.Id) AS ItemCount
            FROM Sales s
            WHERE s.Status = 1 AND s.InvoiceDate >= @From AND s.InvoiceDate < @To
              AND ((@DoctorId IS NOT NULL AND s.DoctorId = @DoctorId)
                   OR (@DoctorId IS NULL AND LOWER(TRIM(s.DoctorName)) = LOWER(TRIM(@Name))
                       AND (s.DoctorId IS NULL OR NOT EXISTS (SELECT 1 FROM Doctors d WHERE d.Id = s.DoctorId))))
            ORDER BY s.InvoiceDate DESC, s.Id DESC
            LIMIT 5000");
        cmd.AddDate("@From", from.Date);
        cmd.AddDate("@To", to.Date.AddDays(1));
        cmd.Add("@DoctorId", doctorId);
        cmd.Add("@Name", doctorName);
        var bills = new List<DoctorSaleBill>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            bills.Add(new DoctorSaleBill(r.Long("Id"), r.Text("InvoiceNumber"), r.Date("InvoiceDate"), r.Str("CustomerName"),
                r.Str("PatientName"), r.Int("ItemCount"), r.Long("GrandTotalPaisa"), r.Long("TaxableAmountPaisa")));
        }
        return bills;
    }

    /// <summary>"Dr. Sharma" finds "Sharma" (names are stored without the prefix).</summary>
    private static string? StripPrefixForSearch(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : DoctorRules.StripPrefix(text);

    private static void Bind(SqliteCommand cmd, Doctor d)
    {
        cmd.Add("@Name", d.Name);
        cmd.Add("@Reg", DoctorsFollowupsSql.Blank(d.RegistrationNo));
        cmd.Add("@Qualification", DoctorsFollowupsSql.Blank(d.Qualification));
        cmd.Add("@Specialization", DoctorsFollowupsSql.Blank(d.Specialization));
        cmd.Add("@Clinic", DoctorsFollowupsSql.Blank(d.ClinicName));
        cmd.Add("@Phone", DoctorsFollowupsSql.Blank(d.Phone));
        cmd.Add("@Email", DoctorsFollowupsSql.Blank(d.Email));
        cmd.Add("@Address", DoctorsFollowupsSql.Blank(d.Address));
        cmd.Add("@City", DoctorsFollowupsSql.Blank(d.City));
        cmd.AddDecimal("@Commission", d.CommissionPercent);
        cmd.Add("@OnProfit", d.CommissionOnProfit ? 1 : 0);
        cmd.Add("@Notes", DoctorsFollowupsSql.Blank(d.Notes));
        cmd.Add("@IsActive", d.IsActive ? 1 : 0);
    }

    private static async Task<IReadOnlyList<Doctor>> ReadDoctorsAsync(SqliteCommand cmd)
    {
        var list = new List<Doctor>();
        using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new Doctor
            {
                Id = r.Long("Id"),
                Name = r.Text("Name"),
                RegistrationNo = r.Str("RegistrationNo"),
                Qualification = r.Str("Qualification"),
                Specialization = r.Str("Specialization"),
                ClinicName = r.Str("ClinicName"),
                Phone = r.Str("Phone"),
                Email = r.Str("Email"),
                Address = r.Str("Address"),
                City = r.Str("City"),
                CommissionPercent = r.Dec("CommissionPercent"),
                CommissionOnProfit = r.Bool("CommissionOnProfit"),
                Notes = r.Str("Notes"),
                IsActive = r.Bool("IsActive", true),
                CreatedAt = r.Date("CreatedAt"),
                UpdatedAt = r.Date("UpdatedAt"),
                BillCount = r.Int("BillCount"),
                LastBillDate = r.DateOrNull("LastBillDate")
            });
        }
        return list;
    }
}
