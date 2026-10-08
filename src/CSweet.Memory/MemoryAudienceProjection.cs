namespace CSweet.Memory;

/// <summary>Removes shared-restricted copies and every contributor-dependent record from an export.</summary>
public static class MemoryAudienceProjection
{
    public static MemoryExport Create(MemoryExport export, Func<MemoryPartition, bool> allowed)
    {
        var blocked = export.Episodes.Where(x =>
            (x.TransferEvidence is not null || x.CorrectionEvidence is not null || x.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) == true || x.Source?.Type == "knowledge-transfer") && !MemorySourceIntegrity.IsVerified(x) ||
            MemorySharedAudiences.Required(x) is { } required &&
            (required.Count is < 1 or > MemorySharedAudiences.MaximumPartitions ||
             required.Any(p => !MemorySharedAudiences.IsCanonical(p) || !allowed(p)))).Select(x => x.Id).ToHashSet();
        if (blocked.Count == 0) return export;
        bool Safe(IEnumerable<Guid> sources) => !sources.Any(blocked.Contains);
        var entities = export.Entities.Where(x => Safe(x.SourceEpisodeIds)).ToArray();
        var entityIds = entities.Select(x => x.Id).ToHashSet();
        var claims = export.Claims.Where(x => !blocked.Contains(x.EpisodeId) && Safe(x.SourceEpisodeIds) &&
            entityIds.Contains(x.SubjectEntityId) && (x.ObjectEntityId is null || entityIds.Contains(x.ObjectEntityId.Value))).ToArray();
        var edges = export.Edges.Where(x => !blocked.Contains(x.EpisodeId) && Safe(x.SourceEpisodeIds) &&
            entityIds.Contains(x.FromEntityId) && entityIds.Contains(x.ToEntityId)).ToArray();
        var blocks = export.Blocks.Where(x => Safe(x.SourceEpisodeIds)).ToArray();
        var procedures = export.Procedures.Where(x => !blocked.Contains(x.EpisodeId) && Safe(x.SourceEpisodeIds)).ToArray();
        var episodes = export.Episodes.Where(x => !blocked.Contains(x.Id)).ToArray();
        var parents = episodes.Select(x => (MemoryLayer.Episodic, x.Id)).Concat(claims.Select(x => (MemoryLayer.Semantic, x.Id)))
            .Concat(edges.Select(x => (MemoryLayer.Semantic, x.Id))).Concat(blocks.Select(x => (MemoryLayer.Core, x.Id)))
            .Concat(procedures.Select(x => (MemoryLayer.Procedural, x.Id))).ToHashSet();
        return new(export.SchemaVersion, episodes, entities, claims, edges, blocks, procedures,
            (export.Embeddings ?? []).Where(x => parents.Contains((x.Layer, x.MemoryId))).ToArray());
    }
}
