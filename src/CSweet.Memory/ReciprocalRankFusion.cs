namespace CSweet.Memory;

public static class ReciprocalRankFusion
{
    public static IReadOnlyList<MemoryCandidate> Rank(IEnumerable<MemoryCandidate> candidates, int rankConstant = 60)
    {
        var materialized = candidates.ToList();
        var totals = new Dictionary<Guid, double>();
        foreach (var channel in materialized.GroupBy(candidate => candidate.RetrievalChannel, StringComparer.Ordinal))
        {
            var rank = 1;
            foreach (var candidate in channel.OrderByDescending(candidate => candidate.Score))
            {
                var authority = 1d + ((int)candidate.Trust * 0.05d);
                var confirmation = candidate.Confirmation == MemoryConfirmationState.Rejected ? 0d :
                    candidate.Confirmation == MemoryConfirmationState.Pending ? 0.75d : 1d;
                totals[candidate.Id] = totals.GetValueOrDefault(candidate.Id) + (authority * confirmation / (rankConstant + rank));
                rank++;
            }
        }
        return materialized
            .GroupBy(candidate => candidate.Id)
            .Select(group => group.OrderByDescending(candidate => candidate.Score).First() with { Score = totals[group.Key] })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ToList();
    }
}
