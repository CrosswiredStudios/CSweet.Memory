using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSweet.Memory;

/// <summary>An exact source record and the database revision approved for copying.</summary>
public sealed record MemoryTransferRecord(MemoryPartition Partition, MemoryRecordKind Kind, Guid Id, long Revision);

/// <summary>Approved evidence, not an access grant. Only trusted transfer orchestration may create it.</summary>
public sealed record MemoryTransferEvidence(Guid PackageId, string PackageFingerprint, IReadOnlyList<MemoryTransferRecord> Records, bool LegalHold = false)
{
    public const int MaximumRecords = 512;
    /// <summary>Verified shared restrictions inherited by this copy; never an access grant.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<MemoryPartition>? RequiredSharedPartitions { get; init; }
    /// <summary>Canonical approved content, independent of the host's newline convention.</summary>
    public static string RenderContent(KnowledgeTransferPackage package)
    {
        var builder = new StringBuilder();
        builder.Append($"Knowledge transfer from employee {package.SourceEmployeeId} to employee {package.TargetEmployeeId}.\n");
        if (!string.IsNullOrWhiteSpace(package.Debrief)) builder.Append($"Debrief: {package.Debrief}\n");
        foreach (var item in package.Items)
            builder.Append("- [").Append(item.Kind).Append("] ").Append(item.Content).Append(" [").Append(item.Citation).Append("]\n");
        return builder.ToString();
    }

    public static string Fingerprint(KnowledgeTransferPackage package) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, package.Id, package.TenantId, package.SourceEmployeeId, package.TargetEmployeeId,
            package.SourceNamespaces, package.TargetNamespace, package.Debrief, package.Items, package.DebriefSensitivity,
            package.CreatedAt, package.CreatedByEmployeeId, package.ApprovedByEmployeeId, package.ApprovedAt, package.ApprovalNotes
        }))).ToLowerInvariant();
}

/// <summary>Trusted store capability. Captures validated source revisions; it does not authorize approval or application.</summary>
public interface IMemoryTransferEvidenceStore
{
    Task<MemoryTransferEvidence> CaptureTransferEvidenceAsync(KnowledgeTransferPackage approvedPackage, CancellationToken cancellationToken = default);
}
