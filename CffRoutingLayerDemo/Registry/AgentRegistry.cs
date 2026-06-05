// Registry/AgentRegistry.cs
namespace CffRoutingLayerDemo.Registry;

using CffRoutingLayerDemo.Agents;

public sealed class AgentRegistry
{
    private readonly Dictionary<string, IAgent> _agents;

    private AgentRegistry(Dictionary<string, IAgent> agents) => _agents = agents;

    public static AgentRegistry BuildDefault() =>
        new(new Dictionary<string, IAgent>(StringComparer.OrdinalIgnoreCase)
        {
            ["BookkeepingAgent"]    = new BookkeepingAgent(),
            ["InvoiceAgent"]        = new InvoiceAgent(),
            ["ReconciliationAgent"] = new ReconciliationAgent(),
            ["TaxAgent"]            = new TaxAgent(),
            ["ReportingAgent"]      = new ReportingAgent(),
            ["AuditAgent"]          = new AuditAgent(),
            ["ForecastAgent"]       = new ForecastAgent(),
        });

    public IAgent Resolve(string agentId)
    {
        if (_agents.TryGetValue(agentId, out var agent))
            return agent;
        throw new InvalidOperationException($"No agent registered for id '{agentId}'.");
    }

    public IReadOnlyDictionary<string, IAgent> All => _agents;
}
