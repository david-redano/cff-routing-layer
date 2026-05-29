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
                "[grey]7-Stage Intent Routing | Semantic Cache | LLM Rewriter | Conversation History[/]\n" +
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
            .AddRow("[yellow]demo[/]",     "Run 12 preset scenarios and show metrics")
            .AddRow("[yellow]history[/]",  "Show conversation history (current session)")
            .AddRow("[yellow]compact[/]",  "Manually trigger conversation compaction")
            .AddRow("[yellow]cache[/]",    "Show semantic cache contents")
            .AddRow("[yellow]clear[/]",    "Clear conversation history")
            .AddRow("[yellow]config[/]",   "Show current configuration")
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
        bool useLlmRewriter,
        bool useBedrockCache,
        bool useBedrockClassifier,
        string awsRegion,
        string rewriterModel,
        string llmModel,
        double cacheThreshold)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title("[cyan1]Active Configuration[/]")
            .AddColumn("[bold]Setting[/]")
            .AddColumn("[bold]Value[/]")
            .AddRow("LLM Rewriter",       useLlmRewriter      ? "[green]enabled[/]" : "[grey]disabled (regex)[/]")
            .AddRow("Bedrock Cache",       useBedrockCache     ? "[green]enabled[/]" : "[grey]disabled (local)[/]")
            .AddRow("Bedrock Classifier",  useBedrockClassifier ? "[green]enabled[/]" : "[grey]disabled (rule-based)[/]")
            .AddRow("AWS Region",          Markup.Escape(awsRegion))
            .AddRow("Rewriter Model",      Markup.Escape(rewriterModel))
            .AddRow("LLM Model",           Markup.Escape(llmModel))
            .AddRow("Cache Threshold",     $"{cacheThreshold:P0}");

        AnsiConsole.Write(table);
    }

    public static void PrintCacheEntries(IEnumerable<CffRoutingLayerDemo.Cache.CacheEntry> entries)
    {
        var list = entries.ToList();
        if (list.Count == 0)
        {
            AnsiConsole.MarkupLine("[grey]Semantic cache is empty.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[cyan1]Semantic Cache ({list.Count} entries)[/]")
            .AddColumn("[bold]#[/]")
            .AddColumn("[bold]Normalized Key[/]")
            .AddColumn("[bold]Intent[/]")
            .AddColumn("[bold]Agent[/]");

        foreach (var (e, i) in list.Select((e, i) => (e, i + 1)))
        {
            table.AddRow(
                i.ToString(),
                Markup.Escape(e.NormalizedText.Length > 55
                    ? e.NormalizedText[..52] + "…"
                    : e.NormalizedText),
                Markup.Escape(e.Intent.Intent),
                Markup.Escape(e.Intent.AgentId));
        }

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
}
