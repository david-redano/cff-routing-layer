// CompanyData/CompanyDataStore.cs
namespace CffRoutingLayerDemo.CompanyData;

using System.Text.RegularExpressions;

/// <summary>
/// In-memory financial data store seeded with realistic demo data for DEMO-001.
///
/// In production this layer would query a real ledger, accounting API, or data
/// warehouse. Here it fulfils the "Retrieve" step of the RAG pipeline:
///   Retrieve (CompanyDataStore) → Augment (prompt) → Generate (Claude).
/// </summary>
public sealed class CompanyDataStore
{
    // ── Current cash balance (separate from transaction history) ─────────────
    private static readonly Dictionary<string, decimal> _balances = new()
    {
        ["DEMO-001"] = 142_500.00m
    };

    // ── Seeded transaction history ────────────────────────────────────────────
    // 40 records spanning Jan–May 2026 for company DEMO-001.
    private static readonly IReadOnlyList<FinancialRecord> _allRecords =
    [
        // ── January 2026 ─────────────────────────────────────────────────────
        new(1,  "DEMO-001", new DateOnly(2026,1,1),  3_500.00m, "OpEx",     "Office Landlord",         RecordType.Expense, "Monthly rent"),
        new(2,  "DEMO-001", new DateOnly(2026,1,5),  5_000.00m, "Revenue",  "TechVentures LLC",        RecordType.Income,  "Jan consulting retainer"),
        new(3,  "DEMO-001", new DateOnly(2026,1,5),  18_000.00m,"Payroll",  "Payroll Run",             RecordType.Expense, "Jan payroll"),
        new(4,  "DEMO-001", new DateOnly(2026,1,15), 1_050.00m, "Software", "Amazon Web Services",     RecordType.Expense, "AWS bill Jan"),
        new(5,  "DEMO-001", new DateOnly(2026,1,18), 11_000.00m,"Revenue",  "Global Corp",             RecordType.Income,  "Software license Q1"),
        new(6,  "DEMO-001", new DateOnly(2026,1,22), 1_200.00m, "Insurance","InsuranceCo",             RecordType.Expense, "Annual business insurance"),

        // ── February 2026 ────────────────────────────────────────────────────
        new(7,  "DEMO-001", new DateOnly(2026,2,1),  3_500.00m, "OpEx",     "Office Landlord",         RecordType.Expense, "Monthly rent"),
        new(8,  "DEMO-001", new DateOnly(2026,2,5),  18_000.00m,"Payroll",  "Payroll Run",             RecordType.Expense, "Feb payroll"),
        new(9,  "DEMO-001", new DateOnly(2026,2,6),  7_500.00m, "Revenue",  "StartupCo",               RecordType.Income,  "Feb consulting"),
        new(10, "DEMO-001", new DateOnly(2026,2,12), 1_080.00m, "Software", "Amazon Web Services",     RecordType.Expense, "AWS bill Feb"),
        new(11, "DEMO-001", new DateOnly(2026,2,18), 280.00m,   "Software", "Software Vendor",         RecordType.Expense, "Licenses Feb"),
        new(12, "DEMO-001", new DateOnly(2026,2,20), 3_800.00m, "Revenue",  "Acme Industries",         RecordType.Income,  "Feb services"),

        // ── March 2026 ───────────────────────────────────────────────────────
        new(13, "DEMO-001", new DateOnly(2026,3,1),  3_500.00m, "OpEx",     "Office Landlord",         RecordType.Expense, "Monthly rent"),
        new(14, "DEMO-001", new DateOnly(2026,3,3),  5_000.00m, "Revenue",  "TechVentures LLC",        RecordType.Income,  "Mar consulting retainer"),
        new(15, "DEMO-001", new DateOnly(2026,3,5),  18_000.00m,"Payroll",  "Payroll Run",             RecordType.Expense, "Mar payroll"),
        new(16, "DEMO-001", new DateOnly(2026,3,10), 1_100.00m, "Software", "Amazon Web Services",     RecordType.Expense, "AWS bill Mar"),
        new(17, "DEMO-001", new DateOnly(2026,3,12), 6_200.00m, "Revenue",  "RetailCo",                RecordType.Income,  "Mar services"),
        new(18, "DEMO-001", new DateOnly(2026,3,15), 2_400.00m, "Marketing","Marketing Agency",        RecordType.Expense, "Q1 marketing campaign"),
        new(19, "DEMO-001", new DateOnly(2026,3,20), 750.00m,   "Travel",   "Business Travel",         RecordType.Expense, "Client site visits"),
        new(20, "DEMO-001", new DateOnly(2026,3,25), 8_400.00m, "Revenue",  "Global Corp",             RecordType.Income,  "Software license Q2 pre-payment"),

        // ── April 2026 ───────────────────────────────────────────────────────
        new(21, "DEMO-001", new DateOnly(2026,4,1),  3_500.00m, "OpEx",     "Office Landlord",         RecordType.Expense, "Monthly rent"),
        new(22, "DEMO-001", new DateOnly(2026,4,3),  5_000.00m, "Revenue",  "TechVentures LLC",        RecordType.Income,  "Apr consulting retainer"),
        new(23, "DEMO-001", new DateOnly(2026,4,5),  18_000.00m,"Payroll",  "Payroll Run",             RecordType.Expense, "Apr payroll"),
        new(24, "DEMO-001", new DateOnly(2026,4,7),  1_150.00m, "Software", "Amazon Web Services",     RecordType.Expense, "AWS bill Apr"),
        new(25, "DEMO-001", new DateOnly(2026,4,10), 320.00m,   "Software", "Software Vendor",         RecordType.Expense, "Licenses Apr"),
        new(26, "DEMO-001", new DateOnly(2026,4,11), 9_800.00m, "Revenue",  "Global Corp",             RecordType.Income,  "Software license Apr"),
        new(27, "DEMO-001", new DateOnly(2026,4,17), 620.00m,   "Travel",   "Business Travel",         RecordType.Expense, "Conference travel"),
        new(28, "DEMO-001", new DateOnly(2026,4,20), 4_100.00m, "Revenue",  "RetailCo",                RecordType.Income,  "Apr services"),
        new(29, "DEMO-001", new DateOnly(2026,4,22), 2_200.00m, "Equipment","EquipCo",                 RecordType.Expense, "Laptop replacement"),

        // ── May 2026 (through May 29) ─────────────────────────────────────────
        new(30, "DEMO-001", new DateOnly(2026,5,1),  3_500.00m, "OpEx",     "Office Landlord",         RecordType.Expense, "Monthly rent"),
        new(31, "DEMO-001", new DateOnly(2026,5,2),  5_000.00m, "Revenue",  "TechVentures LLC",        RecordType.Income,  "May consulting retainer"),
        new(32, "DEMO-001", new DateOnly(2026,5,5),  18_000.00m,"Payroll",  "Payroll Run",             RecordType.Expense, "May payroll"),
        new(33, "DEMO-001", new DateOnly(2026,5,8),  12_500.00m,"Revenue",  "Global Corp",             RecordType.Income,  "Software license May"),
        new(34, "DEMO-001", new DateOnly(2026,5,8),  1_200.00m, "Software", "Amazon Web Services",     RecordType.Expense, "AWS bill May"),
        new(35, "DEMO-001", new DateOnly(2026,5,10), 450.00m,   "Software", "Microsoft",               RecordType.Expense, "Microsoft 365"),
        new(36, "DEMO-001", new DateOnly(2026,5,14), 890.00m,   "Travel",   "Business Travel",         RecordType.Expense, "Client meetings"),
        new(37, "DEMO-001", new DateOnly(2026,5,15), 3_200.00m, "Revenue",  "Acme Industries",         RecordType.Income,  "May services"),
        new(38, "DEMO-001", new DateOnly(2026,5,20), 680.00m,   "Insurance","InsuranceCo",             RecordType.Expense, "Monthly insurance"),
        new(39, "DEMO-001", new DateOnly(2026,5,22), 8_750.00m, "Revenue",  "StartupCo",               RecordType.Income,  "May project delivery"),
        new(40, "DEMO-001", new DateOnly(2026,5,26), 340.00m,   "OpEx",     "City Utilities",          RecordType.Expense, "May utilities"),
    ];

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Current cash balance for the company (independent of transaction history).</summary>
    public decimal GetCurrentBalance(string companyId)
        => _balances.TryGetValue(companyId, out var b) ? b : 0m;

    /// <summary>
    /// Return all transactions for <paramref name="companyId"/> that fall
    /// within the period described by <paramref name="periodText"/>.
    /// </summary>
    public IReadOnlyList<FinancialRecord> GetTransactions(string companyId, string periodText)
    {
        var (start, end) = ParsePeriod(periodText);
        return _allRecords
            .Where(r => r.CompanyId == companyId && r.Date >= start && r.Date <= end)
            .OrderBy(r => r.Date)
            .ToList();
    }

    /// <summary>All records regardless of period (used for anomaly / YTD queries).</summary>
    public IReadOnlyList<FinancialRecord> GetAllTransactions(string companyId)
        => _allRecords.Where(r => r.CompanyId == companyId).OrderBy(r => r.Date).ToList();

    // ── Period parsing ────────────────────────────────────────────────────────

    /// <summary>
    /// Parse a human-readable period string into a (start, end) date range.
    /// Handles: "last month", "this month", "Q1 2026", "March", "FY2025",
    /// "last 30 days", "last 6 months", bare year "2024", etc.
    /// </summary>
    public static (DateOnly Start, DateOnly End) ParsePeriod(string periodText)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var lower = (periodText ?? "last month").Trim().ToLowerInvariant();

        // "last month" / "previous month"
        if (lower is "last month" or "previous month" or "last_month" or "prior month")
        {
            var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-1);
            return (first, first.AddMonths(1).AddDays(-1));
        }

        // "this month" / "current month"
        if (lower is "this month" or "current month" or "this_month")
            return (new DateOnly(today.Year, today.Month, 1), today);

        // "last 30 days"
        if (lower.StartsWith("last 30") || lower == "last_30_days")
            return (today.AddDays(-30), today);

        // "last 6 months"
        if (lower.StartsWith("last 6") || lower == "last_6_months")
            return (today.AddMonths(-6), today);

        // "last 12 months" / "last year"
        if (lower.StartsWith("last 12") || lower is "last year" or "trailing twelve months")
            return (today.AddMonths(-12), today);

        // "ytd" / "year to date"
        if (lower is "ytd" or "year to date" or "year-to-date")
            return (new DateOnly(today.Year, 1, 1), today);

        // "Q1 2026", "q2", "Q3" etc.
        var qMatch = Regex.Match(lower, @"q([1-4])\s*(\d{4})?");
        if (qMatch.Success)
        {
            int q    = int.Parse(qMatch.Groups[1].Value);
            int year = qMatch.Groups[2].Success ? int.Parse(qMatch.Groups[2].Value) : today.Year;
            int startMonth = (q - 1) * 3 + 1;
            var start = new DateOnly(year, startMonth, 1);
            var end   = ClampToToday(start.AddMonths(3).AddDays(-1), today);
            return (start, end);
        }

        // Month names: "March", "Mar", "march 2026"
        var months = System.Globalization.CultureInfo.InvariantCulture.DateTimeFormat;
        for (int m = 1; m <= 12; m++)
        {
            var full  = months.GetMonthName(m).ToLowerInvariant();
            var abbr  = months.GetAbbreviatedMonthName(m).ToLowerInvariant();
            if (lower.Contains(full) || lower.Contains(abbr))
            {
                var yearMatch = Regex.Match(lower, @"\d{4}");
                int year = yearMatch.Success ? int.Parse(yearMatch.Value) : today.Year;
                var first = new DateOnly(year, m, 1);
                return (first, ClampToToday(first.AddMonths(1).AddDays(-1), today));
            }
        }

        // "FY2024", "2024", "fiscal 2025"
        var yearOnly = Regex.Match(lower, @"\b(20\d{2})\b");
        if (yearOnly.Success)
        {
            int year = int.Parse(yearOnly.Groups[1].Value);
            return (new DateOnly(year, 1, 1), ClampToToday(new DateOnly(year, 12, 31), today));
        }

        // Default: last 30 days
        return (today.AddDays(-30), today);
    }

    private static DateOnly ClampToToday(DateOnly d, DateOnly today)
        => d > today ? today : d;
}
