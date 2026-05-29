// Conversation/ConversationHistory.cs
namespace CffRoutingLayerDemo.Conversation;

using System.Text;
using System.Text.Json;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;

/// <summary>
/// Tracks all turns in the current REPL session and supports compaction:
/// when <see cref="NeedsCompaction"/> is true, call <see cref="CompactAsync"/>
/// to summarise the oldest turns via LLM and free context-window space.
///
/// Passed into <c>RoutingEngine.HandleAsync()</c> so the LLM intent rewriter
/// can resolve coreferences ("same account", "that customer") across turns.
/// </summary>
public sealed class ConversationHistory
{
    // ── Configuration ────────────────────────────────────────────────────────

    /// <summary>Turns kept verbatim in the active window after compaction.</summary>
    public const int MaxActiveTurns = 8;

    /// <summary>Total turn count that triggers compaction.</summary>
    public const int CompactionThreshold = 15;

    // ── State ────────────────────────────────────────────────────────────────

    private readonly List<ConversationTurn> _turns = [];
    private string? _summary;

    /// <summary>Active (non-compacted) turns, most recent last.</summary>
    public IReadOnlyList<ConversationTurn> Turns => _turns.AsReadOnly();

    /// <summary>LLM-generated summary of turns that were compacted out of the active window.</summary>
    public string? Summary => _summary;

    /// <summary>Total number of turns ever added (including compacted ones).</summary>
    public int TotalTurns { get; private set; }

    /// <summary>True when the active turn count has reached <see cref="CompactionThreshold"/>.</summary>
    public bool NeedsCompaction => _turns.Count >= CompactionThreshold;

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Append a completed turn to the history.</summary>
    public void AddTurn(ConversationTurn turn)
    {
        _turns.Add(turn);
        TotalTurns++;
    }

    /// <summary>
    /// Build a concise context string suitable for inclusion in LLM prompts.
    /// Returns the compaction summary (if any) followed by the last
    /// <paramref name="recentCount"/> turns in abbreviated form.
    /// </summary>
    public string BuildContext(int recentCount = 4)
    {
        if (TotalTurns == 0) return string.Empty;

        var sb = new StringBuilder();

        if (_summary is not null)
            sb.AppendLine($"[Earlier conversation summary]: {_summary}");

        var recent = _turns.TakeLast(recentCount).ToList();
        foreach (var t in recent)
        {
            sb.Append($"User: {t.NormalizedMessage}");
            if (t.ExtractedEntities.Count > 0)
            {
                var entities = string.Join(", ",
                    t.ExtractedEntities.Select(kv => $"{kv.Key}={kv.Value}"));
                sb.Append($"  [{entities}]");
            }
            sb.AppendLine($"  → {t.Intent}");
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Compact the oldest turns into a brief LLM-generated summary, then
    /// remove them from the active window. Should be called when
    /// <see cref="NeedsCompaction"/> is true.
    /// </summary>
    public async Task CompactAsync(
        AmazonBedrockRuntimeClient client,
        string modelId,
        CancellationToken ct = default)
    {
        if (_turns.Count <= MaxActiveTurns) return;

        int toCompactCount = _turns.Count - MaxActiveTurns;
        var toCompact = _turns.Take(toCompactCount).ToList();

        var turnBlock = string.Join("\n", toCompact.Select(t =>
        {
            var ents = t.ExtractedEntities.Count > 0
                ? $" [{string.Join(", ", t.ExtractedEntities.Select(kv => $"{kv.Key}={kv.Value}"))}]"
                : "";
            var resp = t.AssistantResponse.Length > 120
                ? t.AssistantResponse[..120] + "…"
                : t.AssistantResponse;
            return $"User: {t.UserMessage}{ents}\nAssistant ({t.Intent}): {resp}";
        }));

        var prompt = $"""
            Summarize the following financial assistant conversation history in 2–3 concise sentences.
            Preserve any specific values mentioned: account IDs, customer names, dollar amounts, periods, entity types.
            Do NOT add commentary — just a factual summary the assistant can use as context.

            History:
            {turnBlock}

            Summary:
            """;

        var request = new ConverseRequest
        {
            ModelId  = modelId,
            Messages =
            [
                new Message
                {
                    Role    = ConversationRole.User,
                    Content = [new ContentBlock { Text = prompt }]
                }
            ],
            InferenceConfig = new InferenceConfiguration
            {
                MaxTokens   = 200,
                Temperature = 0f
            }
        };

        var response    = await client.ConverseAsync(request, ct);
        var newSummary  = response.Output.Message.Content[0].Text.Trim();

        // Prepend existing summary if present
        _summary = _summary is not null
            ? $"{_summary} {newSummary}"
            : newSummary;

        // Remove the turns that were just summarised
        _turns.RemoveRange(0, toCompactCount);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[History] Compacted {toCompactCount} turns → summary ({_summary.Length} chars). " +
                          $"Active window: {_turns.Count} turns.");
        Console.ResetColor();
    }

    /// <summary>Clear all history and the compacted summary.</summary>
    public void Clear()
    {
        _turns.Clear();
        _summary    = null;
        TotalTurns  = 0;
    }

    /// <summary>
    /// Print the active history to the console (for the 'history' command).
    /// </summary>
    public void PrintToConsole()
    {
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine($"\nCONVERSATION HISTORY — {TotalTurns} total turn(s), {_turns.Count} active");
        Console.WriteLine(new string('─', 70));

        if (_summary is not null)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  [Summary of earlier turns]\n  {_summary}");
            Console.WriteLine(new string('─', 70));
        }

        if (_turns.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  (no active turns)");
        }
        else
        {
            foreach (var (t, i) in _turns.Select((t, i) => (t, i + 1)))
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"  [{i:D2}] {t.Timestamp:HH:mm:ss}  ");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($"{t.Intent,-30}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  {t.UserMessage[..Math.Min(60, t.UserMessage.Length)]}…");
                if (t.ExtractedEntities.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    var ents = string.Join(", ",
                        t.ExtractedEntities.Select(kv => $"{kv.Key}={kv.Value}"));
                    Console.WriteLine($"       entities: {ents}");
                }
            }
        }

        Console.ResetColor();
        Console.WriteLine();
    }
}
