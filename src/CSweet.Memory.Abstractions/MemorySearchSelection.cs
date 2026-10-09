namespace CSweet.Memory;

/// <summary>A bounded search could not establish its result within the evidence allowance.
/// This is not evidence that the requested fact does not exist.</summary>
public sealed class MemorySearchBudgetExceededException : Exception
{
    public const string ErrorCode = "memory_search_budget_exceeded";
    public string Budget { get; }
    internal MemorySearchBudgetExceededException(string budget)
        : base("Memory search exceeded its evidence allowance. Narrow the query or requested layers.") => Budget = budget;
}

internal sealed class MemorySearchBudget
{
    internal const int MaximumCandidates = 8192;
    internal const int MaximumPayloadCharacters = 8 * 1024 * 1024;
    private int candidates, sources, payloadCharacters;
    internal void Candidate() { if (++candidates > MaximumCandidates) throw new MemorySearchBudgetExceededException("candidates"); }
    internal void Source(int count = 1)
    {
        sources += count;
        if (sources > MemoryProvenance.MaximumReadSourceEpisodes) throw new MemorySearchBudgetExceededException("sources");
    }
    internal string Payload(string value)
    {
        payloadCharacters += value.Length;
        if (payloadCharacters > MaximumPayloadCharacters) throw new MemorySearchBudgetExceededException("payload");
        return value;
    }
}

internal sealed record MemorySearchPage<T>(IReadOnlyList<T> Items, int Rows);

internal static class MemorySearchSelection
{
    // SQL ordering is retained across pages. LIMIT applies to fully eligible records,
    // not to a fixed overfetch window containing invalid copied/correction evidence.
    internal const int PageSize = 16;
    internal static async Task<IReadOnlyList<T>> SelectAsync<T>(int limit,
        Func<int, int, CancellationToken, Task<MemorySearchPage<T>>> read,
        Func<IReadOnlyList<T>, CancellationToken, Task<IReadOnlyList<T>>> validate,
        CancellationToken token)
    {
        var accepted = new List<T>();
        var offset = 0;
        while (accepted.Count < limit)
        {
            token.ThrowIfCancellationRequested();
            var take = Math.Min(PageSize, limit - accepted.Count);
            var page = await read(offset, take, token);
            if (page.Rows == 0) break;
            accepted.AddRange(await validate(page.Items, token));
            offset += page.Rows;
            if (page.Rows < take) break;
        }
        return accepted.Take(limit).ToArray();
    }
}
