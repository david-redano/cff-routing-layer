// Benchmarks/Program.cs
using BenchmarkDotNet.Running;
using CffRoutingLayerDemo.Benchmarks;

// Usage:
//   dotnet run -c Release --project CffRoutingLayerDemo.Benchmarks
// To run a specific benchmark:
//   dotnet run -c Release --project CffRoutingLayerDemo.Benchmarks -- --filter *Routing*

var summary = BenchmarkRunner.Run(
[
    typeof(RoutingPipelineBenchmarks),
    typeof(RewriterBenchmarks),
    typeof(CacheWarmerBenchmarks),
]);

