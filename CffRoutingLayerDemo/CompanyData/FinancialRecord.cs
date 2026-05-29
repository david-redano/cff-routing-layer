// CompanyData/FinancialRecord.cs
namespace CffRoutingLayerDemo.CompanyData;

public enum RecordType { Income, Expense }

/// <summary>
/// A single financial transaction for a company.
/// The in-memory store uses these to answer plan step queries
/// (cash flow, P&L, burn rate, etc.) with real aggregated numbers.
/// </summary>
public record FinancialRecord(
    int      Id,
    string   CompanyId,
    DateOnly Date,
    decimal  Amount,
    string   Category,      // "Revenue" | "COGS" | "OpEx" | "Payroll" | "Software" | "Travel" | "Insurance" | "Equipment" | "Marketing"
    string   Counterparty,  // vendor or customer name
    RecordType Type,
    string   Description
);
