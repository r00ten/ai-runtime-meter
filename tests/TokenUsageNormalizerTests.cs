using DerivityMeter.Collectors;
using Xunit;

namespace DerivityMeter.Tests;

public class TokenUsageNormalizerTests
{
    [Theory]
    // input aliases
    [InlineData("input", TokenKind.Input)]
    [InlineData("input_tokens", TokenKind.Input)]
    [InlineData("inputTokens", TokenKind.Input)]
    [InlineData("gen_ai.usage.input_tokens", TokenKind.Input)]
    // output aliases
    [InlineData("output", TokenKind.Output)]
    [InlineData("output_tokens", TokenKind.Output)]
    [InlineData("outputTokens", TokenKind.Output)]
    // cache read aliases
    [InlineData("cacheRead", TokenKind.CacheRead)]
    [InlineData("cache_read", TokenKind.CacheRead)]
    [InlineData("cacheReadTokens", TokenKind.CacheRead)]
    [InlineData("cache_read_tokens", TokenKind.CacheRead)]
    [InlineData("cache_read_input_tokens", TokenKind.CacheRead)]
    [InlineData("cacheReadInputTokens", TokenKind.CacheRead)]
    // cache creation aliases
    [InlineData("cacheCreation", TokenKind.CacheCreation)]
    [InlineData("cache_creation", TokenKind.CacheCreation)]
    [InlineData("cacheCreationTokens", TokenKind.CacheCreation)]
    [InlineData("cache_creation_tokens", TokenKind.CacheCreation)]
    [InlineData("cache_creation_input_tokens", TokenKind.CacheCreation)]
    [InlineData("cacheCreationInputTokens", TokenKind.CacheCreation)]
    public void ClassifyAttributeKey_maps_known_aliases(string key, TokenKind expected)
        => Assert.Equal(expected, TokenUsageNormalizer.ClassifyAttributeKey(key));

    [Theory]
    [InlineData("input", TokenKind.Input)]
    [InlineData("output", TokenKind.Output)]
    [InlineData("cacheRead", TokenKind.CacheRead)]
    [InlineData("cacheCreation", TokenKind.CacheCreation)]
    [InlineData("cache_read", TokenKind.CacheRead)]
    [InlineData("cache_creation", TokenKind.CacheCreation)]
    public void ClassifyTypeValue_maps_metric_type_dimension(string value, TokenKind expected)
        => Assert.Equal(expected, TokenUsageNormalizer.ClassifyTypeValue(value));

    [Theory]
    [InlineData("max_tokens")]
    [InlineData("max_output_tokens")]
    [InlineData("token_limit")]
    [InlineData("remaining_tokens")]
    [InlineData("duration_ms")]
    [InlineData("cost_usd")]
    [InlineData("model")]
    [InlineData("prompt_length")]
    public void ClassifyAttributeKey_ignores_config_and_non_token_fields(string key)
        => Assert.Equal(TokenKind.None, TokenUsageNormalizer.ClassifyAttributeKey(key));

    [Theory]
    [InlineData("thinking_tokens", true)]   // real future field Claude Code may add
    [InlineData("reasoning_tokens", true)]
    [InlineData("cache_read_tokens", false)] // maps cleanly, not "unknown"
    [InlineData("max_tokens", false)]        // denied config field, not flagged
    [InlineData("duration_ms", false)]       // not token-like at all
    public void IsUnknownTokenLike_flags_only_unmapped_token_fields(string key, bool expected)
        => Assert.Equal(expected, TokenUsageNormalizer.IsUnknownTokenLike(key));

    [Theory]
    [InlineData("3000", true, 3000)]
    [InlineData("420.0", true, 420)]
    [InlineData("", false, 0)]
    [InlineData("abc", false, 0)]
    public void TryParseCount_handles_int_and_double_strings(string raw, bool ok, long expected)
    {
        Assert.Equal(ok, TokenUsageNormalizer.TryParseCount(raw, out var v));
        Assert.Equal(expected, v);
    }
}
