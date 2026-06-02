// Bedrock/BedrockDisambiguator.cs
namespace CffRoutingLayerDemo.Bedrock;

using CffRoutingLayerDemo.Classification;
using CffRoutingLayerDemo.Config;

/// <summary>
/// LLM-backed disambiguator (Claude Haiku, max_tokens=20).
/// Presents the user query and each candidate's <c>Understanding</c> text to
/// the model and expects back the exact intent name or the word "none".
/// </summary>
public sealed class BedrockDisambiguator : IDisambiguator
{
    private readonly BedrockLlmHelper _llm;

    private const string SystemPrompt = """
        You are an intent selector for a business operations assistant covering
        sales, invoicing, accounting, finance, and financial reporting.
        Given a user message and a numbered list of candidate intents (each with a description
        of what that intent does), decide which candidate best matches what the user is asking.

        Rules:
        1. Select the candidate whose description is semantically equivalent to, or a
           reasonable interpretation of, the user message — even if the wording differs.
           Example: "which period had better sales" → candidate "Compare total sales order amounts
           for a given period" is a valid match.
        2. Respond: none ONLY when no candidate plausibly addresses what the user is asking
           (e.g. "write me a poem" when all candidates are financial operations).
        3. Respond with ONLY the exact intent name from the list, or the single word: none
        4. No explanation, no punctuation, no prose — one token answer only.
        """;

    public BedrockDisambiguator(AppConfig config)
    {
        _llm = new BedrockLlmHelper(config);
    }

    /// <inheritdoc/>
    public async Task<string?> SelectAsync(
        string normalizedQuery,
        IReadOnlyList<(string Intent, string Understanding)> candidates,
        CancellationToken ct = default)
    {
        if (candidates.Count == 0) return null;

        // Build candidate list: "1. CreateInvoice — Create and send an invoice …"
        var candidateLines = string.Join("\n",
            candidates.Select((c, i) => $"{i + 1}. {c.Intent} — {c.Understanding}"));

        var userContent = $"""
            User message: "{normalizedQuery}"

            Candidate intents:
            {candidateLines}

            Which intent matches? Respond with the intent name or none:
            """;

        try
        {
            var raw = await _llm.ConverseAsync(SystemPrompt, userContent, maxTokens: 20, ct: ct);

            // Accept only an exact candidate name (case-insensitive) or "none"
            if (raw.Equals("none", StringComparison.OrdinalIgnoreCase))
                return null;

            var match = candidates.FirstOrDefault(
                c => c.Intent.Equals(raw, StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrEmpty(match.Intent) ? null : match.Intent;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine($"  [Disambiguator] LLM call failed ({ex.GetType().Name}) — skipping.");
            Console.ResetColor();
            // Reject on failure: a false-positive match (e.g. "create a customer" → CreateInvoice)
            // is more harmful than discarding the candidate and falling through to streaming.
            return null;
        }
    }
}
