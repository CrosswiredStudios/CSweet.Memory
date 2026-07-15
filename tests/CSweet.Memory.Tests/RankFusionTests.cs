namespace CSweet.Memory.Tests;

public sealed class RankFusionTests
{
    [Fact]
    public void Rank_CombinesIndependentChannels_WithoutComparingRawScores()
    {
        var repeated = Guid.NewGuid();
        var other = Guid.NewGuid();
        var candidates = new[]
        {
            Candidate(repeated, "fulltext", 0.2),
            Candidate(repeated, "graph", 90),
            Candidate(other, "vector", 0.99)
        };

        var ranked = ReciprocalRankFusion.Rank(candidates);

        Assert.Equal(repeated, ranked[0].Id);
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void Rank_RemovesRejectedClaims()
    {
        var rejected = Candidate(Guid.NewGuid(), "semantic", 1) with { Confirmation = MemoryConfirmationState.Rejected };
        Assert.Empty(ReciprocalRankFusion.Rank([rejected]));
    }

    private static MemoryCandidate Candidate(Guid id, string channel, double score) =>
        new(id, MemoryLayer.Semantic, "fact", score, MemoryTrustTier.ConfirmedUser,
            MemoryConfirmationState.Confirmed, MemorySensitivity.Internal, null, null, [], channel);
}
