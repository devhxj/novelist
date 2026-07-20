using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

/// <summary>
/// Stores reference-book sources. Registration deliberately stops before any
/// chapter parsing or material extraction; those operations belong to the
/// materialization worker.
/// </summary>
public sealed class SqliteReferenceAnchorService : IReferenceAnchorService
{
    private const long WorkspaceNovelId = 0;
    private const long MaxSourceBytes = 20L * 1024L * 1024L;
    private const string BuildVersion = "reference-materialization-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> SourceKinds = ["text", "markdown"];
    private static readonly HashSet<string> LicenseStatuses = ["user_provided", "licensed", "public_domain", "unknown"];
    private static readonly HashSet<string> VisibilityValues = new(ReferenceCorpusVisibilities.All, StringComparer.Ordinal);
    private static readonly HashSet<string> TrustValues = new(ReferenceSourceTrustLevels.All, StringComparer.Ordinal);

    private readonly AppInitializationOptions _options;
    private readonly INovelService _novels;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceAnchorService(
        AppInitializationOptions? options = null,
        INovelService? novels = null)
    {
        _options = options ?? new AppInitializationOptions();
        _novels = novels ?? new FileSystemNovelService(_options);
    }

    public async ValueTask<ReferenceAnchorPayload> RegisterMaterializationSourceAsync(
        CreateReferenceAnchorPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        await EnsureNovelExistsAsync(input.NovelId, cancellationToken);

        var title = Required(input.Title, nameof(input.Title), 200);
        var author = Optional(input.Author, nameof(input.Author), 200);
        var sourcePath = ValidateSourcePath(input.SourcePath);
        var sourceKind = ValidateValue(input.SourceKind, nameof(input.SourceKind), SourceKinds);
        var licenseStatus = ValidateValue(input.LicenseStatus, nameof(input.LicenseStatus), LicenseStatuses);
        var visibility = string.IsNullOrWhiteSpace(input.Visibility)
            ? ReferenceCorpusVisibilities.Private
            : ValidateValue(input.Visibility, nameof(input.Visibility), VisibilityValues);
        var sourceTrust = string.IsNullOrWhiteSpace(input.SourceTrust)
            ? ReferenceSourceTrustLevels.UserVerified
            : ValidateValue(input.SourceTrust, nameof(input.SourceTrust), TrustValues);
        var tags = NormalizeTags(input.UserTags);
        var source = await ReadSourceAsync(sourcePath, cancellationToken);
        var storedNovelId = visibility == ReferenceCorpusVisibilities.Workspace ? (long?)null : input.NovelId;

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            var existing = await FindByIdentityAsync(
                connection,
                transaction,
                storedNovelId,
                visibility,
                sourcePath,
                sourceKind,
                source.Hash,
                cancellationToken);
            if (existing is not null)
            {
                await transaction.CommitAsync(cancellationToken);
                return existing;
            }

            var now = DateTimeOffset.UtcNow;
            var anchorId = await InsertAnchorAsync(
                connection,
                transaction,
                storedNovelId,
                title,
                author,
                sourcePath,
                sourceKind,
                licenseStatus,
                visibility,
                sourceTrust,
                tags,
                source.Hash,
                now,
                cancellationToken);
            await UpsertMembershipAsync(
                connection,
                transaction,
                anchorId,
                storedNovelId,
                visibility,
                licenseStatus,
                sourceTrust,
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new ReferenceAnchorPayload(
                anchorId,
                storedNovelId ?? WorkspaceNovelId,
                title,
                author,
                sourcePath,
                sourceKind,
                licenseStatus,
                source.Hash,
                BuildVersion,
                ReferenceAnchorBuildStates.PendingSplit,
                now,
                now,
                visibility,
                sourceTrust,
                tags);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<IReadOnlyList<ReferenceAnchorPayload>> GetAnchorsAsync(
        long novelId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.anchor_id, a.novel_id, a.title, a.author, a.source_path,
                   a.source_kind, a.license_status, a.source_file_hash,
                   a.build_version,
                   CASE
                     WHEN state.active_generation_id IS NOT NULL THEN $ready
                     WHEN EXISTS (
                       SELECT 1 FROM reference_chapter_split_profiles profile
                       WHERE profile.anchor_id = a.anchor_id AND profile.status = $confirmed)
                       THEN $pending_materialization
                     ELSE $pending_split
                   END AS status,
                   a.created_at, a.updated_at, a.corpus_visibility,
                   a.source_trust, a.user_tags_json
            FROM reference_anchors a
            LEFT JOIN reference_anchor_materialization_state state
              ON state.anchor_id = a.anchor_id
            WHERE a.novel_id = $novel_id
               OR ((a.novel_id IS NULL OR a.novel_id = 0)
                   AND a.corpus_visibility = $workspace_visibility)
            ORDER BY a.created_at, a.anchor_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace_visibility", ReferenceCorpusVisibilities.Workspace);
        command.Parameters.AddWithValue("$ready", ReferenceAnchorBuildStates.Ready);
        command.Parameters.AddWithValue("$confirmed", ReferenceChapterSplitProfileStates.Confirmed);
        command.Parameters.AddWithValue("$pending_materialization", ReferenceAnchorBuildStates.PendingMaterialization);
        command.Parameters.AddWithValue("$pending_split", ReferenceAnchorBuildStates.PendingSplit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var anchors = new List<ReferenceAnchorPayload>();
        while (await reader.ReadAsync(cancellationToken))
        {
            anchors.Add(ReadAnchor(reader));
        }

        return anchors;
    }

    public async ValueTask DeleteAnchorAsync(
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        ValidateNovelId(novelId);
        ValidateAnchorId(anchorId);
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await EnsureAccessibleAsync(connection, transaction, novelId, anchorId, cancellationToken);
            await DeleteAnchorRowsAsync(connection, transaction, anchorId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask DeleteAnchorsAsync(
        DeleteReferenceAnchorsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        if (input.AnchorIds is null || input.AnchorIds.Count == 0)
        {
            throw new ArgumentException("At least one anchor id is required.", nameof(input));
        }

        foreach (var anchorId in input.AnchorIds)
        {
            ValidateAnchorId(anchorId);
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            foreach (var anchorId in input.AnchorIds.Distinct())
            {
                await EnsureAccessibleAsync(connection, transaction, input.NovelId, anchorId, cancellationToken);
                await DeleteAnchorRowsAsync(connection, transaction, anchorId, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceAnchorPayload> UpdateAnchorMetadataAsync(
        UpdateReferenceAnchorMetadataPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        ValidateAnchorId(input.AnchorId);
        var title = Required(input.Title, nameof(input.Title), 200);
        var author = Optional(input.Author, nameof(input.Author), 200);
        var licenseStatus = ValidateValue(input.LicenseStatus, nameof(input.LicenseStatus), LicenseStatuses);
        var visibility = ValidateValue(input.Visibility, nameof(input.Visibility), VisibilityValues);
        var sourceTrust = ValidateValue(input.SourceTrust, nameof(input.SourceTrust), TrustValues);
        var tags = NormalizeTags(input.UserTags);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await EnsureSchemaAsync(cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await EnsureAccessibleAsync(connection, transaction, input.NovelId, input.AnchorId, cancellationToken);
            var storedNovelId = visibility == ReferenceCorpusVisibilities.Workspace ? (long?)null : input.NovelId;
            var now = DateTimeOffset.UtcNow;
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE reference_anchors
                    SET novel_id = $novel_id,
                        title = $title,
                        author = $author,
                        license_status = $license_status,
                        corpus_visibility = $visibility,
                        source_trust = $source_trust,
                        user_tags_json = $user_tags_json,
                        updated_at = $updated_at
                    WHERE anchor_id = $anchor_id;
                    """;
                update.Parameters.AddWithValue("$novel_id", storedNovelId.HasValue ? storedNovelId.Value : DBNull.Value);
                update.Parameters.AddWithValue("$title", title);
                update.Parameters.AddWithValue("$author", author);
                update.Parameters.AddWithValue("$license_status", licenseStatus);
                update.Parameters.AddWithValue("$visibility", visibility);
                update.Parameters.AddWithValue("$source_trust", sourceTrust);
                update.Parameters.AddWithValue("$user_tags_json", JsonSerializer.Serialize(tags, JsonOptions));
                update.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
                update.Parameters.AddWithValue("$anchor_id", input.AnchorId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpsertMembershipAsync(
                connection,
                transaction,
                input.AnchorId,
                storedNovelId,
                visibility,
                licenseStatus,
                sourceTrust,
                now,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return await ReadAnchorByIdAsync(connection, input.AnchorId, cancellationToken)
                ?? throw new InvalidOperationException("Reference source disappeared after metadata update.");
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async ValueTask<string> EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        var path = await new ReferenceCorpusDatabasePathResolver(_options).ResolveAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var connection = await OpenConnectionAsync(path, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        return path;
    }

    private async ValueTask EnsureNovelExistsAsync(long novelId, CancellationToken cancellationToken)
    {
        var novels = await _novels.GetNovelsAsync(cancellationToken);
        if (!novels.Any(novel => novel.Id == novelId))
        {
            throw new ArgumentException("Novel does not exist.", nameof(novelId));
        }
    }

    private static async ValueTask<ReferenceAnchorPayload?> FindByIdentityAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? novelId,
        string visibility,
        string sourcePath,
        string sourceKind,
        string sourceHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT anchor_id, novel_id, title, author, source_path, source_kind,
                   license_status, source_file_hash, build_version, status,
                   created_at, updated_at, corpus_visibility, source_trust,
                   user_tags_json
            FROM reference_anchors
            WHERE source_path = $source_path
              AND source_kind = $source_kind
              AND source_file_hash = $source_hash
              AND corpus_visibility = $visibility
              AND (($has_novel = 1 AND novel_id = $novel_id)
                   OR ($has_novel = 0 AND (novel_id IS NULL OR novel_id = 0)));
            """;
        command.Parameters.AddWithValue("$source_path", sourcePath);
        command.Parameters.AddWithValue("$source_kind", sourceKind);
        command.Parameters.AddWithValue("$source_hash", sourceHash);
        command.Parameters.AddWithValue("$visibility", visibility);
        command.Parameters.AddWithValue("$has_novel", novelId.HasValue ? 1 : 0);
        command.Parameters.AddWithValue("$novel_id", novelId.HasValue ? novelId.Value : DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAnchor(reader) : null;
    }

    private static async ValueTask<long> InsertAnchorAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? novelId,
        string title,
        string author,
        string sourcePath,
        string sourceKind,
        string licenseStatus,
        string visibility,
        string sourceTrust,
        IReadOnlyList<string> tags,
        string sourceHash,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO reference_anchors (
              novel_id, title, author, source_path, source_kind, license_status,
              source_file_hash, build_version, status, created_at, updated_at,
              corpus_visibility, source_trust, user_tags_json)
            VALUES (
              $novel_id, $title, $author, $source_path, $source_kind, $license_status,
              $source_hash, $build_version, $status, $created_at, $updated_at,
              $visibility, $source_trust, $tags)
            RETURNING anchor_id;
            """;
        command.Parameters.AddWithValue("$novel_id", novelId.HasValue ? novelId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$author", author);
        command.Parameters.AddWithValue("$source_path", sourcePath);
        command.Parameters.AddWithValue("$source_kind", sourceKind);
        command.Parameters.AddWithValue("$license_status", licenseStatus);
        command.Parameters.AddWithValue("$source_hash", sourceHash);
        command.Parameters.AddWithValue("$build_version", BuildVersion);
        command.Parameters.AddWithValue("$status", ReferenceAnchorBuildStates.PendingSplit);
        command.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$updated_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$visibility", visibility);
        command.Parameters.AddWithValue("$source_trust", sourceTrust);
        command.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(tags, JsonOptions));
        return (long)(await command.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("SQLite did not return a reference anchor id."));
    }

    private static async ValueTask UpsertMembershipAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        long? novelId,
        string visibility,
        string licenseStatus,
        string sourceTrust,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var libraryId = visibility == ReferenceCorpusVisibilities.Workspace
            ? "global:workspace"
            : $"project:{novelId!.Value.ToString(CultureInfo.InvariantCulture)}:default";
        var scope = visibility == ReferenceCorpusVisibilities.Workspace ? "global" : "project";
        await using (var library = connection.CreateCommand())
        {
            library.Transaction = transaction;
            library.CommandText = """
                INSERT INTO reference_corpus_libraries (library_id, scope, novel_id, name, created_at)
                VALUES ($library_id, $scope, $novel_id, $name, $created_at)
                ON CONFLICT(library_id) DO UPDATE SET scope = excluded.scope,
                  novel_id = excluded.novel_id, name = excluded.name;
                """;
            library.Parameters.AddWithValue("$library_id", libraryId);
            library.Parameters.AddWithValue("$scope", scope);
            library.Parameters.AddWithValue("$novel_id", novelId.HasValue ? novelId.Value : DBNull.Value);
            library.Parameters.AddWithValue("$name", scope == "global" ? "Workspace corpus" : "Project corpus");
            library.Parameters.AddWithValue("$created_at", FormatTimestamp(now));
            await library.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var member = connection.CreateCommand())
        {
            member.Transaction = transaction;
            member.CommandText = """
                INSERT INTO reference_library_members
                  (library_id, anchor_id, enabled, source_quality, disabled_reason, dedup_group_id)
                VALUES ($library_id, $anchor_id, 1, $quality, NULL, $dedup)
                ON CONFLICT(library_id, anchor_id) DO UPDATE SET enabled = 1,
                  source_quality = excluded.source_quality, disabled_reason = NULL,
                  dedup_group_id = excluded.dedup_group_id;
                """;
            member.Parameters.AddWithValue("$library_id", libraryId);
            member.Parameters.AddWithValue("$anchor_id", anchorId);
            member.Parameters.AddWithValue("$quality", MapSourceQuality(sourceTrust));
            member.Parameters.AddWithValue("$dedup", $"anchor:{anchorId.ToString(CultureInfo.InvariantCulture)}");
            await member.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var binding = connection.CreateCommand())
        {
            binding.Transaction = transaction;
            binding.CommandText = "INSERT OR IGNORE INTO reference_session_library_binding (session_id, library_id) VALUES ($session_id, $library_id);";
            binding.Parameters.AddWithValue("$session_id", libraryId);
            binding.Parameters.AddWithValue("$library_id", libraryId);
            await binding.ExecuteNonQueryAsync(cancellationToken);
        }

        var license = MapLicenseStatus(licenseStatus);
        await using var gate = connection.CreateCommand();
        gate.Transaction = transaction;
        gate.CommandText = """
            INSERT INTO reference_source_license
              (anchor_id, license_state, authorization_evidence, reuse_policy,
               max_verbatim_ratio, cleared_for_insertion, reviewed_at)
            VALUES ($anchor_id, $state, $evidence, $policy, $ratio, $cleared, $reviewed)
            ON CONFLICT(anchor_id) DO UPDATE SET license_state = excluded.license_state,
              authorization_evidence = excluded.authorization_evidence,
              reuse_policy = excluded.reuse_policy,
              max_verbatim_ratio = excluded.max_verbatim_ratio,
              cleared_for_insertion = excluded.cleared_for_insertion,
              reviewed_at = excluded.reviewed_at;
            """;
        gate.Parameters.AddWithValue("$anchor_id", anchorId);
        gate.Parameters.AddWithValue("$state", license.State);
        gate.Parameters.AddWithValue("$evidence", licenseStatus);
        gate.Parameters.AddWithValue("$policy", license.Policy);
        gate.Parameters.AddWithValue("$ratio", license.Ratio.HasValue ? license.Ratio.Value : DBNull.Value);
        gate.Parameters.AddWithValue("$cleared", license.Cleared ? 1 : 0);
        gate.Parameters.AddWithValue("$reviewed", FormatTimestamp(now));
        await gate.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask EnsureAccessibleAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM reference_anchors
            WHERE anchor_id = $anchor_id
              AND (novel_id = $novel_id OR ((novel_id IS NULL OR novel_id = 0) AND corpus_visibility = $workspace));
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$novel_id", novelId);
        command.Parameters.AddWithValue("$workspace", ReferenceCorpusVisibilities.Workspace);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new ArgumentException("Reference source does not exist or is not accessible.", nameof(anchorId));
        }
    }

    private static async ValueTask DeleteAnchorRowsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long anchorId,
        CancellationToken cancellationToken)
    {
        var statements = new[]
        {
            "DELETE FROM reference_materialization_material_embeddings WHERE material_id IN (SELECT material_id FROM reference_materialization_materials WHERE anchor_id = $anchor_id);",
            "DELETE FROM reference_materialization_materials WHERE anchor_id = $anchor_id;",
            "DELETE FROM reference_materialization_vector_indexes WHERE generation_id IN (SELECT generation_id FROM reference_materialization_runs WHERE anchor_id = $anchor_id);",
            "DELETE FROM reference_materialization_chapter_progress WHERE run_id IN (SELECT run_id FROM reference_materialization_runs WHERE anchor_id = $anchor_id);",
            "DELETE FROM reference_materialization_run_leases WHERE run_id IN (SELECT run_id FROM reference_materialization_runs WHERE anchor_id = $anchor_id);",
            "DELETE FROM reference_materialization_runs WHERE anchor_id = $anchor_id;",
            "DELETE FROM reference_anchor_materialization_state WHERE anchor_id = $anchor_id;",
            "DELETE FROM reference_chapter_split_boundaries WHERE split_profile_id IN (SELECT split_profile_id FROM reference_chapter_split_profiles WHERE anchor_id = $anchor_id);",
            "DELETE FROM reference_chapter_split_profiles WHERE anchor_id = $anchor_id;",
            "DELETE FROM reference_source_license WHERE anchor_id = $anchor_id;",
            "DELETE FROM reference_library_members WHERE anchor_id = $anchor_id;",
            "DELETE FROM reference_anchors WHERE anchor_id = $anchor_id;"
        };
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$anchor_id", anchorId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask<ReferenceAnchorPayload?> ReadAnchorByIdAsync(
        SqliteConnection connection,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.anchor_id, a.novel_id, a.title, a.author, a.source_path,
                   a.source_kind, a.license_status, a.source_file_hash,
                   a.build_version, a.status, a.created_at, a.updated_at,
                   a.corpus_visibility, a.source_trust, a.user_tags_json
            FROM reference_anchors a WHERE a.anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAnchor(reader) : null;
    }

    private static ReferenceAnchorPayload ReadAnchor(SqliteDataReader reader)
    {
        var novelId = reader.IsDBNull(1) ? WorkspaceNovelId : reader.GetInt64(1);
        var status = reader.GetString(9);
        return new ReferenceAnchorPayload(
            reader.GetInt64(0),
            novelId,
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            status,
            ParseTimestamp(reader.GetString(10)),
            ParseTimestamp(reader.GetString(11)),
            reader.GetString(12),
            reader.GetString(13),
            JsonSerializer.Deserialize<IReadOnlyList<string>>(reader.GetString(14), JsonOptions) ?? []);
    }

    private static async ValueTask<SourceSnapshot> ReadSourceAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new ArgumentException("Reference source file does not exist.", nameof(path));
        }

        if (info.Length <= 0 || info.Length > MaxSourceBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(path), "Reference source file size is invalid.");
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var text = Encoding.UTF8.GetString(bytes);
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }

        return new SourceSnapshot(text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n'), HashBytes(bytes));
    }

    private static string ValidateSourcePath(string? value)
    {
        var path = Required(value, nameof(value), 1024);
        var full = Path.GetFullPath(path);
        if (Path.GetExtension(full) is not (".txt" or ".md"))
        {
            throw new ArgumentException("Reference source must be a .txt or .md file.", nameof(value));
        }

        return full;
    }

    private static string Required(string? value, string name, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength || normalized.Any(char.IsControl))
        {
            throw new ArgumentException($"{name} is invalid.", name);
        }

        return normalized;
    }

    private static string Optional(string? value, string name, int maxLength) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Required(value, name, maxLength);

    private static string ValidateValue(string? value, string name, IReadOnlySet<string> allowed)
    {
        var normalized = Required(value, name, 64).ToLowerInvariant();
        if (!allowed.Contains(normalized))
        {
            throw new ArgumentException($"Unsupported {name}.", name);
        }

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? tags) =>
        (tags ?? [])
            .Select(tag => Required(tag, nameof(tags), 128))
            .Distinct(StringComparer.Ordinal)
            .Take(32)
            .ToArray();

    private static string MapSourceQuality(string trust) => trust switch
    {
        ReferenceSourceTrustLevels.UserVerified => "trusted",
        ReferenceSourceTrustLevels.Unverified => "low",
        _ => "normal"
    };

    private static LicenseMapping MapLicenseStatus(string status) => status switch
    {
        "public_domain" => new(ReferenceCorpusLicenseStates.PublicDomain, ReferenceCorpusReusePolicies.VerbatimOk, 0.9, true),
        "licensed" or "user_provided" => new(ReferenceCorpusLicenseStates.Authorized, ReferenceCorpusReusePolicies.AdaptedOnly, 0.42, true),
        _ => new(ReferenceCorpusLicenseStates.Unknown, ReferenceCorpusReusePolicies.ReferenceOnly, null, false)
    };

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(string path, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static string HashBytes(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FormatTimestamp(DateTimeOffset value) => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseTimestamp(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static void ValidateNovelId(long novelId)
    {
        if (novelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId));
        }
    }

    private static void ValidateAnchorId(long anchorId)
    {
        if (anchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(anchorId));
        }
    }

    private sealed record SourceSnapshot(string Text, string Hash);
    private sealed record LicenseMapping(string State, string Policy, double? Ratio, bool Cleared);
}
