# CSweet.Memory

## Core review state (unpublished 0.2.0)

`MemoryBlock.Confirmation` now carries explicit review state. Both stores exclude rejected and
unknown states from search, and include pending blocks only for explicit pending searches. Safe
export/transfer projection excludes pending/rejected blocks; transfer evidence also validates the
state directly. Missing legacy state remains `NotRequired`, with existing source and classification
checks unchanged. This does not approve unverified legacy evidence.

C-Sweet's authenticated core-review workflow owns confirm/reject/correction, current authority,
revision/evidence checks and atomic review receipts/audit. Corrections preserve block identity,
increment its content revision and retain all contributing sources plus the new human source.
Raw store writes/exports remain trusted administrative interfaces, not human authorization APIs.
Deploy the matching rebuilt draft packages together: older readers ignore this new state and are
not compatible with reviewed core blocks. No live mixed-version rollback is supported; quiesce
writers and use the documented snapshot/restore release procedure. No packages are published yet.

## Transfer evidence (unpublished 0.2.0)

`IMemoryTransferEvidenceStore.CaptureTransferEvidenceAsync` validates the selected content,
classification, citations and complete source closure before approval. `MemoryTransferEvidence`
pins each episode/entity/claim/edge/block/procedure to its database revision and binds the approved
package fields. `MemoryEngine` rechecks source access and that evidence before applying a transfer.
The new episode uses canonical LF rendering and a `sha256-v2` fingerprint including the manifest;
ordinary episode fingerprints retain their existing v1 format.

Both stores verify applied-package identity, target audience, exact content, all source revisions,
current confirmation/validity and recursive transfer dependencies during source reads, search and
export projection. Changed, missing, expired, rejected or unverified contributors withhold the copy
and derivatives. Restoring a source's old values does not restore its approved revision. Employee
handoffs and same-user relationship handoffs are supported within one tenant/application; a
relationship cannot be widened into employee memory or another user's relationship. Organization
sources may contribute to these targets. Custom, role, team and case transfers require future policy.

Validation is bounded to 32 items, 16 namespaces, 512 distinct manifest records, three transfer
hops and 8,192 source/package reads per resolver. Reads are cached but currently issued per record;
production performance evaluation remains open. Store verification is transient and deliberately
not serialized: raw JSON exports must be re-resolved by a trusted store before recall projection.
Legacy transfer episodes without manifests are withheld and require reviewed migration.

Initial holds propagate to the copied episode. `DeleteScopeAsync` also refuses unresolved/legacy
transfers or changed evidence with `memory_transfer_retention_review_required`. This conservative
barrier prevents a later source hold from being bypassed by deleting its copy. PostgreSQL blocks
writers to the six contributing record tables while checking; SQLite uses its reserved writer.
A current, unheld transfer scope can be deleted. Reviewed hold release and full suppression/deletion
propagation remain unfinished.

These are trusted library/storage primitives. The C-Sweet agent broker rejects caller-supplied
transfer manifests. The platform's `AgentMemoryTransferService` supplies authenticated human
review over both employees, bounded selection, atomic application/revocation, immutable receipts,
audit and paginated recovery. Library application itself writes the episode and applied package
separately unless the caller supplies an encompassing PostgreSQL transaction; the platform does
so. Incomplete standalone applications remain withheld. Generic proposal/transfer enrichment,
other review types and full lifecycle/release gates remain open; this does not complete P0.
See C-Sweet's `docs/implementation/features/agent-memory-hardening.md` for the current register.

## Source fingerprints and graph evidence (unpublished 0.2.0)

New episode writes receive a server-computed `SourceFingerprint`, ignoring caller-supplied
values. `MemorySourceIntegrity` hashes a fixed v1 shape containing the exact partition,
scope, content/type/checksum, source identity/author, UTC occurrence time, sorted metadata
and operational references. Recording time, sensitivity, expiry and legal holds remain
independently enforced policy state. The additive `source-fingerprints-v1` upgrade installs
database guards against changing or removing an existing fingerprint on an episode ID.
Ordinary retrieval and derived-record projection withhold evidence with a missing or
mismatched fingerprint. Raw administrative exports and history retain it for inspection.

**Legacy evidence is not automatically resealed.** Deploy matching current draft binaries
together and expect unverified old episodes and their derivatives to be absent from recall
until a reviewed migration establishes their evidence. That review/backfill workflow remains
open. Old direct writers cannot establish new verified evidence. This fingerprint detects
changes to stored evidence; it does not authenticate an author, track later edits to a live
conversation, prevent privileged database bypass, or solve generic ingestion replay.

Both stores use `MemoryGraphTraversal` to validate each edge and endpoint's contributors
before traversing. Results carry every source on the selected path, its maximum sensitivity,
minimum trust/confidence and intersected validity. A separate less-restricted path can remain
eligible; paths are never combined to conceal a restricted bridge. Selection is deterministic,
preferring lower sensitivity and then fewer hops, rather than optimizing all possible paths.
Traversal reads at most 32 roots and 512 edges in one exact partition, follows at most three
hops and deduplicates states by endpoint, depth, sensitivity and trust. Full path evidence is
bounded to 1,155 IDs and rechecked before return. The existing 8,192 distinct-source read
ceiling still applies; overflow withholds dependent results. These bounds can reduce recall
on large graphs; production-scale quality/latency evaluation remains a release gate.

## Database revision history (unpublished 0.2.0)

SQLite and PostgreSQL now preserve snapshots for episodes, entities, claims, edges, core
blocks, procedures and embeddings. The additive `revision-history-v1` upgrade runs after
canonical partition migration, with writer serialization and one transaction for baseline
snapshots, triggers and the migration receipt. Existing records get a `Baseline` snapshot
at upgrade time; this does not reconstruct earlier states or invent historical timestamps.

Triggers record inserts, changed payloads and deletions atomically with the original write,
including writes by older direct database clients. Unchanged payloads and idempotent inserts
do not add revisions. Historical rows cannot be updated. Block upserts retain the existing
block ID, and database triggers reject primary-ID replacement with
`memory_record_identity_immutable`. All source/classification fields remain in each snapshot.

`IMemoryRevisionReader.ReadRevisionsAsync` is a trusted administrative API for one exact
partition, record kind and ID. It returns a revision cursor, operation, database recording
time and full JSON snapshot. Pages accept 1–200 records and return at most 1 MiB of payload;
a larger single snapshot fails with `memory_revision_page_limit` rather than truncating or
skipping evidence. Quarantined identities cannot be read through this interface. History
may include sensitive, expired, rejected or deleted content: authorize inspection separately
from ordinary recall. Neither the agent broker nor ordinary exports expose this interface.

Revisions describe recorded database changes, not a complete bitemporal/as-of model or a
global commit-ordered event feed. They do not yet contain authenticated reviewer receipts;
review transitions, expected-revision checks and operator UI belong to the next integration.
Transfer-package revisions remain part of that reviewed workflow. History is immutable to
ordinary updates, not tamper-proof against a privileged database administrator.

`DeleteScopeAsync` checks current legal holds and removes history in the same transaction as
the scope's live records. A hold blocks the whole purge. This does not yet provide source-level
forget/suppress or transfer/hold propagation. Deploy matching current draft store/platform
binaries together: older direct store binaries do not purge the history table, and old block
writers that replace IDs are rejected. Older broker clients remain subject to current server
policy. Stop writers and take a backup before upgrading; binary downgrade alone is not a safe
rollback. Use the pre-upgrade backup in a stopped environment and separately reconcile later
writes. The migration's baseline scan and extra write/storage cost still need deployment-scale
rehearsal; no production migration or package publication has occurred.

## Additional derivative evidence (unpublished 0.2.0)

`MemoryClaim`, `MemoryEdge` and `ProceduralMemory` now also carry `SourceEpisodeIds`.
These additive JSON fields identify up to 128 additional contributing episodes in the
exact same partition; the existing `EpisodeId` remains mandatory. Empty lists preserve
single-source compatibility. Inserts reject null, oversized or empty-GUID lists, and
idempotent replay rejects changed evidence instead of replacing the original record.

SQLite/PostgreSQL retrieval, `MemoryReadProjection.Create`, transfer previews and platform
broker claim reads validate all contributors and inherit their highest current sensitivity.
Missing, foreign, future or expired evidence excludes the derivative. Candidate evidence
combines the direct episode, additional sources and contributing entities (at most 385 IDs,
within the existing 8,192-source read budget). Raw store exports and claim reads remain
trusted storage APIs; applications must apply the provenance/projection rules and authorization.
The original `MemoryProvenance.ResolveClaim`/`ResolveEdge` overloads with no contributor
dictionary withhold records with additional sources; callers with such evidence must use
the new dictionary overloads. The `ResolveClaimReferences`/`ResolveEdgeReferences` helpers
check only direct references and require a subsequent contributor check before returning data.

Platform paired extraction captures both messages before inference and writes their IDs to
every derived entity, claim, edge and procedure. Accepted output uses envelope version 3;
version 2 single-message output remains replayable, while version 2 paired output is withheld
without re-inference. This does not backfill previously completed paired extractions. Matching
current draft binaries must be deployed together; older binaries do not enforce the new fields.

## Entity and core-block source lineage (unpublished 0.2.0)

`MemoryEntity.SourceEpisodeIds` and `MemoryBlock.SourceEpisodeIds` identify contributing
episodes in the record's exact partition. Both enrichment producers attach the captured
episode to extracted entities. Trusted block producers should supply every contributing
episode; raw agent entity/block writes remain denied by the platform broker.

SQLite and PostgreSQL atomically union these IDs during updates, including entity updates
by application key. An older JSON client omitting the field cannot erase existing links.
Entity and block sensitivity cannot be lowered by ordinary upserts. The union is limited
to 128 episodes per record; an overflowing update fails with `memory_lineage_limit` and
leaves the previous record intact. Do not truncate evidence to work around this limit;
splitting or reviewing a saturated record needs a trusted workflow that is not yet supplied.

Entity lookups, lexical/graph/core retrieval, projected exports and transfer previews
recheck linked episodes. Missing, foreign-partition, future or expired contributors exclude
the dependent record; surviving records inherit the highest current source sensitivity.
Candidate/transfer evidence includes contributing entity/block episode IDs. Retrieval batches
up to 8,192 distinct source reads (256 per SQLite query); exceeding that bound withholds
source-dependent candidates. Raw `ExportAsync` remains an administrative API; use
`MemoryReadProjection.Create` after namespace authorization for content-level filtering.

The JSON fields are additive and need no schema migration beyond the existing canonical
partition migration. Deploy matching current 0.2.0 packages together: older store/server
binaries do not enforce these links. This draft version has not been published.

Dependency lists are accumulated lineage; the revision snapshots described above preserve
subsequent database changes. An absent/empty
list keeps the existing rules for legacy or explicitly authored classified records; it does
not prove provenance. Unclassified legacy entities/blocks remain Restricted. Reviewed legacy
classification, completed legacy paired-output review, transfer lineage, audience promotion, source-content
fingerprint validation during recall, and suppression/hold propagation remain separate work.
Source checks during a read do not establish an authorization fence at provider dispatch.

`CSweet.Memory` is a first-party temporal memory framework for .NET agents. It stores immutable source episodes, derives provenance-bearing claims and relationships, retrieves context with hybrid rank fusion, and integrates with Microsoft Agent Framework through `AIContextProvider`.

The framework owns its complete memory pipeline and does not wrap or depend on another agent-memory product. SQLite provides embedded local storage and PostgreSQL provides production storage. Both implement the same temporal property-graph model without requiring a graph database.

## Employee memory model

C-Sweet operational records remain authoritative for employees, roles, teams, assignments, objectives, tasks, cases, and approvals. Memory stores evidence-backed observations and experience about those records through stable `MemoryOperationalReference` and entity application keys; it is not a competing HR or workflow database.

Employee memory distinguishes facts, observations, decisions, commitments, outcomes, handoffs, feedback, failures, demonstrated skills, and open questions. Retrieval can combine authorized organization, team, role, employee, user-relationship, case, and conversation namespaces. Authorization is evaluated before each namespace is searched.

The engine denies reads and writes by default and redacts content above the caller's explicit `memory.maxSensitivity` attribute. Applications must register an `IMemoryScopeAuthorizer`. `DelegatedMemoryScopeAuthorizer` is intended only when a trusted downstream broker performs the definitive policy check; `AllowAllMemoryScopeAuthorizer` is for isolated tests and local development.

```csharp
services.AddAgentMemory(options =>
    {
        options.DefaultScope = MemoryScope.User;
        options.ContextTokenBudget = 2_000;
    })
    .UseIntegratedSqlite("memory.db")
    .UseOptionalEnrichment();

services.AddAgentMemoryContextProvider();
```

## Knowledge transfer

Replacing an employee is an approval-gated workflow rather than a bulk copy of private history:

1. `PrepareKnowledgeTransferAsync` builds an inspectable debrief package from authorized source namespaces.
2. Sensitivity, layer, selected-memory, and token-budget filters are applied before the package is persisted.
3. `ApproveKnowledgeTransferAsync` records an approval or rejection and its reviewer.
4. `ApplyKnowledgeTransferAsync` creates one provenance-bearing episode in the replacement employee's namespace and queues normal enrichment.

Raw episodic history is excluded by default. A transfer normally contains active semantic knowledge, curated core memory, and confirmed procedures. Episodic history must be selected explicitly. Restricted content cannot be included when the transfer's maximum sensitivity is lower, and the target must be a canonical employee or same-user relationship namespace matching the replacement employee.

```csharp
var package = await memory.PrepareKnowledgeTransferAsync(new(
    SourceEmployeeId: "employee-old",
    TargetEmployeeId: "employee-new",
    SourceNamespaces: [EmployeeMemoryNamespaces.Employee(tenantId, "employee-old")],
    TargetNamespace: EmployeeMemoryNamespaces.Employee(tenantId, "employee-new"),
    Access: managerAccess,
    Debrief: "Open work, key decisions, recurring risks, and important relationships."));

package = await memory.ApproveKnowledgeTransferAsync(
    new(package.Id, managerAccess, Approved: true));
package = await memory.ApplyKnowledgeTransferAsync(
    new(package.Id, managerAccess));
```

The vendor-neutral packages target .NET 8 or later. `CSweet.Memory.Broker` 0.2.0 targets .NET 10 and pins `CSweet.Agent.SDK` 3.30.0, including when sibling project references are disabled. All six memory packages are version 0.2.0. All packages are licensed under Apache-2.0.

## Creating NuGet packages

Run the batch file from the repository root to restore published dependencies, run the test suite, and create all six packages in a versioned directory such as `artifacts\packages\0.2.0`:

```bat
Create-NuGetPackages.bat
```

Pass a version and optional output root to override the repository defaults. The version directory is appended automatically, so this example writes to `C:\packages\csweet-memory\0.2.0`:

```bat
Create-NuGetPackages.bat 0.2.0 C:\packages\csweet-memory
```

## Provenance and classification (0.2.0)

`MemoryProvenance` resolves source sensitivity and exact partition references for claims and edges. Both database stores apply these checks during retrieval; procedure and vector results also require an unexpired source episode that has already occurred. `IMemorySourceReader` provides bounded episode/entity reads for a previously authorized partition. It does not grant access.

`MemoryEntity.Sensitivity` and `MemoryBlock.Sensitivity` are additive init properties with a conservative `Restricted` default, including when older JSON omits them. Trusted enrichment assigns the source classification to new entities; entity upserts retain the highest classification and the stored row ID. Existing unclassified entities and blocks need a separately reviewed classification migration before lower-clearance recall can use them. Do not mass-relabel them as Internal. Claims inherit the greatest source/entity/declared sensitivity, and confirmed procedures still inherit their source's sensitivity.

`MemoryReadProjection.Create` filters an already-authorized export by sensitivity, source availability, validity, confirmation, and exact partition, including dependent embeddings. `TransferItems` accepts that projection; transfer application preserves the greatest item/debrief classification. Raw `IMemoryStore` exports remain trusted storage APIs: apply authorization and projection before exposing them. The broker integration in C-Sweet does so. Its write policy normalizes agent episodes, accepts claims/procedures only as pending inferences, and denies raw entity/block/edge writes, confirmation, supersession, and transfer upserts until trusted review workflows exist. Library APIs do not independently grant these operations through the platform.

The shared `ProvenanceStoreTests` always run with SQLite. To also execute them against PostgreSQL, set `CSWEET_MEMORY_TEST_POSTGRES` to a connection string for a dedicated disposable test database, then run `dotnet test CSweet.Memory.slnx -p:UseLocalCSweetAgentSdk=false`. Tests initialize the memory schema and remove only their randomly generated partitions. Without this environment variable, PostgreSQL cases are not included; a SQLite-only success is not PostgreSQL validation.

## Caller-owned PostgreSQL transactions (0.2.0)

`PostgreSqlMemoryStore(NpgsqlTransaction)` enlists every operation, including scope deletion, in an existing transaction. Initialize the memory schema using a regular connection-string store before beginning that transaction. The caller owns the transaction and connection: disposing the enlisted store does not commit, roll back, or dispose either. Do not share an enlisted store across concurrent operations or use it after the transaction ends.

The C-Sweet enrichment worker uses this overload to lock its outbox job, verify ownership, apply a previously persisted extraction, and complete the job in one transaction. This requires the outbox and memory tables to reside in the same PostgreSQL database. The library's standalone enrichment queue remains an in-process queue; this overload alone does not make that queue durable. `PostgreSqlTransactionTests` checks transaction visibility, rollback, commit, and ownership, and is explicitly skipped when the test connection string is absent.

## Indexed lexical retrieval and valid time (0.2.0)

`MemoryLexicalQuery.Parse` treats input as literal Unicode words and identifiers, removes a small English conversational stopword list, and builds quoted OR terms. It reads at most 1,024 input characters and 12 distinct terms. Empty, punctuation-only and stopword-only queries return no candidates, including core/vector channels. Search limits are clamped to 1–100 per lexical channel. This is literal token search with no stemming, translation, fuzzy matching, or implied support for language-specific word segmentation.

Both stores search episode text, claim predicates/values plus subject/object names and aliases, and procedure names/content/applicability. Exact entity or procedure names and contiguous claim/procedure phrases receive ordering preference. Metadata outside these fields is not searchable content. PostgreSQL uses the `simple` text-search configuration and GIN indexes; SQLite uses FTS5. Joined source/entity reads avoid one additional database call per result. Semantic content renders object names instead of opaque IDs.

`InitializeAsync` applies the additive `indexed-search-v1` upgrade to populated databases. PostgreSQL takes a transaction-scoped advisory lock, adds generated searchable columns/indexes, and records the migration atomically. SQLite transactionally creates FTS tables, backfills them once, installs insert/update/delete triggers, and records the migration. Existing writers continue to maintain these indexes through generated columns/triggers. Initialization must run before opening a caller-owned PostgreSQL transaction. Initial index creation can lock large tables; rehearse against representative data before production rollout. This upgrade does not migrate namespace keys or change classification.

Graph fallback considers up to 32 matching roots and a deterministic sample of 512 currently valid edges, traverses at most three hops, and deduplicates returned edges. Future/expired links cannot bridge into other facts. Core reads consider at most 100 blocks. The existing in-process vector fallback scores at most 1,024 eligible episode embeddings; it is a bounded sample, not an approximate-nearest-neighbor index or a guarantee of global top-k recall. These explicit caps trade recall for bounded application work; database plans and large-corpus latency still require measurement.

`AsOf` is valid time: start boundaries are inclusive and end/expiry boundaries exclusive. Late-recorded evidence can answer an earlier valid-time request. It is not a reconstruction of what the system knew at that time. Confirmation/classification/suppression use current state. Core blocks updated after `AsOf` are excluded; prior block revisions are not reconstructed.

Trusted callers can use `IMemorySuppressionStore.SuppressEpisodeAsync` after authorizing and auditing the action. It atomically marks all captures of the same source type/ID within the partition as suppressed and stores content-free source/episode tombstones. Ordinary recall, derivative resolution, safe export projection and transferred copies then withhold the evidence, including historical `AsOf` queries. Administrative exports and revision history retain the evidence and any legal hold. `DeleteScopeAsync` preserves the tombstones; inserts with a suppressed source identity or known episode ID are rejected even after purge/restart. Suppression is permanent through this API and is not physical erasure or a legal-hold release.

The additive `source-suppression-v1` store upgrade installs tombstones and write guards transactionally after revision history. PostgreSQL uses a conservative episode-table writer barrier; SQLite reserves its writer. PostgreSQL callers may enlist suppression in their own transaction for authorization and audit. The lifecycle flag is not part of the immutable evidence fingerprint. Stop old reader binaries before enabling suppression: they do not enforce the new flag. Only callers using the current library/projection enforce suppression on reads; database administrators remain privileged. `SuppressionTests` covers six channels, held evidence, replay, purge/restart, partition isolation and chained transfers. Full reviewed erasure, hold release and production-load verification remain integration responsibilities.

SQLite compares timestamp columns as UTC ticks through the connection-local deterministic `csweet_utc_ticks` function, before candidate limits. It handles round-trip ISO timestamps with explicit offsets or `Z`, including existing rows and later writes from older clients. Invalid or offset-free scalar timestamps fail their SQL temporal predicates; they are never treated as an absent expiry. New indexed timestamp writes use UTC. Original JSON payloads, offsets, fingerprints and revision history remain unchanged, so no data rewrite or schema migration is required.

PostgreSQL normalizes native timestamp parameters to UTC. Its native columns retain only microseconds, so retrieval compares the preserved payload timestamps using exact numeric epoch ticks in SQL before candidate limits. `PostgreSqlMemoryStore.TimeTicks` parses whole seconds and fractional ticks separately to avoid rounding the seventh digit or losing precision at large dates. Both stores preserve 100-nanosecond retrieval boundaries, including existing evidence. Do not downgrade to old reader binaries while relying on mixed-offset or sub-microsecond eligibility. Production-scale query-plan and latency evaluation of timestamp parsing on both stores remains a release gate.

`LexicalRetrievalTests`, `TemporalTraversalTests`, `TemporalOffsetTests` and `IndexedSearchMigrationTests` exercise natural-language recall, abstention, validity boundaries, offsets, traversal, populated upgrades, concurrent initialization, and restart on both stores. Offset fixtures cover lexical, semantic, graph, vector, procedural and core channels, source expiry, supersession, late recording, malformed scalar timestamps, legacy writes, and filtering before result limits. Precision fixtures include years 1, 1900, 2000, 2026 and 9999; independent derivative intervals; one-tick boundaries; and future/expired rows competing for one result. Set `CSWEET_MEMORY_EVAL_OUTPUT` to save the small deterministic corpus report. These timings are test observations, not production SLAs or model-answer evaluations. The CI workflow now supplies disposable PostgreSQL alongside SQLite.

## Replay conflicts and legal retention (0.2.0)

`IMemoryErasureStore` is a trusted administrative store capability on PostgreSQL and SQLite. `PreviewEpisodeErasureAsync` returns affected identities, an evidence token and any retention/lineage blocker. `EraseEpisodeAsync` recomputes the plan under writer locks, rejects changed evidence or holds, and atomically removes live payloads and all corresponding revision snapshots. It includes logical duplicate episodes, linked entities/claims/edges/blocks/procedures, embeddings, usage records, correction references, transfer packages and applied chains. Content-free receipts allow exact-token replay after restart. Episode/source identity fences and database write guards prevent old identities and structured references from being written back, including a fresh episode ID in another audience of the same tenant/application. Scope deletion does not remove these fences.

Preview and erasure use a conservative, bounded tenant inventory: at most 16,384 live/history snapshots, 32 MiB of payload and 128 closure passes. Exceeding a bound fails with `memory_erasure_scan_limit`; it never truncates an erasure plan. Historical references participate in dependency discovery. All transfer packages originating from an affected audience are included conservatively, and structured GUID references may widen the plan. Every affected audience needs host authorization. Holds on contributors, copied holds, missing lineage and unlinked legacy derivatives can block the entire operation. Releasing an original hold does not release a transferred hold. Unrelated changes in the same tenant can stale the preview; the inventory and global PostgreSQL writer barriers still require representative load validation.

The automatic additive upgrade is `source-erasure-v1`; preserve `*_erased_records`, `*_erased_sources` and `*_erasure_receipts` through application rollback. Reinitialization retains existing data/history and resumes a failed installation transaction. No automatic downgrade is supported. The API's deletion guarantee concerns active database rows and indexes, not backups, transaction logs, replicas or content already delivered to another system. PostgreSQL callers may enlist erasure with their platform transaction. Hosts must coordinate source exclusions, queued extraction/work payloads, audit and runtime/cache invalidation before exposing a user-facing forget workflow. The platform broker does not expose this capability.

Claim, edge, procedure and embedding inserts are create-only by ID. Repeating the same payload returns `Created = false`; the original `RecordedAt` is preserved and differences in that field alone do not conflict. Any other changed field, partition, source, or current review state throws `InvalidOperationException("memory_write_conflict")`. Replaying an old pending claim cannot overwrite a later confirmation. This does not provide a durable ingestion queue or atomic multi-record transfer application.

`DeleteScopeAsync` refuses the entire operation if any source episode in the scope has a legal hold, using `memory_legal_hold_prevents_deletion`. PostgreSQL uses a table writer barrier inside the deletion transaction; SQLite reserves its writer before checking. This conservative PostgreSQL barrier can temporarily block writes in other partitions. Holds therefore protect evidence from this trusted raw-store API, but do not implement recall suppression, reviewed hold release, derivative/transfer retention policy, or user-facing forgetting. The platform broker continues to deny raw deletion.

## Canonical partition storage and reviewed migration (0.2.0)

`MemoryPartition.StorageKey` is the database identity. It is `mp2:` followed by lowercase SHA-256 of the UTF-8 domain `CSweet.Memory.Partition/v2` plus a NUL byte, then all six fields in declared order. Each field has a four-byte signed big-endian UTF-8 byte length (`-1` for null), followed by its bytes. Empty, null, whitespace, separators, Unicode and field positions remain distinct. Invalid UTF-16 is rejected. `MemoryPartition.Key` remains the legacy slash-delimited wire/display value for source compatibility; never use it for storage or authorization. Namespace deduplication uses `StorageKey`.

Fresh stores initialize canonical storage automatically. A populated legacy store fails closed with `memory_partition_migration_required`. It does not silently merge or hide old data behind a successful empty recall. `IMemoryPartitionMigration` supplies a content-free inventory and an atomic apply operation bound to its fingerprint. Inspection prepares additive schema/index metadata, but does not move records. Stop application/worker writers and take a verified database backup before inspecting a production copy; use an offline rehearsal first.

The offline utility is included in the solution and is not a NuGet package. Set `CSWEET_MEMORY_MIGRATION_CONNECTION` locally to the PostgreSQL connection string or SQLite file path. Do not put credentials in command arguments or commit them. From the repository root:

```powershell
dotnet run --project tools/CSweet.Memory.Migrate --configuration Release -- inspect postgres artifacts/partition-review.json
# Review every mapping and quarantine reason in that file before applying.
dotnet run --project tools/CSweet.Memory.Migrate --configuration Release -- apply postgres artifacts/partition-review.json
```

Use `sqlite` in place of `postgres` for SQLite. Inspection refuses to overwrite an existing plan file. The plan contains record IDs, original/destination keys, structured namespace fields, dispositions and payload hashes, never source content. It is still administrative metadata and needs restricted handling. Applying an edited/stale plan fails; re-inspect to a new file and review again. The library API recomputes the fingerprint under writer locks before moving data. Cancellation/failure rolls back row changes, receipts, guards and completion together; replaying the same applied plan is safe. Completed inspections return the retained original migration plan.

Migration preserves record IDs, namespace fields, application segments, source content, references, sensitivity and holds. Installation-private content stays in that installation's application segment. A legacy flattened key with multiple identities, unverifiable ID/classification/SQLite indexed fields, or uncertain ownership quarantines the affected key group. Different old keys that would merge into one canonical scope also require review. Quarantine changes only indexed routing and retains original payloads. All existing transfer packages are quarantined for separate approval review, so an old debrief cannot bypass unavailable sources. Ordinary reads hide quarantined claims/transfers, and guarded writes cannot revive their IDs. The migration does not release quarantine, rewrite ownership, lower sensitivity or implement the later authenticated review workflow.

PostgreSQL uses an advisory lock plus writer locks on the memory tables; SQLite uses an immediate writer transaction. Migration receipts and the original plan are retained in `*_partition_migration_rows` and `*_partition_migration_runs`. New write guards require canonical keys and matching serialized `partition.storageKey`; older direct-store writers fail rather than recreate flattened partitions. The offline inventory is bounded to 100,000 records, including transfers; larger databases stop with `memory_migration_inventory_limit` and require a separately rehearsed bulk migration. Large payloads and lock duration must be assessed on the backup before rollout.

Supported compatibility: older JSON clients can call the **current platform server**, which reconstructs canonical identity from the six fields. Current stores read canonical data only. Older servers/direct-store packages are not supported against an upgraded database. Do not drop guards, receipts or the completion marker to run them. Recover an application release with canonical-aware binaries; if database restoration is necessary, restore a complete backup with all writers stopped, account for writes after the backup, and rehearse migration again before reopening access. There is no automatic downgrade or automatic quarantine release. This storage migration does not align the platform's `csweet` application segment with existing agent installation segments; that remains a separate integration change.
