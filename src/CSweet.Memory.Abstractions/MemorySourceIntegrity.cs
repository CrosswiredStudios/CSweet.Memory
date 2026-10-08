using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace CSweet.Memory;

/// <summary>Evidence integrity, not source authority or authorization. Policy/retention fields remain independently enforced.</summary>
public static class MemorySourceIntegrity
{
    public static MemoryEpisode Seal(MemoryEpisode episode) => episode with { SourceFingerprint = Fingerprint(episode) };

    public static bool IsVerified(MemoryEpisode episode)
    {
        if (episode.SourceFingerprint is not { Length: 74 } fingerprint) return false;
        try { return string.Equals(fingerprint, Fingerprint(episode), StringComparison.Ordinal); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException) { return false; }
    }

    private static string Fingerprint(MemoryEpisode episode)
    {
        // Freeze the v1 shape explicitly. Recording time, sensitivity, expiry and holds are
        // policy/retention state; changes to them do not rewrite the underlying evidence.
        var source = episode.Source;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Domain = "CSweet.Memory.Evidence/v1", Partition = episode.Partition.StorageKey,
            Scope = (int)episode.Scope, episode.Content, episode.ContentType, episode.Checksum,
            Source = new { source.Type, source.Id, source.Author },
            OccurredAt = episode.OccurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            Metadata = episode.Metadata?.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { x.Key, x.Value }).ToArray(),
            References = episode.OperationalReferences?.Select(x => new { x.Type, x.Id, x.Version }).ToArray()
        });
        if (episode.TransferEvidence is not null)
            return "sha256-v2:" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
                { EvidenceV1 = Convert.ToHexString(SHA256.HashData(payload)), episode.TransferEvidence }))).ToLowerInvariant();
        return "sha256-v1:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }
}
