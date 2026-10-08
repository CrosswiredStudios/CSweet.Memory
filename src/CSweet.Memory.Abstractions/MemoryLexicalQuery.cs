using System.Text.RegularExpressions;

namespace CSweet.Memory;

/// <summary>Bounded literal terms shared by both stores. Input is never treated as query syntax.</summary>
public sealed record MemoryLexicalQuery(IReadOnlyList<string> Terms)
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    { "what", "when", "where", "which", "who", "why", "how", "the", "this", "that", "with", "from",
      "have", "has", "had", "was", "were", "are", "your", "you", "my", "our", "their", "and", "but",
      "can", "could", "would", "should", "does", "did", "tell", "about", "please", "is", "a", "an", "of", "to", "for" };

    public static MemoryLexicalQuery Parse(string? query) => new(Regex.Matches(
            (query ?? string.Empty)[..Math.Min(query?.Length ?? 0, 1024)], @"[\p{L}\p{N}][\p{L}\p{N}_.:/+#-]*",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))
        .Select(x => x.Value.TrimEnd('.', ':', '/', '+', '#', '-').ToLowerInvariant())
        .Where(x => x.Length > 0 && !StopWords.Contains(x)).Distinct(StringComparer.Ordinal).Take(12).ToArray());

    public string FullText => string.Join(" OR ", Terms.Select(x => $"\"{x}\""));
}
