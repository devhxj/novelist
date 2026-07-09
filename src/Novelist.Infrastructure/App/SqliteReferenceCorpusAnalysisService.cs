using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceCorpusAnalysisService : IReferenceCorpusAnalysisService
{
    private const string AnalyzerVersion = "reference-corpus-feature-llm-v1";
    private const int MaxRunIdLength = 128;
    private const int MaxDiagnostics = 50;
    private const int MaxDiagnosticLength = 1_200;
    private static readonly string[] SensitiveDiagnosticIdentifiers =
    [
        "node_text",
        "source_text",
        "source_path",
        "raw_text",
        "candidate_text",
        "prompt",
        "model_output_json",
        "embedding"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppInitializationOptions _options;
    private readonly IAppSettingsService _settings;
    private readonly IReferenceCorpusFeatureFamilyAnalyzer _analyzer;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceCorpusAnalysisService(
        AppInitializationOptions? options = null,
        IAppSettingsService? settings = null,
        IReferenceCorpusFeatureFamilyAnalyzer? analyzer = null,
        IChatCompletionClient? chatCompletion = null)
    {
        _options = options ?? new AppInitializationOptions();
        _settings = settings ?? new FileSystemAppSettingsService(_options);
        _analyzer = analyzer ?? new ReferenceCorpusChatCompletionFeatureFamilyAnalyzer(
            _settings,
            chatCompletion ?? new StandardChatCompletionClient(new FileSystemLlmConfigurationService(_options)));
    }

    public async ValueTask<ReferenceCorpusFeatureAnalysisRunPayload> StartFeatureAnalysisAsync(
        StartReferenceCorpusFeatureAnalysisPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateStartInput(input);
        var scope = NormalizeScope(input.Scope);
        var families = FamiliesForScope(scope);
        var runId = NormalizeRunId(input.RunId) ?? BuildRunId(input.AnchorId, scope);
        var selectedModel = await ResolveSelectedModelAsync(cancellationToken)
            ?? throw new InvalidOperationException("Reference corpus feature analysis requires a selected model.");

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await EnsureAnchorAccessibleAsync(connection, input.NovelId, input.AnchorId, cancellationToken);

            try
            {
                var runner = new ReferenceCorpusFeatureAnalysisRunner(_analyzer);
                var result = await runner.RunAsync(
                    connection,
                    new ReferenceCorpusFeatureAnalysisRunRequest(
                        RunId: runId,
                        AnchorId: input.AnchorId,
                        NodeType: scope,
                        Families: families,
                        AnalyzerVersion: AnalyzerVersion,
                        ModelProvider: selectedModel.ProviderName,
                        ModelId: selectedModel.ModelId,
                        TokenBudget: input.TokenBudget,
                        Resume: input.Resume,
                        StartedAt: DateTimeOffset.UtcNow),
                    cancellationToken);
                await UpdateRunDiagnosticsAsync(connection, runId, result.Diagnostics, completedAt: null, cancellationToken);
                return await ReadRunPayloadAsync(
                    connection,
                    input.NovelId,
                    runId,
                    processedWorkItems: result.ProcessedWorkItems,
                    diagnostics: result.Diagnostics,
                    cancellationToken) ?? throw new InvalidOperationException("Feature analysis run disappeared after completion.");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                await MarkRunFailedAsync(connection, runId, exception, cancellationToken);
                throw;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceCorpusFeatureAnalysisRunPayload?> GetFeatureAnalysisRunAsync(
        GetReferenceCorpusFeatureAnalysisRunPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateNovelId(input.NovelId);
        var runId = NormalizeRequiredRunId(input.RunId);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            return await ReadRunPayloadAsync(
                connection,
                input.NovelId,
                runId,
                processedWorkItems: 0,
                diagnostics: null,
                cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private static void ValidateStartInput(StartReferenceCorpusFeatureAnalysisPayload input)
    {
        ValidateNovelId(input.NovelId);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.AnchorId, "Anchor id must be positive.");
        }

        _ = NormalizeScope(input.Scope);
        if (input.TokenBudget < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.TokenBudget, "Token budget cannot be negative.");
        }

        if (input.Resume && string.IsNullOrWhiteSpace(input.RunId))
        {
            throw new ArgumentException("Resume requires a run_id.", nameof(input));
        }

        _ = NormalizeRunId(input.RunId);
    }

    private static void ValidateNovelId(long novelId)
    {
        if (novelId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(novelId), novelId, "Novel id cannot be negative.");
        }
    }

    private static string NormalizeScope(string scope)
    {
        var normalized = (scope ?? string.Empty).Trim();
        return normalized switch
        {
            ReferenceCorpusNodeTypes.Sentence => ReferenceCorpusNodeTypes.Sentence,
            ReferenceCorpusNodeTypes.Passage => ReferenceCorpusNodeTypes.Passage,
            _ => throw new ArgumentException("Feature analysis scope must be sentence or passage.", nameof(scope))
        };
    }

    private static IReadOnlyList<string> FamiliesForScope(string scope)
    {
        return scope switch
        {
            ReferenceCorpusNodeTypes.Sentence => ReferenceCorpusFeatureFamilies.SentenceFamilies,
            ReferenceCorpusNodeTypes.Passage => ReferenceCorpusFeatureFamilies.PassageFamilies,
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, "Unsupported feature analysis scope.")
        };
    }

    private static string NormalizeRequiredRunId(string runId)
    {
        return NormalizeRunId(runId) ?? throw new ArgumentException("Run id is required.", nameof(runId));
    }

    private static string? NormalizeRunId(string? runId)
    {
        if (string.IsNullOrWhiteSpace(runId))
        {
            return null;
        }

        var normalized = runId.Trim();
        if (normalized.Length > MaxRunIdLength)
        {
            throw new ArgumentOutOfRangeException(nameof(runId), normalized.Length, $"Run id must be at most {MaxRunIdLength} characters.");
        }

        if (normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.')))
        {
            throw new ArgumentException("Run id contains unsupported characters.", nameof(runId));
        }

        return normalized;
    }

    private static string BuildRunId(long anchorId, string scope)
    {
        return $"corpus-feature:{anchorId.ToString(CultureInfo.InvariantCulture)}:{scope}:{Guid.NewGuid():N}";
    }

    private async ValueTask<SelectedModel?> ResolveSelectedModelAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedModelKey))
        {
            return null;
        }

        var parts = settings.SelectedModelKey.Split('/', 2, StringSplitOptions.None);
        if (parts.Length != 2 ||
            string.IsNullOrWhiteSpace(parts[0]) ||
            string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        var providerName = NormalizeProviderName(parts[0]);
        var modelId = NormalizeRequiredModelPart(parts[1], maxLength: 256);
        return providerName.Length == 0 || modelId.Length == 0
            ? null
            : new SelectedModel(providerName, modelId);
    }

    private static string NormalizeProviderName(string value)
    {
        var providerName = NormalizeRequiredModelPart(value, maxLength: 128).ToLowerInvariant();
        return providerName.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.'))
            ? string.Empty
            : providerName;
    }

    private static string NormalizeRequiredModelPart(string value, int maxLength)
    {
        var normalized = value.Trim();
        normalized = new string(normalized.Where(ch => !char.IsControl(ch)).ToArray());
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private async ValueTask<string> DatabasePathAsync(CancellationToken cancellationToken)
    {
        return Path.Combine(
            await AppDataDirectoryResolver.ResolveAsync(_options, cancellationToken),
            "reference-anchor",
            "index.sqlite");
    }

    private static async ValueTask EnsureSchemaAsync(string databasePath, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        await ReferenceCorpusSchemaProvisioner.EnsureCoreTablesAsync(connection, cancellationToken);
        await EnsureAnalysisDiagnosticsColumnAsync(connection, cancellationToken);
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async ValueTask EnsureAnalysisDiagnosticsColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        if (await HasColumnAsync(connection, "reference_analysis_runs", "diagnostics_json", cancellationToken))
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = "ALTER TABLE reference_analysis_runs ADD COLUMN diagnostics_json TEXT NOT NULL DEFAULT '[]';";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<bool> HasColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(" + tableName + ");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static async ValueTask EnsureAnchorAccessibleAsync(
        SqliteConnection connection,
        long novelId,
        long anchorId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT novel_id
            FROM reference_anchors
            WHERE anchor_id = $anchor_id;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value == DBNull.Value)
        {
            throw new ArgumentException($"Reference anchor '{anchorId}' is not accessible.", nameof(anchorId));
        }

        var anchorNovelId = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        if (anchorNovelId != 0 && novelId != 0 && anchorNovelId != novelId)
        {
            throw new ArgumentException($"Reference anchor '{anchorId}' is not accessible for novel '{novelId}'.", nameof(anchorId));
        }
    }

    private static async ValueTask UpdateRunDiagnosticsAsync(
        SqliteConnection connection,
        string runId,
        IReadOnlyList<string> diagnostics,
        DateTimeOffset? completedAt,
        CancellationToken cancellationToken)
    {
        var safeDiagnostics = SanitizeDiagnostics(diagnostics);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_analysis_runs
            SET diagnostics_json = $diagnostics_json,
                completed_at = COALESCE($completed_at, completed_at)
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$diagnostics_json", JsonSerializer.Serialize(safeDiagnostics, JsonOptions));
        command.Parameters.AddWithValue("$completed_at", completedAt is null ? DBNull.Value : FormatTimestamp(completedAt.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask MarkRunFailedAsync(
        SqliteConnection connection,
        string runId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var diagnostics = new[]
        {
            $"analysis_failed:{exception.GetType().Name}"
        };
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reference_analysis_runs
            SET status = 'failed',
                completed_at = $completed_at,
                diagnostics_json = $diagnostics_json,
                observation_count = (
                    SELECT COUNT(*)
                    FROM reference_feature_observations
                    WHERE run_id = $run_id
                )
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$completed_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$diagnostics_json", JsonSerializer.Serialize(SanitizeDiagnostics(diagnostics), JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<ReferenceCorpusFeatureAnalysisRunPayload?> ReadRunPayloadAsync(
        SqliteConnection connection,
        long novelId,
        string runId,
        int processedWorkItems,
        IReadOnlyList<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT r.run_id,
                   r.anchor_id,
                   r.scope,
                   r.status,
                   r.token_budget,
                   r.tokens_spent,
                   r.resume_cursor,
                   r.observation_count,
                   r.analyzer_version,
                   r.schema_version,
                   r.model_provider,
                   r.model_id,
                   r.started_at,
                   r.completed_at,
                   r.diagnostics_json,
                   a.novel_id
            FROM reference_analysis_runs r
            INNER JOIN reference_anchors a ON a.anchor_id = r.anchor_id
            WHERE r.run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var anchorNovelId = reader.IsDBNull(15) ? 0 : reader.GetInt64(15);
        if (anchorNovelId != 0 && novelId != 0 && anchorNovelId != novelId)
        {
            return null;
        }

        var scope = reader.GetString(2);
        var persistedDiagnostics = SanitizeDiagnostics(diagnostics ?? ReadDiagnostics(reader.GetString(14)));
        return new ReferenceCorpusFeatureAnalysisRunPayload(
            RunId: reader.GetString(0),
            NovelId: novelId == 0 ? anchorNovelId : novelId,
            AnchorId: reader.GetInt64(1),
            Scope: scope,
            Families: FamiliesForScope(scope),
            Status: reader.GetString(3),
            TokenBudget: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            TokensSpent: reader.GetInt32(5),
            ResumeCursor: reader.IsDBNull(6) ? null : reader.GetString(6),
            ObservationCount: reader.GetInt32(7),
            ProcessedWorkItems: processedWorkItems,
            AnalyzerVersion: reader.GetString(8),
            SchemaVersion: reader.GetString(9),
            ModelProvider: reader.GetString(10),
            ModelId: reader.GetString(11),
            StartedAt: DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
            CompletedAt: reader.IsDBNull(13)
                ? null
                : DateTimeOffset.Parse(reader.GetString(13), CultureInfo.InvariantCulture),
            Diagnostics: persistedDiagnostics);
    }

    private static IReadOnlyList<string> ReadDiagnostics(string diagnosticsJson)
    {
        if (string.IsNullOrWhiteSpace(diagnosticsJson))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<string>>(diagnosticsJson, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return ["diagnostics_unreadable"];
        }
    }

    private static IReadOnlyList<string> SanitizeDiagnostics(IReadOnlyList<string>? diagnostics)
    {
        if (diagnostics is null || diagnostics.Count == 0)
        {
            return [];
        }

        var result = new List<string>(Math.Min(diagnostics.Count, MaxDiagnostics + 1));
        foreach (var diagnostic in diagnostics.Take(MaxDiagnostics))
        {
            var safe = RedactDiagnostic(diagnostic);
            if (!string.IsNullOrWhiteSpace(safe))
            {
                result.Add(safe);
            }
        }

        if (diagnostics.Count > MaxDiagnostics)
        {
            result.Add("diagnostics_truncated");
        }

        return result;
    }

    private static string RedactDiagnostic(string? diagnostic)
    {
        var safe = ReferencePayloadSanitizer.RedactSensitiveIdentifier(diagnostic);
        foreach (var identifier in SensitiveDiagnosticIdentifiers)
        {
            safe = safe.Replace(identifier, "redacted_field", StringComparison.OrdinalIgnoreCase);
        }

        return safe.Length <= MaxDiagnosticLength
            ? safe
            : safe[..MaxDiagnosticLength].TrimEnd() + "...";
    }

    private static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }

    private readonly record struct SelectedModel(
        string ProviderName,
        string ModelId);
}
