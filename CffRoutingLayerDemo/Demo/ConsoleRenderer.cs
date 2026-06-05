// Demo/ConsoleRenderer.cs
namespace CffRoutingLayerDemo.Demo;

using Spectre.Console;

/// <summary>
/// Console rendering helpers for the demo REPL.
/// Uses Spectre.Console for banners and panels, plain Console for streaming output.
/// </summary>
public static class ConsoleRenderer
{
    public static void PrintBanner()
    {
        AnsiConsole.Write(new FigletText("CFF Routing Layer").Color(Color.Cyan1));
        AnsiConsole.Write(
            new Panel(
                "[grey]4-Layer Structural Plan Routing | LLM-Optional | Conversation History[/]\n" +
                "[grey]Type [bold]help[/] for commands, [bold]demo[/] to run preset scenarios, [bold]exit[/] to quit.[/]")
            .Header("[cyan1]CFF Financial Assistant Demo[/]")
            .Border(BoxBorder.Rounded)
            .Expand());
        AnsiConsole.WriteLine();
    }

    public static void PrintHelp()
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan1]Available Commands[/]")
            .AddColumn("[bold]Command[/]")
            .AddColumn("[bold]Description[/]")
            .AddRow("[yellow]demo[/]",     "Run preset scenarios and show metrics")
            .AddRow("[yellow]history[/]",  "Show conversation history (current session)")
            .AddRow("[yellow]compact[/]",  "Manually trigger conversation compaction")
            .AddRow("[yellow]clear[/]",    "Clear conversation history")
            .AddRow("[yellow]config[/]",   "Show current configuration")
            .AddRow("[yellow]stats[/]",    "Show session statistics")
            .AddRow("[yellow]help[/]",     "Show this help")
            .AddRow("[yellow]exit[/]",     "Quit the demo");

        AnsiConsole.Write(table);
    }

    public static void PrintResult(string result, bool fromCache)
    {
        var color  = fromCache ? "green" : "cyan1";
        var label  = fromCache ? "CACHED" : "RESULT";
        AnsiConsole.Write(
            new Panel($"[{color}]{Markup.Escape(result)}[/]")
            .Header($"[{color}]{label}[/]")
            .Border(BoxBorder.Rounded));
    }

    public static void PrintError(string message)
        => AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(message)}");

    public static void PrintConfig(
        bool useLlmQueryParser,
        bool useLlmRanker,
        bool useBedrockRag,
        string awsRegion,
        string llmModel)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan1]Active Configuration[/]")
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Value[/]")
            .AddRow("LLM Query Parser",    useLlmQueryParser ? "[green]enabled[/]" : "[grey]disabled (rule-based)[/]")
            .AddRow("LLM Ranker",          useLlmRanker      ? "[green]enabled[/]" : "[grey]disabled (feature-alignment)[/]")
            .AddRow("Bedrock RAG",         useBedrockRag     ? "[green]enabled[/]" : "[grey]disabled[/]")
            .AddRow("AWS Region",          Markup.Escape(awsRegion))
            .AddRow("LLM Model",           Markup.Escape(llmModel));

        AnsiConsole.Write(table);
    }

    public static void PrintStageHeader(string userMessage)
    {
        AnsiConsole.MarkupLine($"\n[bold cyan1]▶[/] [white]{Markup.Escape(userMessage)}[/]");
        AnsiConsole.MarkupLine("[grey]  Pipeline stages:[/]");
    }

    public static void PrintMetrics(TimeSpan elapsed, bool cached)
    {
        var label = cached ? "[green]CACHED[/]" : "[cyan1]FRESH[/]";
        AnsiConsole.MarkupLine($"  [grey]Completed in[/] [bold]{elapsed.TotalMilliseconds:F0} ms[/] {label}");
    }

    public static void PrintStats(SessionStats s)
    {
        if (s.TotalQueries == 0)
        {
            AnsiConsole.MarkupLine("[grey]  No queries yet.[/]");
            return;
        }

        // ── Routing summary ───────────────────────────────────────────────────
        var summary = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan1]Session Statistics[/]")
            .AddColumn("[bold]Metric[/]")
            .AddColumn(new TableColumn("[bold]Value[/]").RightAligned())
            .AddRow("Total queries",           $"{s.TotalQueries}")
            .AddRow("Cache hits",              $"[green]{s.CacheHits}[/]")
            .AddRow("Cache misses",            $"[cyan1]{s.CacheMisses}[/]")
            .AddRow("Cache hit rate",          $"[bold]{s.CacheHitRate:P0}[/]")
            .AddRow("Guardrail rejections",    $"{s.GuardrailRejections}")
            .AddRow("Streaming fallbacks",     $"{s.StreamingFallbacks}")
            .AddRow("", "")
            .AddRow("Avg latency (all)",       $"{s.AvgElapsedMs:F0} ms")
            .AddRow("Avg latency (cache hit)", $"{s.AvgCacheHitMs:F0} ms")
            .AddRow("Avg latency (miss/fresh)", $"{s.AvgCacheMissMs:F0} ms")
            .AddRow("Total executor time",     $"{s.TotalExecutorMs} ms")
            .AddRow("", "")
            .AddRow("LLM calls (total)",            $"[yellow]{s.TotalLlmCalls}[/]")
            .AddRow("  Phase 0 (intent)",            $"{s.LlmPhase0Calls}  ({(s.LlmPhase0Calls > 0 ? s.TotalPhase0Ms / s.LlmPhase0Calls : 0)} ms avg)")
            .AddRow("  Phase 2 (ranking)",           $"{s.LlmPhase2Calls}  ({(s.LlmPhase2Calls > 0 ? s.TotalPhase2Ms / s.LlmPhase2Calls : 0)} ms avg)")
            .AddRow("  RAG calls",                   $"{s.LlmRagCalls}")
            .AddRow("  Streaming calls",             $"{s.LlmStreamingCalls}")
            .AddRow("", "")
            .AddRow("Tokens — input",               $"{s.TotalInputTokens:N0}")
            .AddRow("Tokens — output",              $"{s.TotalOutputTokens:N0}")
            .AddRow("Tokens — total",               $"[bold]{s.TotalInputTokens + s.TotalOutputTokens:N0}[/]")
            .AddRow("", "")
            .AddRow("Est. cost (input)",            $"[grey]${s.EstimatedInputCostUsd:F5}[/]")
            .AddRow("Est. cost (output)",           $"[grey]${s.EstimatedOutputCostUsd:F5}[/]")
            .AddRow("Est. cost (total)",            $"[bold yellow]${s.EstimatedTotalCostUsd:F5}[/]")
            .AddRow("[grey]  model: Claude Haiku[/]",  "[grey]$0.25/1M in · $1.25/1M out[/]");

        AnsiConsole.Write(summary);

        // ── Intent distribution ───────────────────────────────────────────────
        if (s.IntentCounts.Count > 0)
        {
            var dist = new Table()
                .Border(TableBorder.Rounded)
                .Title("[cyan1]Intent Distribution[/]")
                .AddColumn("[bold]Intent[/]")
                .AddColumn(new TableColumn("[bold]Count[/]").RightAligned())
                .AddColumn("[bold]Share[/]");

            foreach (var (intent, count) in s.IntentCounts.OrderByDescending(kv => kv.Value))
            {
                var bar = new string('█', (int)(count * 20.0 / s.TotalQueries));
                dist.AddRow(Markup.Escape(intent), $"{count}", $"[cyan1]{bar}[/] {count * 100.0 / s.TotalQueries:F0}%");
            }

            AnsiConsole.Write(dist);
        }
    }
}
