// Index/PlanFeatureExtractor.cs
namespace CffRoutingLayerDemo.Index;

using CffRoutingLayerDemo.Plans;
using CffRoutingLayerDemo.Queries;

/// <summary>
/// Extracts a PlanFeatureVector from a PlanDefinition.
/// Run once at plan ingestion time (in PlanLoader), not at query time.
/// </summary>
public sealed class PlanFeatureExtractor
{
    public PlanFeatureVector Extract(PlanDefinition plan)
    {
        var actionNames = plan.Steps
            .Select(s => string.IsNullOrEmpty(s.ToolName) ? s.Action : s.ToolName)
            .Where(a => !string.IsNullOrEmpty(a))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allParamKeys = plan.Steps
            .SelectMany(s => s.Parameters.Keys)
            .Concat(plan.DefaultEntities.Keys)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var outputFields = plan.ExpectedData.Summary.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var domain    = InferDomain(plan);
        var subDomain = InferSubDomain(plan);

        return new PlanFeatureVector
        {
            Domain                 = domain,
            SubDomain              = subDomain,
            PrimaryAction          = InferPrimaryAction(plan),
            SecondaryActions       = InferSecondaryActions(plan),
            ToolNames              = actionNames,
            StepCount              = plan.Steps.Count,
            RequiresCrossReference = DetectCrossReference(plan),
            RequiredEntityTypes    = InferRequiredEntities(plan),
            OptionalEntityTypes    = new HashSet<string>(),
            TemporalScope          = InferTemporalScope(allParamKeys),
            SupportsDateRange      = allParamKeys.Any(k =>
                                         k.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                                         k.Contains("period", StringComparison.OrdinalIgnoreCase) ||
                                         k.Contains("start", StringComparison.OrdinalIgnoreCase)),
            SupportsPointInTime    = allParamKeys.Any(k =>
                                         k.Contains("asof", StringComparison.OrdinalIgnoreCase) ||
                                         k.Contains("pointintime", StringComparison.OrdinalIgnoreCase)),
            OutputFormat           = InferOutputFormat(plan),
            OutputFieldNames       = outputFields,
            Discriminators         = ExtractDiscriminators(plan)
        };
    }

    private static string InferDomain(PlanDefinition plan)
    {
        var capabilities = plan.Capabilities;
        var combined = (plan.Description + " " + plan.Understanding + " " +
                        string.Join(" ", capabilities)).ToLowerInvariant();

        if (combined.Contains("invoice") || combined.Contains("billing") ||
            combined.Contains("reconcil") || combined.Contains("tax") ||
            combined.Contains("cash") || combined.Contains("payroll") ||
            combined.Contains("ledger") || combined.Contains("p&l") ||
            combined.Contains("forecast") || combined.Contains("runway"))
            return "finance";

        if (combined.Contains("sales") || combined.Contains("order") || combined.Contains("customer"))
            return "sales";

        if (combined.Contains("inventory") || combined.Contains("stock"))
            return "inventory";

        if (combined.Contains("hr") || combined.Contains("employee"))
            return "hr";

        return "general";
    }

    private static string InferSubDomain(PlanDefinition plan)
    {
        var text = (plan.Intent + " " + plan.Description + " " + plan.Understanding).ToLowerInvariant();

        if (text.Contains("cashflow") || text.Contains("cash flow")) return "cashflow";
        if (text.Contains("invoice"))                                  return "invoicing";
        if (text.Contains("reconcil"))                                 return "reconciliation";
        if (text.Contains("tax"))                                      return "tax";
        if (text.Contains("forecast") || text.Contains("runway"))     return "forecasting";
        if (text.Contains("payroll"))                                  return "payroll";
        if (text.Contains("p&l") || text.Contains("profit") ||
            text.Contains("loss"))                                     return "reporting";
        if (text.Contains("order"))                                    return "orders";
        if (text.Contains("stock") || text.Contains("inventory"))     return "stock";

        return "unknown";
    }

    private static DomainAction InferPrimaryAction(PlanDefinition plan)
    {
        // Last step's action usually defines the plan's purpose
        var lastAction = plan.Steps
            .OrderByDescending(s => s.Id)
            .Select(s => (string.IsNullOrEmpty(s.ToolName) ? s.Action : s.ToolName).ToLowerInvariant())
            .FirstOrDefault() ?? "";

        if (lastAction.Contains("report") || lastAction.Contains("generate") || lastAction.Contains("compute"))
            return DomainAction.Compute;
        if (lastAction.Contains("list") || lastAction.Contains("fetch") || lastAction.Contains("get"))
            return DomainAction.List;
        if (lastAction.Contains("compare") || lastAction.Contains("reconcile") || lastAction.Contains("match"))
            return DomainAction.Compare;
        if (lastAction.Contains("forecast") || lastAction.Contains("predict") || lastAction.Contains("runway"))
            return DomainAction.Forecast;
        if (lastAction.Contains("audit") || lastAction.Contains("flag") || lastAction.Contains("detect"))
            return DomainAction.Audit;

        // Fall back to intent name
        var intent = plan.Intent.ToLowerInvariant();
        if (intent.Contains("generate") || intent.Contains("compute") || intent.Contains("calculate"))
            return DomainAction.Compute;
        if (intent.Contains("list") || intent.Contains("get") || intent.Contains("show"))
            return DomainAction.List;

        return DomainAction.Compute;
    }

    private static IReadOnlySet<DomainAction> InferSecondaryActions(PlanDefinition plan)
    {
        var result = new HashSet<DomainAction>();
        var allActions = plan.Steps
            .Select(s => (string.IsNullOrEmpty(s.ToolName) ? s.Action : s.ToolName).ToLowerInvariant());

        foreach (var action in allActions)
        {
            if (action.Contains("list") || action.Contains("fetch")) result.Add(DomainAction.List);
            if (action.Contains("compute") || action.Contains("calculate")) result.Add(DomainAction.Compute);
        }

        result.Remove(InferPrimaryAction(plan)); // Remove primary
        return result;
    }

    private static bool DetectCrossReference(PlanDefinition plan)
    {
        var fetchCount = plan.Steps.Count(s =>
        {
            var a = (string.IsNullOrEmpty(s.ToolName) ? s.Action : s.ToolName).ToLowerInvariant();
            return a.Contains("fetch") || a.Contains("list") || a.Contains("get");
        });

        var compareCount = plan.Steps.Count(s =>
        {
            var a = (string.IsNullOrEmpty(s.ToolName) ? s.Action : s.ToolName).ToLowerInvariant();
            return a.Contains("reconcile") || a.Contains("compare") || a.Contains("match") || a.Contains("cross");
        });

        return fetchCount >= 2 && compareCount >= 1;
    }

    private static IReadOnlySet<string> InferRequiredEntities(PlanDefinition plan)
    {
        // Entities without defaults are truly required
        var defaulted = plan.DefaultEntities.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return plan.Steps
            .SelectMany(s => s.Parameters.Keys)
            .Where(k => !defaulted.Contains(k))
            .Select(NormalizeEntityName)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string? NormalizeEntityName(string paramKey)
    {
        var lower = paramKey.ToLowerInvariant();
        if (lower.Contains("company") || lower.Contains("companyid")) return "companyId";
        if (lower.Contains("period"))   return "period";
        if (lower.Contains("start"))    return "startDate";
        if (lower.Contains("end"))      return "endDate";
        if (lower.Contains("invoice"))  return "invoiceId";
        if (lower.Contains("account"))  return "accountId";
        return null; // Skip unknown/implementation params
    }

    private static TemporalScopeType InferTemporalScope(IReadOnlySet<string> paramKeys)
    {
        var lower = paramKeys.Select(k => k.ToLowerInvariant()).ToList();
        if (lower.Any(k => k.Contains("start") || k.Contains("end") || k.Contains("period")))
            return TemporalScopeType.BoundedPeriod;
        if (lower.Any(k => k.Contains("asof") || k.Contains("pointintime")))
            return TemporalScopeType.PointInTime;
        return TemporalScopeType.None;
    }

    private static OutputFormat InferOutputFormat(PlanDefinition plan)
    {
        var text = (plan.Intent + " " + plan.Understanding).ToLowerInvariant();
        if (text.Contains("forecast") || text.Contains("runway") || text.Contains("predict"))
            return OutputFormat.Forecast;
        if (text.Contains("reconcil") || text.Contains("compare") || text.Contains("match"))
            return OutputFormat.Comparison;
        if (text.Contains("list") || text.Contains("detail") || text.Contains("breakdown"))
            return OutputFormat.DetailedList;
        return OutputFormat.Summary;
    }

    private static IReadOnlyDictionary<string, string> ExtractDiscriminators(PlanDefinition plan)
    {
        var d = new Dictionary<string, string>();

        var hasGroupBy = plan.Steps.Any(s =>
            s.Parameters.Keys.Any(k => k.Contains("GroupBy", StringComparison.OrdinalIgnoreCase)));
        if (hasGroupBy) d["aggregation"] = "grouped";

        if (DetectCrossReference(plan)) d["cross_reference"] = "true";

        var intent = plan.Intent.ToLowerInvariant();
        if (intent.Contains("forecast") || intent.Contains("runway"))
            d["includes_forecast"] = "true";

        return d;
    }
}
