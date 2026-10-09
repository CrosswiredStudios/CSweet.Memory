using System.Text.Json;

namespace CSweet.Memory;

internal sealed partial class MemoryTransferEvidenceStorage
{
    internal async Task<MemoryCorrectionEvidence> CaptureCorrectionAsync(MemoryPartition partition, Guid operationId,
        IReadOnlyList<Guid> sourceIds, CancellationToken token)
    {
        MemoryProvenance.ValidateSourceEpisodes(sourceIds);
        if (operationId == Guid.Empty || sourceIds.Count == 0 || sourceIds.Any(x => x == Guid.Empty)) throw Invalid();
        var sources = new List<MemoryEpisode>();
        foreach (var id in sourceIds.Distinct().Order())
        {
            var row = await LoadAsync(partition, MemoryRecordKind.Episode, id, token) ?? throw Invalid();
            if (!await CurrentAsync(row, [], 1, token)) throw Invalid();
            sources.Add(row.Payload.Deserialize<MemoryEpisode>(Json) ?? throw Invalid());
        }
        var shared = CorrectionRestrictions(partition, sources);
        return new(operationId, sources.Select(x => new MemoryCorrectionSource(x.Id, x.SourceFingerprint!)).ToArray())
            { RequiredSharedPartitions = shared.Length == 0 ? null : shared };
    }

    internal async Task<bool> VerifyCorrectionRetentionAsync(MemoryEpisode episode, CancellationToken token)
    {
        try { return await VerifyCorrectionAsync(episode, [], 0, true, token); }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        { return false; }
    }

    private async Task<bool> VerifyCorrectionAsync(MemoryEpisode episode, HashSet<Guid> path, int depth, bool retention, CancellationToken token)
    {
        if (depth >= 3 || !path.Add(episode.Id)) return false;
        try
        {
            var evidence = episode.CorrectionEvidence;
            if (episode.TransferEvidence is not null || !MemorySourceIntegrity.IsVerified(episode) ||
                evidence is null || evidence.ReviewOperationId == Guid.Empty ||
                episode.Source.Type != "user" || episode.Source.Id != evidence.ReviewOperationId.ToString("D") ||
                string.IsNullOrWhiteSpace(episode.Source.Author) ||
                evidence.Sources is not { Count: > 0 and <= MemoryProvenance.MaximumSourceEpisodes } ||
                !retention && !MemoryProvenance.IsSnapshotCurrent(episode, episode.Partition, episode.Id, asOf)) return false;
            var sources = new List<MemoryEpisode>(); var ids = new HashSet<Guid>();
            foreach (var reference in evidence.Sources)
            {
                if (reference is null || reference.EpisodeId == Guid.Empty || !ids.Add(reference.EpisodeId) || path.Contains(reference.EpisodeId)) return false;
                var row = await LoadAsync(episode.Partition, MemoryRecordKind.Episode, reference.EpisodeId, token);
                var source = row?.Payload.Deserialize<MemoryEpisode>(Json);
                if (source is null || !MemorySourceIntegrity.IsVerified(source) || source.SourceFingerprint != reference.SourceFingerprint) return false;
                if (retention)
                {
                    // Retention ignores recall policy but still verifies sealed ancestry.
                    if (source.CorrectionEvidence is not null && !await VerifyCorrectionAsync(source, path, depth + 1, true, token)) return false;
                    if (source.TransferEvidence is not null && !await VerifyTransferRetentionAsync(source, path, depth + 1, token)) return false;
                    if (source.CorrectionEvidence is null && source.TransferEvidence is null &&
                        (source.Source.Type == "knowledge-transfer" || source.SourceFingerprint!.StartsWith("sha256-v3:", StringComparison.Ordinal))) return false;
                }
                else if (!await CurrentAsync(row!, path, depth + 1, token)) return false;
                sources.Add(source);
            }
            var shared = CorrectionRestrictions(episode.Partition, sources);
            if (shared.Length == 0 ? evidence.RequiredSharedPartitions is not null :
                evidence.RequiredSharedPartitions is null || !shared.SequenceEqual(evidence.RequiredSharedPartitions)) return false;
            // A policy revision cannot lower a correction below its contributors.
            return retention || episode.Sensitivity >= sources.Select(x => x.Sensitivity).Max() &&
                sources.All(x => x.ExpiresAt is null || episode.ExpiresAt is not null && episode.ExpiresAt <= x.ExpiresAt);
        }
        finally { path.Remove(episode.Id); }
    }

    private async Task<bool> VerifyTransferRetentionAsync(MemoryEpisode episode, HashSet<Guid> path, int depth, CancellationToken token)
    {
        if (depth >= 3 || !path.Add(episode.Id)) return false;
        try
        {
            var evidence = episode.TransferEvidence;
            if (episode.CorrectionEvidence is not null || !MemorySourceIntegrity.IsVerified(episode) || evidence is null ||
                evidence.Records is null || evidence.Records.Count > MemoryTransferEvidence.MaximumRecords) return false;
            var package = await PackageAsync(evidence.PackageId, token);
            if (!MatchesAppliedCertificate(episode, package, true)) return false;
            var records = new Dictionary<(MemoryPartition, MemoryRecordKind, Guid), Snapshot>();
            foreach (var reference in evidence.Records)
            {
                if (reference is null || reference.Revision <= 0 || !package!.SourceNamespaces.Any(x => x.Partition == reference.Partition)) return false;
                var row = await LoadAsync(reference.Partition, reference.Kind, reference.Id, token);
                if (row is null || row.Reference.Revision < reference.Revision) return false;
                var approved = await RetainedRevisionAsync(reference, token);
                if (approved is null || !records.TryAdd((reference.Partition, reference.Kind, reference.Id), approved)) return false;
                if (reference.Kind != MemoryRecordKind.Episode) continue;
                var source = row.Payload.Deserialize<MemoryEpisode>(Json)!;
                var approvedSource = approved.Payload.Deserialize<MemoryEpisode>(Json)!;
                if (!MemorySourceIntegrity.IsVerified(source) || !MemorySourceIntegrity.IsVerified(approvedSource) ||
                    source.SourceFingerprint != approvedSource.SourceFingerprint) return false;
                if (source.CorrectionEvidence is not null && !await VerifyCorrectionAsync(source, path, depth + 1, true, token)) return false;
                if (source.TransferEvidence is not null && !await VerifyTransferRetentionAsync(source, path, depth + 1, token)) return false;
                if (source.CorrectionEvidence is null && source.TransferEvidence is null &&
                    (source.Source.Type == "knowledge-transfer" || source.SourceFingerprint!.StartsWith("sha256-v3:", StringComparison.Ordinal))) return false;
            }
            foreach (var row in records.Values)
                if (Dependencies(row).Any(key => !records.ContainsKey(key))) return false;
            var shared = SharedRestrictions(package!, records.Values);
            return shared.Length == 0 ? evidence.RequiredSharedPartitions is null :
                evidence.RequiredSharedPartitions is not null && shared.SequenceEqual(evidence.RequiredSharedPartitions);
        }
        finally { path.Remove(episode.Id); }
    }

    private static MemoryPartition[] CorrectionRestrictions(MemoryPartition partition, IEnumerable<MemoryEpisode> sources) =>
        MemorySharedAudiences.Merge(MemorySharedAudiences.FromSources(sources)
            .Concat(MemorySharedAudiences.IsCanonical(partition) ? [partition] : []));

    private readonly Dictionary<MemoryTransferRecord, Snapshot?> retainedRevisions = [];
    private async Task<Snapshot?> RetainedRevisionAsync(MemoryTransferRecord reference, CancellationToken token)
    {
        if (retainedRevisions.TryGetValue(reference, out var existing)) return existing;
        sourceRead?.Invoke();
        if (++reads > MemoryProvenance.MaximumReadSourceEpisodes) throw Invalid();
        await using var command = commandFactory($"SELECT CAST(payload AS text) FROM {Prefix}revisions WHERE partition_key=@partition AND kind=@kind AND record_id=@id AND revision=@revision");
        Add(command,"partition",reference.Partition.StorageKey); Add(command,"kind",(int)reference.Kind);
        Add(command,"id",postgres ? reference.Id : reference.Id.ToString("D")); Add(command,"revision",reference.Revision);
        var payload=await command.ExecuteScalarAsync(token) as string;
        if (payload is null) return retainedRevisions[reference]=null;
        using var document=JsonDocument.Parse(ReadPayload(payload)); var value=document.RootElement;
        if (value.GetProperty("id").GetGuid()!=reference.Id || value.GetProperty("partition").Deserialize<MemoryPartition>(Json)!=reference.Partition)
            return retainedRevisions[reference]=null;
        return retainedRevisions[reference]=new(reference,value.Clone());
    }
}
