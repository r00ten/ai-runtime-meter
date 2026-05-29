using System.Text;

namespace DerivityMeter.Collectors;

/// <summary>
/// Canonical token buckets. These map 1:1 onto RuntimeUsageMetric's existing
/// internal fields — this normalizer never renames model properties or JSON,
/// it only collapses external aliases into the canonical internal shape at ingest.
/// </summary>
public enum TokenKind { None, Input, Output, CacheRead, CacheCreation }

/// <summary>
/// Maps the many shapes Claude Code (and other OTLP sources) use for token/cache
/// counts into the four canonical RuntimeUsageMetric fields:
///
///   external alias  ->  TokenKind  ->  RuntimeUsageMetric.InputTokens
///                                       RuntimeUsageMetric.OutputTokens
///                                       RuntimeUsageMetric.CacheReadInputTokens
///                                       RuntimeUsageMetric.CacheCreationInputTokens
///
/// Two entry points by context:
///   - ClassifyAttributeKey : for log/event attribute KEYS (e.g. "cache_read_tokens").
///     Conservative: explicit alias table first, fuzzy only for clearly token-related
///     keys, with a deny-list so config fields like "max_tokens" are never counted.
///   - ClassifyTypeValue    : for metric "type" dimension VALUES (e.g. "cacheRead").
///     The value is already known to be a token type, so fuzzy classification is safe.
/// </summary>
internal static class TokenUsageNormalizer
{
    // Primary, safest path: exact (case-insensitive) alias match.
    private static readonly Dictionary<string, TokenKind> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        // input
        ["input"] = TokenKind.Input,
        ["input_tokens"] = TokenKind.Input,
        ["inputTokens"] = TokenKind.Input,
        ["llm.usage.input_tokens"] = TokenKind.Input,
        ["gen_ai.usage.input_tokens"] = TokenKind.Input,

        // output
        ["output"] = TokenKind.Output,
        ["output_tokens"] = TokenKind.Output,
        ["outputTokens"] = TokenKind.Output,
        ["llm.usage.output_tokens"] = TokenKind.Output,
        ["gen_ai.usage.output_tokens"] = TokenKind.Output,

        // cache read
        ["cacheRead"] = TokenKind.CacheRead,
        ["cache_read"] = TokenKind.CacheRead,
        ["cacheReadTokens"] = TokenKind.CacheRead,
        ["cache_read_tokens"] = TokenKind.CacheRead,
        ["cacheReadInputTokens"] = TokenKind.CacheRead,
        ["cache_read_input_tokens"] = TokenKind.CacheRead,
        ["llm.usage.cache_read_input_tokens"] = TokenKind.CacheRead,
        ["gen_ai.usage.cache_read_input_tokens"] = TokenKind.CacheRead,

        // cache creation (a.k.a. cache write)
        ["cacheCreation"] = TokenKind.CacheCreation,
        ["cache_creation"] = TokenKind.CacheCreation,
        ["cacheCreationTokens"] = TokenKind.CacheCreation,
        ["cache_creation_tokens"] = TokenKind.CacheCreation,
        ["cacheCreationInputTokens"] = TokenKind.CacheCreation,
        ["cache_creation_input_tokens"] = TokenKind.CacheCreation,
        ["llm.usage.cache_creation_input_tokens"] = TokenKind.CacheCreation,
        ["gen_ai.usage.cache_creation_input_tokens"] = TokenKind.CacheCreation,
    };

    // Compacted fragments that mark a token-named field as a limit/config value,
    // not a usage count. Checked against the compacted (lowercased, alphanumeric) key.
    private static readonly string[] DenyFragments =
        { "max", "limit", "remaining", "budget", "window", "totaltokens" };

    /// <summary>Classify a metric "type" dimension value (input/output/cacheRead/cacheCreation).</summary>
    public static TokenKind ClassifyTypeValue(string? typeValue)
    {
        if (string.IsNullOrEmpty(typeValue)) return TokenKind.None;
        if (Aliases.TryGetValue(typeValue, out var exact)) return exact;
        return ClassifyCompact(Compact(typeValue));
    }

    /// <summary>
    /// Classify a log/event attribute key. Explicit alias first; fuzzy fallback only
    /// for keys that are clearly token-related and not in the deny-list.
    /// </summary>
    public static TokenKind ClassifyAttributeKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return TokenKind.None;
        if (Aliases.TryGetValue(key, out var exact)) return exact;

        var compact = Compact(key);
        if (!compact.Contains("token")) return TokenKind.None; // must be token-related
        if (IsDenied(compact)) return TokenKind.None;

        return ClassifyCompact(compact);
    }

    /// <summary>
    /// True when a key looks token-related but did not classify into a usage bucket
    /// (and is not a known config/limit field). Surfaces alias drift in debug output.
    /// </summary>
    public static bool IsUnknownTokenLike(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (Aliases.ContainsKey(key)) return false;
        var compact = Compact(key);
        return compact.Contains("token") && !IsDenied(compact) && ClassifyCompact(compact) == TokenKind.None;
    }

    private static bool IsDenied(string compact)
    {
        foreach (var deny in DenyFragments)
            if (compact.Contains(deny)) return true;
        return false;
    }

    /// <summary>Parse a token count that may arrive as "123" or "123.0".</summary>
    public static bool TryParseCount(string? raw, out long value)
    {
        value = 0;
        if (string.IsNullOrEmpty(raw)) return false;
        if (long.TryParse(raw, out value)) return true;
        if (double.TryParse(raw, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d))
        {
            value = (long)d;
            return true;
        }
        return false;
    }

    // Cache checked BEFORE input/output: "cache_read_input_tokens" also contains "input".
    private static TokenKind ClassifyCompact(string compact)
    {
        if (compact.Length == 0) return TokenKind.None;
        if (compact.Contains("cacheread")) return TokenKind.CacheRead;
        if (compact.Contains("cachecreation") || compact.Contains("cachewrite")) return TokenKind.CacheCreation;
        if (compact.Contains("input")) return TokenKind.Input;
        if (compact.Contains("output")) return TokenKind.Output;
        return TokenKind.None;
    }

    private static string Compact(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }
}

/// <summary>Accumulates canonical token counts during a single parse.</summary>
internal sealed class TokenTotals
{
    public long Input { get; private set; }
    public long Output { get; private set; }
    public long CacheRead { get; private set; }
    public long CacheCreation { get; private set; }

    public bool HasAny => Input != 0 || Output != 0 || CacheRead != 0 || CacheCreation != 0;

    public void Add(TokenKind kind, long value)
    {
        switch (kind)
        {
            case TokenKind.Input: Input += value; break;
            case TokenKind.Output: Output += value; break;
            case TokenKind.CacheRead: CacheRead += value; break;
            case TokenKind.CacheCreation: CacheCreation += value; break;
        }
    }
}

/// <summary>Diagnostics gathered during a parse so the collector can surface what it saw.</summary>
internal sealed class OtelParseDiagnostics
{
    public int RecordsSeen { get; set; }
    public HashSet<string> SeenKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> UnknownTokenLikeKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
}
