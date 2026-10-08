using System.Data.Common;
using System.Text.Json;

namespace CSweet.Memory;

internal sealed partial class MemoryTransferEvidenceStorage(Func<string, DbCommand> commandFactory, bool postgres, DateTimeOffset asOf)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Tables = ["episodes", "entities", "claims", "edges", "blocks", "procedures"];
    private string Prefix => postgres ? "csweet_memory_" : "memory_";
    private readonly Dictionary<(MemoryPartition, MemoryRecordKind, Guid), Snapshot?> cache = [];
    private readonly Dictionary<Guid, KnowledgeTransferPackage?> packages = [];
    private int reads;
    private sealed record Snapshot(MemoryTransferRecord Reference, JsonElement Payload);

    internal async Task<MemoryTransferEvidence> CaptureAsync(KnowledgeTransferPackage package, CancellationToken token)
    {
        if (package.Status != KnowledgeTransferStatus.Approved || !ValidAudience(package)) throw Invalid();
        var selected = new Dictionary<(MemoryPartition, MemoryRecordKind, Guid), Snapshot>();
        foreach (var item in package.Items)
        {
            var root = await RootAsync(item, token) ?? throw Invalid();
            var closure = new Dictionary<(MemoryPartition, MemoryRecordKind, Guid), Snapshot>();
            await CollectAsync(root, closure, token);
            foreach (var row in closure.Values)
            {
                // Reserve depth zero for the new copy so approval never creates an unreadable chain.
                if (!await CurrentAsync(row, new HashSet<Guid>(), 1, token)) throw Invalid();
                selected[(row.Reference.Partition, row.Reference.Kind, row.Reference.Id)] = row;
            }
            var episodes = closure.Values.Where(x => x.Reference.Kind == MemoryRecordKind.Episode).Select(x => x.Reference.Id).Order().ToArray();
            var sensitivity = closure.Values.Select(Sensitivity).Append(item.Sensitivity).Max();
            if (item.EpisodeIds is null || !episodes.SequenceEqual(item.EpisodeIds.Distinct().Order()) ||
                sensitivity != item.Sensitivity || Content(root, closure) != item.Content || Trust(root) != item.Trust ||
                Kind(root) != item.Kind || item.Citation != $"memory:{item.MemoryId:N}") throw Invalid();
        }
        if (selected.Count > MemoryTransferEvidence.MaximumRecords) throw Invalid();
        var restrictions = SharedRestrictions(package, selected.Values);
        return new(package.Id, MemoryTransferEvidence.Fingerprint(package), selected.Values.Select(x => x.Reference)
            .OrderBy(x => x.Partition.StorageKey, StringComparer.Ordinal).ThenBy(x => x.Kind).ThenBy(x => x.Id).ToArray(),
            selected.Values.Any(x => x.Reference.Kind == MemoryRecordKind.Episode && x.Payload.GetProperty("legalHold").GetBoolean()))
            { RequiredSharedPartitions = restrictions.Length == 0 ? null : restrictions };
    }

    internal async Task<MemoryEpisode> ResolveAsync(MemoryEpisode episode, CancellationToken token)
    {
        // Start from no derived state: a caller-supplied copy cannot carry an earlier result forward.
        episode = episode with { VerifiedEvidenceFingerprint = null, RetainedEvidenceFingerprint = null };
        try
        {
            if (await VerifyAsync(episode, new HashSet<Guid>(), 0, token))
                return episode with { VerifiedEvidenceFingerprint = episode.SourceFingerprint, RetainedEvidenceFingerprint = episode.SourceFingerprint };
            return await VerifyRetainedIntegrityAsync(episode, token)
                ? episode with { RetainedEvidenceFingerprint = episode.SourceFingerprint } : episode;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
        { return episode; }
    }

    // Retained integrity lets an authorized inspection keep suppressed, expired or revoked copies
    // visible as evidence. It never grants recall: eligibility still requires TransferEvidenceVerified.
    private async Task<bool> VerifyRetainedIntegrityAsync(MemoryEpisode episode, CancellationToken token)
    {
        if (!MemorySourceIntegrity.IsVerified(episode)) return false;
        try
        {
            if (episode.CorrectionEvidence is not null) return await VerifyCorrectionAsync(episode, [], 0, true, token);
            if (episode.TransferEvidence is { Records.Count: 0 }) return await VerifyNotesOnlyRetentionAsync(episode, token);
            if (episode.TransferEvidence is not null) return await VerifyTransferRetentionAsync(episode, [], 0, token);
            return episode.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) != true &&
                !string.Equals(episode.Source.Type, "knowledge-transfer", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        { return false; }
    }

    // Retention verification does not establish recall eligibility. Suppressed,
    // expired or revoked notes-only copies still need an exact live certificate
    // before a trusted coordinator may erase them or review a hold release.
    internal async Task<bool> VerifyNotesOnlyRetentionAsync(MemoryEpisode episode, CancellationToken token)
    {
        try
        {
            if (!MemorySourceIntegrity.IsVerified(episode) || episode.TransferEvidence?.Records is not { Count: 0 }) return false;
            var evidence = episode.TransferEvidence;
            var package = await PackageAsync(evidence.PackageId, token);
            if (!MatchesAppliedCertificate(episode, package, allowRejected: true) || package!.Items.Count != 0 ||
                evidence.LegalHold || episode.Sensitivity < package.DebriefSensitivity) return false;
            var required = SharedRestrictions(package, []);
            return required.Length == 0 ? evidence.RequiredSharedPartitions is null :
                evidence.RequiredSharedPartitions is not null && required.SequenceEqual(evidence.RequiredSharedPartitions);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        { return false; }
    }

    private bool MatchesAppliedCertificate(MemoryEpisode episode, KnowledgeTransferPackage? package, bool allowRejected)
    {
        var evidence = episode.TransferEvidence;
        return evidence is not null && package is not null && package.Id == evidence.PackageId && ValidAudience(package) &&
            (package.Status == KnowledgeTransferStatus.Applied || allowRejected && package.Status == KnowledgeTransferStatus.Rejected) &&
            episode.Source == new MemorySource("knowledge-transfer", package.Id.ToString("D"), package.SourceEmployeeId) &&
            episode.Content == MemoryTransferEvidence.RenderContent(package) && package.AppliedEpisodeId == episode.Id &&
            package.TargetNamespace.Partition == episode.Partition && package.TargetNamespace.Scope == episode.Scope &&
            package.AppliedAt is not null && package.AppliedAt <= asOf &&
            evidence.PackageFingerprint == MemoryTransferEvidence.Fingerprint(package) && package.ApprovedEvidence is not null &&
            JsonSerializer.Serialize(package.ApprovedEvidence, Json) == JsonSerializer.Serialize(evidence, Json);
    }

    internal async Task EnsureDeletionAllowedAsync(MemoryPartition partition, CancellationToken token)
    {
        var predicate = postgres ? "payload->>'transferEvidence' IS NOT NULL OR payload->>'correctionEvidence' IS NOT NULL OR payload->>'sourceFingerprint' LIKE 'sha256-v3:%' OR lower(payload->'source'->>'type')='knowledge-transfer'"
            : "json_extract(payload,'$.transferEvidence') IS NOT NULL OR json_extract(payload,'$.correctionEvidence') IS NOT NULL OR json_extract(payload,'$.sourceFingerprint') LIKE 'sha256-v3:%' OR lower(json_extract(payload,'$.source.type'))='knowledge-transfer'";
        await using var command = commandFactory($"SELECT CAST(payload AS text) FROM {Prefix}episodes WHERE partition_key=@partition AND ({predicate}) LIMIT 8193");
        Add(command, "partition", partition.StorageKey);
        var episodes = new List<MemoryEpisode>();
        await using (var reader = await command.ExecuteReaderAsync(token))
            while (await reader.ReadAsync(token)) episodes.Add(JsonSerializer.Deserialize<MemoryEpisode>(reader.GetString(0), Json)!);
        if (episodes.Count > MemoryProvenance.MaximumReadSourceEpisodes) throw new InvalidOperationException("memory_transfer_retention_review_required");
        // Resolve correction retention first so a live upstream hold cannot be hidden
        // behind a copied record's now-stale recall revision.
        foreach (var episode in episodes.Where(x => x.CorrectionEvidence is not null))
        {
            if (!await VerifyCorrectionRetentionAsync(episode, token))
                throw new InvalidOperationException("memory_transfer_retention_review_required");
            if (cache.Values.Any(x => x?.Reference.Kind == MemoryRecordKind.Episode && x.Payload.GetProperty("legalHold").GetBoolean()))
                throw new InvalidOperationException("memory_legal_hold_prevents_deletion");
        }
        foreach (var episode in episodes.Where(x => x.CorrectionEvidence is null))
            if (episode.TransferEvidence is null || episode.TransferEvidence.LegalHold || !(await ResolveAsync(episode, token)).TransferEvidenceVerified)
                throw new InvalidOperationException("memory_transfer_retention_review_required");
    }

    private async Task<bool> VerifyAsync(MemoryEpisode episode, HashSet<Guid> path, int depth, CancellationToken token)
    {
        if (episode.CorrectionEvidence is not null) return await VerifyCorrectionAsync(episode, path, depth, false, token);
        if (episode.SourceFingerprint?.StartsWith("sha256-v3:", StringComparison.Ordinal) == true) return false;
        if (episode.TransferEvidence is null) return !string.Equals(episode.Source.Type, "knowledge-transfer", StringComparison.OrdinalIgnoreCase);
        if (depth >= 3 || !path.Add(episode.Id) || !MemoryProvenance.IsSnapshotCurrent(episode, episode.Partition, episode.Id, asOf)) return false;
        try
        {
            var evidence = episode.TransferEvidence;
            if (evidence.Records is null || evidence.Records.Count > MemoryTransferEvidence.MaximumRecords) return false;
            var package = await PackageAsync(evidence.PackageId, token);
            if (package is null || !MatchesAppliedCertificate(episode, package, allowRejected: false)) return false;
            var records = new Dictionary<(MemoryPartition, MemoryRecordKind, Guid), Snapshot>();
            foreach (var reference in evidence.Records)
            {
                if (reference is null || reference.Revision <= 0 || !package!.SourceNamespaces.Any(x => x.Partition == reference.Partition)) return false;
                var row = await LoadAsync(reference.Partition, reference.Kind, reference.Id, token);
                if (row is null || row.Reference.Revision != reference.Revision || !records.TryAdd((reference.Partition, reference.Kind, reference.Id), row) ||
                    !await CurrentAsync(row, path, depth + 1, token)) return false;
            }
            foreach (var row in records.Values)
                if (Dependencies(row).Any(key => !records.ContainsKey(key))) return false;
            foreach (var item in package!.Items)
            {
                var root = await RootAsync(item, token);
                if (root is null || !records.ContainsKey((root.Reference.Partition, root.Reference.Kind, root.Reference.Id))) return false;
            }
            var restrictions = SharedRestrictions(package, records.Values);
            if (restrictions.Length == 0 ? evidence.RequiredSharedPartitions is not null :
                evidence.RequiredSharedPartitions is null || !restrictions.SequenceEqual(evidence.RequiredSharedPartitions)) return false;
            return (!evidence.LegalHold || episode.LegalHold) &&
                episode.Sensitivity >= records.Values.Select(Sensitivity).Concat(package.Items.Select(x => x.Sensitivity))
                    .Append(package.DebriefSensitivity).Max();
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or NullReferenceException)
        { return false; }
        finally { path.Remove(episode.Id); }
    }

    private async Task CollectAsync(Snapshot row, Dictionary<(MemoryPartition, MemoryRecordKind, Guid), Snapshot> rows, CancellationToken token)
    {
        var key = (row.Reference.Partition, row.Reference.Kind, row.Reference.Id);
        if (!rows.TryAdd(key, row)) return;
        if (rows.Count > MemoryTransferEvidence.MaximumRecords) throw Invalid();
        foreach (var (partition, kind, id) in Dependencies(row)) await CollectAsync(await LoadAsync(partition, kind, id, token) ?? throw Invalid(), rows, token);
    }

    private static IEnumerable<(MemoryPartition, MemoryRecordKind, Guid)> Dependencies(Snapshot row)
    {
        var value = row.Payload; var partition = row.Reference.Partition;
        if (row.Reference.Kind == MemoryRecordKind.Episode && value.TryGetProperty("correctionEvidence", out var correction) && correction.ValueKind != JsonValueKind.Null)
        {
            var evidence = correction.Deserialize<MemoryCorrectionEvidence>(Json) ?? throw Invalid();
            if (evidence.Sources is not { Count: > 0 and <= MemoryProvenance.MaximumSourceEpisodes }) throw Invalid();
            foreach (var source in evidence.Sources) yield return (partition, MemoryRecordKind.Episode, source.EpisodeId);
        }
        if (value.TryGetProperty("sourceEpisodeIds", out var ids))
        {
            if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > MemoryProvenance.MaximumSourceEpisodes) throw Invalid();
            foreach (var id in ids.EnumerateArray()) yield return (partition, MemoryRecordKind.Episode, id.GetGuid());
        }
        foreach (var name in row.Reference.Kind switch
        {
            MemoryRecordKind.Claim => new[] { "episodeId", "subjectEntityId", "objectEntityId" },
            MemoryRecordKind.Edge => new[] { "episodeId", "fromEntityId", "toEntityId" },
            MemoryRecordKind.Procedure => new[] { "episodeId" }, _ => Array.Empty<string>()
        })
            if (value.TryGetProperty(name, out var id) && id.ValueKind != JsonValueKind.Null)
                yield return (partition, name == "episodeId" ? MemoryRecordKind.Episode : MemoryRecordKind.Entity, id.GetGuid());
    }

    private async Task<bool> CurrentAsync(Snapshot row, HashSet<Guid> path, int depth, CancellationToken token)
    {
        var value = row.Payload;
        if (row.Reference.Kind == MemoryRecordKind.Episode)
        {
            var source = value.Deserialize<MemoryEpisode>(Json)!;
            return MemoryProvenance.IsSnapshotCurrent(source, row.Reference.Partition, row.Reference.Id, asOf) &&
                await VerifyAsync(source, path, depth, token);
        }
        if (!Enum.IsDefined(Sensitivity(row))) return false;
        if (row.Reference.Kind is MemoryRecordKind.Claim or MemoryRecordKind.Procedure &&
            value.GetProperty("confirmation").GetInt32() is not (0 or 2)) return false;
        if (row.Reference.Kind == MemoryRecordKind.Block && value.TryGetProperty("confirmation", out var confirmation) &&
            confirmation.GetInt32() is not (0 or 2)) return false;
        if (value.TryGetProperty("validFrom", out var from) && from.GetDateTimeOffset() > asOf) return false;
        if (value.TryGetProperty("validTo", out var to) && to.ValueKind != JsonValueKind.Null && to.GetDateTimeOffset() <= asOf) return false;
        if (row.Reference.Kind == MemoryRecordKind.Block && value.GetProperty("updatedAt").GetDateTimeOffset() > asOf) return false;
        return true;
    }

    private static MemorySensitivity Sensitivity(Snapshot row) => row.Payload.TryGetProperty("sensitivity", out var value)
        ? (MemorySensitivity)value.GetInt32() : row.Reference.Kind is MemoryRecordKind.Edge or MemoryRecordKind.Procedure
            ? MemorySensitivity.Public : MemorySensitivity.Restricted;

    private static MemoryTrustTier Trust(Snapshot row) => row.Reference.Kind == MemoryRecordKind.Episode
        ? row.Payload.GetProperty("source").GetProperty("type").GetString()?.ToLowerInvariant() switch
        { "application" => MemoryTrustTier.Authoritative, "user" => MemoryTrustTier.UnconfirmedUser, _ => MemoryTrustTier.External }
        : (MemoryTrustTier)row.Payload.GetProperty("trust").GetInt32();

    private static MemoryClaimKind Kind(Snapshot row) => row.Reference.Kind switch
    {
        MemoryRecordKind.Claim => row.Payload.TryGetProperty("kind", out var kind) ? (MemoryClaimKind)kind.GetInt32() : MemoryClaimKind.Fact,
        MemoryRecordKind.Edge => MemoryClaimKind.Fact,
        MemoryRecordKind.Procedure => MemoryClaimKind.Handoff,
        _ => MemoryClaimKind.Observation
    };

    private static string Content(Snapshot root, Dictionary<(MemoryPartition, MemoryRecordKind, Guid), Snapshot> rows)
    {
        var p = root.Payload;
        string Entity(string property) => rows[(root.Reference.Partition, MemoryRecordKind.Entity, p.GetProperty(property).GetGuid())].Payload.GetProperty("canonicalName").GetString()!;
        return root.Reference.Kind switch
        {
            MemoryRecordKind.Episode or MemoryRecordKind.Block => p.GetProperty("content").GetString()!,
            MemoryRecordKind.Claim => $"{Entity("subjectEntityId")} {p.GetProperty("predicate").GetString()} {p.GetProperty("value").GetString() ?? (p.GetProperty("objectEntityId").ValueKind == JsonValueKind.Null ? "" : Entity("objectEntityId"))}",
            MemoryRecordKind.Edge => $"{Entity("fromEntityId")} {p.GetProperty("relationship").GetString()} {Entity("toEntityId")}",
            MemoryRecordKind.Procedure => $"{p.GetProperty("name").GetString()}: {p.GetProperty("procedure").GetString()}",
            _ => throw Invalid()
        };
    }

    private async Task<Snapshot?> RootAsync(KnowledgeTransferItem item, CancellationToken token)
    {
        var kind = item.Layer switch { MemoryLayer.Episodic => MemoryRecordKind.Episode, MemoryLayer.Core => MemoryRecordKind.Block,
            MemoryLayer.Procedural => MemoryRecordKind.Procedure, MemoryLayer.Semantic => MemoryRecordKind.Claim, _ => (MemoryRecordKind)(-1) };
        var root = await LoadAsync(item.SourcePartition, kind, item.MemoryId, token);
        if (item.Layer != MemoryLayer.Semantic) return root;
        var edge = await LoadAsync(item.SourcePartition, MemoryRecordKind.Edge, item.MemoryId, token);
        return root is not null && edge is not null ? null : root ?? edge;
    }

    private async Task<Snapshot?> LoadAsync(MemoryPartition partition, MemoryRecordKind kind, Guid id, CancellationToken token)
    {
        if (id == Guid.Empty || (int)kind is < 0 or >= 6) return null;
        var key = (partition, kind, id);
        if (cache.TryGetValue(key, out var existing)) return existing;
        if (++reads > MemoryProvenance.MaximumReadSourceEpisodes) throw Invalid();
        var table = Prefix + Tables[(int)kind];
        await using var command = commandFactory($"""
            SELECT CAST(r.payload AS text),(SELECT max(revision) FROM {Prefix}revisions h
                WHERE h.partition_key=r.partition_key AND h.kind=@kind AND h.record_id=r.id)
            FROM {table} r WHERE r.partition_key=@partition AND r.id=@id
                AND NOT EXISTS(SELECT 1 FROM {Prefix}partition_migration_rows q WHERE q.table_name=@table AND q.record_id=CAST(r.id AS text) AND q.disposition='Quarantine')
            """);
        Add(command, "partition", partition.StorageKey); Add(command, "kind", (int)kind); Add(command, "table", table);
        Add(command, "id", postgres ? id : id.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token);
        Snapshot? snapshot = null;
        if (await reader.ReadAsync(token) && !reader.IsDBNull(1))
        {
            using var document = JsonDocument.Parse(reader.GetString(0)); var payload = document.RootElement;
            if (payload.GetProperty("id").GetGuid() == id && payload.GetProperty("partition").Deserialize<MemoryPartition>(Json) == partition)
                snapshot = new(new(partition, kind, id, reader.GetInt64(1)), payload.Clone());
        }
        cache[key] = snapshot; return snapshot;
    }

    private async Task<KnowledgeTransferPackage?> PackageAsync(Guid id, CancellationToken token)
    {
        if (id == Guid.Empty) return null;
        if (packages.TryGetValue(id, out var existing)) return existing;
        if (++reads > MemoryProvenance.MaximumReadSourceEpisodes) throw Invalid();
        await using var command = commandFactory($"SELECT CAST(payload AS text) FROM {Prefix}transfers WHERE id=@id AND NOT EXISTS(SELECT 1 FROM {Prefix}partition_migration_rows WHERE table_name=@table AND record_id=CAST(@id AS text) AND disposition='Quarantine')");
        Add(command, "id", postgres ? id : id.ToString("D")); Add(command, "table", Prefix + "transfers");
        var payload = (string?)await command.ExecuteScalarAsync(token);
        return packages[id] = payload is null ? null : JsonSerializer.Deserialize<KnowledgeTransferPackage>(payload, Json);
    }

    private bool ValidAudience(KnowledgeTransferPackage p)
    {
        if (p.Id == Guid.Empty || p.Items is null || p.Items.Count > 32 || p.SourceNamespaces is null || p.SourceNamespaces.Count is < 1 or > 16 ||
            string.IsNullOrWhiteSpace(p.ApprovedByEmployeeId) || p.ApprovedAt is null || p.ApprovedAt > asOf || p.SourceEmployeeId == p.TargetEmployeeId ||
            !Enum.IsDefined(p.DebriefSensitivity) || !ValidNamespace(p.TargetNamespace, p.TargetEmployeeId, p.TenantId, false)) return false;
        foreach (var source in p.SourceNamespaces)
            if (!ValidNamespace(source, p.SourceEmployeeId, p.TenantId, true) || source.Partition.ApplicationId != p.TargetNamespace.Partition.ApplicationId ||
                (source.Audience == MemoryAudienceType.UserRelationship && source.Partition.UserId != p.TargetNamespace.Partition.UserId)) return false;
        return p.Items.All(x => x is not null && Enum.IsDefined(x.Sensitivity) && Enum.IsDefined(x.Trust) &&
            p.SourceNamespaces.Any(n => n.Partition == x.SourcePartition));
    }

    private static bool ValidNamespace(MemoryNamespace ns, string employee, string tenant, bool allowOrganization)
    {
        if (ns is null || ns.Partition is null || string.IsNullOrWhiteSpace(employee)) return false;
        var app = ns.Partition.ApplicationId;
        return ns.Audience switch
        {
            MemoryAudienceType.Employee => ns.Scope == MemoryScope.Agent && ns.AudienceId == employee &&
                ns.Partition == new MemoryPartition(tenant, app, employee, CustomNamespace: $"employee:{employee}"),
            MemoryAudienceType.UserRelationship => !string.IsNullOrWhiteSpace(ns.AudienceId) && ns.Scope == MemoryScope.User &&
                ns.Partition == new MemoryPartition(tenant, app, employee, ns.AudienceId, CustomNamespace: $"relationship:{employee}:{ns.AudienceId}"),
            MemoryAudienceType.Team or MemoryAudienceType.Role => allowOrganization && ns.Scope == MemoryScope.Custom &&
                MemorySharedAudiences.IsCanonical(ns.Partition) && ns.Partition.TenantId == tenant &&
                ns.Partition.CustomNamespace == (ns.Audience == MemoryAudienceType.Team ? "team:" : "role:") + ns.AudienceId,
            MemoryAudienceType.Organization => allowOrganization && ns.Scope == MemoryScope.Tenant && ns.AudienceId == tenant &&
                ns.Partition == new MemoryPartition(tenant, app, CustomNamespace: "organization"),
            _ => false
        };
    }

    private static MemoryPartition[] SharedRestrictions(KnowledgeTransferPackage package, IEnumerable<Snapshot> records) =>
        MemorySharedAudiences.Merge(package.SourceNamespaces.Where(x => x.Audience is MemoryAudienceType.Team or MemoryAudienceType.Role)
            .Select(x => x.Partition).Concat(MemorySharedAudiences.FromSources(records.Where(x => x.Reference.Kind == MemoryRecordKind.Episode)
                .Select(x => x.Payload.Deserialize<MemoryEpisode>(Json) ?? throw Invalid()))));

    private static void Add(DbCommand command, string name, object value)
    { var parameter = command.CreateParameter(); parameter.ParameterName = name; parameter.Value = value; command.Parameters.Add(parameter); }
    private static InvalidOperationException Invalid() => new("memory_transfer_evidence_invalid");
}
