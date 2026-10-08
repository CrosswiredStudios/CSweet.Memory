namespace CSweet.Memory;

/// <summary>Content-level filtering for authorized raw reads. Does not authorize a namespace.</summary>
public static class MemoryReadProjection
{
    public static MemoryExport Create(MemoryExport export, MemoryPartition partition,
        MemorySensitivity maximum, DateTimeOffset asOf)
    {
        bool Allowed(MemorySensitivity value) => Enum.IsDefined(value) && Enum.IsDefined(maximum) && value <= maximum;
        var episodes = export.Episodes.Where(x => MemoryProvenance.IsCurrent(x, partition, x.Id, asOf) && Allowed(x.Sensitivity))
            .ToDictionary(x => x.Id);
        var entities = export.Entities.Select(x => MemoryProvenance.ResolveEntity(x, partition, episodes, asOf))
            .OfType<MemoryEntity>().Where(x => Allowed(x.Sensitivity)).ToDictionary(x => x.Id);
        var claims = export.Claims.Where(x => x.Partition == partition)
            .Select(x => MemoryProvenance.ResolveClaim(x, episodes.GetValueOrDefault(x.EpisodeId),
                entities.GetValueOrDefault(x.SubjectEntityId), x.ObjectEntityId is { } id ? entities.GetValueOrDefault(id) : null, asOf, episodes))
            .OfType<MemoryClaim>().Where(x => Allowed(x.Sensitivity) && Current(x.ValidFrom, x.ValidTo, asOf) && Confirmed(x.Confirmation)).ToList();
        var edges = export.Edges.Where(x => x.Partition == partition && Current(x.ValidFrom, x.ValidTo, asOf) &&
            MemoryProvenance.ResolveEdge(x, episodes.GetValueOrDefault(x.EpisodeId), entities.GetValueOrDefault(x.FromEntityId),
                entities.GetValueOrDefault(x.ToEntityId), asOf, episodes) is { } sensitivity && Allowed(sensitivity)).ToList();
        var blocks = export.Blocks.Select(x => MemoryProvenance.ResolveBlock(x, partition, episodes, asOf))
            .OfType<MemoryBlock>().Where(x => Allowed(x.Sensitivity) && Confirmed(x.Confirmation)).ToList();
        var procedures = export.Procedures.Where(x => x.Partition == partition && episodes.ContainsKey(x.EpisodeId) &&
            MemoryProvenance.ResolveSensitivity(partition, episodes[x.EpisodeId].Sensitivity, x.SourceEpisodeIds, episodes, asOf) is { } sensitivity && Allowed(sensitivity) &&
            Current(x.ValidFrom, x.ValidTo, asOf) && Confirmed(x.Confirmation)).ToList();
        var parents = episodes.Keys.Select(id => (MemoryLayer.Episodic, id))
            .Concat(claims.Select(x => (MemoryLayer.Semantic, x.Id)))
            .Concat(edges.Select(x => (MemoryLayer.Semantic, x.Id)))
            .Concat(blocks.Select(x => (MemoryLayer.Core, x.Id)))
            .Concat(procedures.Select(x => (MemoryLayer.Procedural, x.Id))).ToHashSet();
        return new MemoryExport(export.SchemaVersion, episodes.Values.ToList(), entities.Values.ToList(), claims, edges,
            blocks, procedures, (export.Embeddings ?? []).Where(x => x.Partition == partition && parents.Contains((x.Layer, x.MemoryId))).ToList());
    }

    public static IEnumerable<KnowledgeTransferItem> TransferItems(MemoryExport projection, MemoryPartition partition)
    {
        var episodes = projection.Episodes.ToDictionary(x => x.Id);
        var entities = projection.Entities.ToDictionary(x => x.Id);
        foreach (var episode in projection.Episodes)
            yield return new(episode.Id, partition, MemoryLayer.Episodic, MemoryClaimKind.Observation, episode.Content,
                episode.Sensitivity, episode.Source.Type.Equals("application", StringComparison.OrdinalIgnoreCase)
                    ? MemoryTrustTier.Authoritative : episode.Source.Type.Equals("user", StringComparison.OrdinalIgnoreCase)
                    ? MemoryTrustTier.UnconfirmedUser : MemoryTrustTier.External, [episode.Id], $"memory:{episode.Id:N}");
        foreach (var claim in projection.Claims)
            yield return new(claim.Id, partition, MemoryLayer.Semantic, claim.Kind,
                $"{entities[claim.SubjectEntityId].CanonicalName} {claim.Predicate} {claim.Value ?? (claim.ObjectEntityId is { } objectId ? entities[objectId].CanonicalName : string.Empty)}",
                claim.Sensitivity, claim.Trust, new[] { claim.EpisodeId }.Concat(claim.SourceEpisodeIds).Concat(entities[claim.SubjectEntityId].SourceEpisodeIds)
                    .Concat(claim.ObjectEntityId is { } objectId2 ? entities[objectId2].SourceEpisodeIds : []).Distinct().ToArray(), $"memory:{claim.Id:N}");
        foreach (var edge in projection.Edges)
            yield return new(edge.Id, partition, MemoryLayer.Semantic, MemoryClaimKind.Fact,
                $"{entities[edge.FromEntityId].CanonicalName} {edge.Relationship} {entities[edge.ToEntityId].CanonicalName}",
                MemoryProvenance.Maximum(edge.SourceEpisodeIds.Select(id => episodes[id].Sensitivity).Append(episodes[edge.EpisodeId].Sensitivity)
                    .Append(entities[edge.FromEntityId].Sensitivity).Append(entities[edge.ToEntityId].Sensitivity).ToArray()),
                edge.Trust, new[] { edge.EpisodeId }.Concat(edge.SourceEpisodeIds).Concat(entities[edge.FromEntityId].SourceEpisodeIds)
                    .Concat(entities[edge.ToEntityId].SourceEpisodeIds).Distinct().ToArray(), $"memory:{edge.Id:N}");
        foreach (var block in projection.Blocks)
            yield return new(block.Id, partition, MemoryLayer.Core, MemoryClaimKind.Observation, block.Content,
                block.Sensitivity, block.Trust, block.SourceEpisodeIds, $"memory:{block.Id:N}");
        foreach (var procedure in projection.Procedures)
            yield return new(procedure.Id, partition, MemoryLayer.Procedural, MemoryClaimKind.Handoff, $"{procedure.Name}: {procedure.Procedure}",
                MemoryProvenance.Maximum(procedure.SourceEpisodeIds.Select(id => episodes[id].Sensitivity).Append(episodes[procedure.EpisodeId].Sensitivity).ToArray()),
                procedure.Trust, new[] { procedure.EpisodeId }.Concat(procedure.SourceEpisodeIds).Distinct().ToArray(), $"memory:{procedure.Id:N}");
    }

    private static bool Current(DateTimeOffset from, DateTimeOffset? to, DateTimeOffset asOf) => from <= asOf && (to is null || to > asOf);
    private static bool Confirmed(MemoryConfirmationState value) => value is MemoryConfirmationState.NotRequired or MemoryConfirmationState.Confirmed;
}
