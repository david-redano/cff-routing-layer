// Cache/CacheHit.cs
namespace CffRoutingLayerDemo.Cache;

using CffRoutingLayerDemo.Core;
using CffRoutingLayerDemo.Plans;

public sealed record CacheHit(IntentResult Intent, ExecutionPlan Plan, double Score);
