using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceCorpusAnalysisService : IReferenceCorpusAnalysisService
{
    private const string FeatureAnalyzerVersion = "reference-corpus-feature-llm-v1";
    private const string TechniqueSpecimenAnalyzerVersion = "reference-corpus-technique-specimen-llm-v1";
    private const string TechniqueSpecimenScope = "technique_specimen";
    private const int MaxRunIdLength = 128;
    private const int MaxDiagnostics = 50;
    private const int MaxDiagnosticLength = 1_200;
    private const int MaxAnalysisTextLength = 4_000;
    private const int MaxEvidencePreviewLength = 160;
    private const int MaxStructuredListItems = 20;
    private static readonly string[] SensitiveDiagnosticIdentifiers =
    [
        "node_text",
        "source_text",
        "source_path",
        "raw_text",
        "raw_source",
        "candidate_text",
        "prompt",
        "model_output_json",
        "embedding",
        "value_json"
    ];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly PageRequestPolicy ObservationPagePolicy = new(
        AllowedSortFields: ["created_at", "feature_family", "confidence", "observation_id"],
        DefaultSortBy: "created_at",
        StableTieBreakers: ["created_at", "observation_id"]);

    private static readonly PageRequestPolicy TechniqueSpecimenPagePolicy = new(
        AllowedSortFields: ["created_at", "technique_family", "confidence", "specimen_id"],
        DefaultSortBy: "created_at",
        StableTieBreakers: ["created_at", "specimen_id"]);

    private readonly AppInitializationOptions _options;
    private readonly IAppSettingsService _settings;
    private readonly IReferenceCorpusFeatureFamilyAnalyzer _featureAnalyzer;
    private readonly IReferenceCorpusTechniqueSpecimenAnalyzer _techniqueSpecimenAnalyzer;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceCorpusAnalysisService(
        AppInitializationOptions? options = null,
        IAppSettingsService? settings = null,
        IReferenceCorpusFeatureFamilyAnalyzer? analyzer = null,
        IChatCompletionClient? chatCompletion = null,
        IReferenceCorpusTechniqueSpecimenAnalyzer? techniqueSpecimenAnalyzer = null)
    {
        _options = options ?? new AppInitializationOptions();
        _settings = settings ?? new FileSystemAppSettingsService(_options);
        var completion = chatCompletion ?? new StandardChatCompletionClient(new FileSystemLlmConfigurationService(_options));
        _featureAnalyzer = analyzer ?? new ReferenceCorpusChatCompletionFeatureFamilyAnalyzer(
            _settings,
            completion);
        _techniqueSpecimenAnalyzer = techniqueSpecimenAnalyzer ?? new ReferenceCorpusChatCompletionTechniqueSpecimenAnalyzer(
            _settings,
            completion);
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
                var runner = new ReferenceCorpusFeatureAnalysisRunner(_featureAnalyzer);
                var result = await runner.RunAsync(
                    connection,
                    new ReferenceCorpusFeatureAnalysisRunRequest(
                        RunId: runId,
                        AnchorId: input.AnchorId,
                        NodeType: scope,
                        Families: families,
                        AnalyzerVersion: FeatureAnalyzerVersion,
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
                await MarkFeatureRunFailedAsync(connection, runId, exception, cancellationToken);
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

    public async ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisRunPayload> StartTechniqueSpecimenAnalysisAsync(
        StartReferenceCorpusTechniqueSpecimenAnalysisPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateStartTechniqueSpecimenInput(input);
        var sourceNodeType = NormalizeTechniqueSpecimenSourceNodeType(input.SourceNodeType);
        var runId = NormalizeRunId(input.RunId) ?? BuildTechniqueSpecimenRunId(input.AnchorId, sourceNodeType);
        var selectedModel = await ResolveSelectedModelAsync(cancellationToken)
            ?? throw new InvalidOperationException("Reference corpus technique specimen analysis requires a selected model.");

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await EnsureAnchorAccessibleAsync(connection, input.NovelId, input.AnchorId, cancellationToken);

            try
            {
                var runner = new ReferenceCorpusTechniqueSpecimenRunner(_techniqueSpecimenAnalyzer);
                var result = await runner.RunAsync(
                    connection,
                    new ReferenceCorpusTechniqueSpecimenRunRequest(
                        RunId: runId,
                        AnchorId: input.AnchorId,
                        SourceNodeType: sourceNodeType,
                        AnalyzerVersion: TechniqueSpecimenAnalyzerVersion,
                        ModelProvider: selectedModel.ProviderName,
                        ModelId: selectedModel.ModelId,
                        MinObservationConfidence: input.MinObservationConfidence,
                        TokenBudget: input.TokenBudget,
                        Resume: input.Resume,
                        StartedAt: DateTimeOffset.UtcNow),
                    cancellationToken);
                await UpdateRunDiagnosticsAsync(connection, runId, result.Diagnostics, completedAt: null, cancellationToken);
                return await ReadTechniqueSpecimenRunPayloadAsync(
                    connection,
                    input.NovelId,
                    runId,
                    processedNodes: result.ProcessedNodes,
                    diagnostics: result.Diagnostics,
                    cancellationToken) ?? throw new InvalidOperationException("Technique specimen analysis run disappeared after completion.");
            }
            catch (Exception exception) when (
            exception is not OperationCanceledException and
            not ReferenceCorpusTechniqueSpecimenRunner.ReferenceCorpusAnalysisRunPreconditionException)
            {
                await MarkTechniqueSpecimenRunFailedAsync(connection, runId, exception, cancellationToken);
                throw;
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisRunPayload?> GetTechniqueSpecimenAnalysisRunAsync(
        GetReferenceCorpusTechniqueSpecimenAnalysisRunPayload input,
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
            return await ReadTechniqueSpecimenRunPayloadAsync(
                connection,
                input.NovelId,
                runId,
                processedNodes: 0,
                diagnostics: null,
                cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<PageResultPayload<ReferenceCorpusFeatureObservationPayload>> ListFeatureObservationsAsync(
        ListReferenceCorpusFeatureObservationsPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateListFeatureObservationsInput(input);
        var page = PageRequestNormalizer.Normalize(input.PageRequest, ObservationPagePolicy);
        ValidateObservationFilters(page.Filters);
        var nodeId = NormalizeOptionalIdentifier(input.NodeId, maxLength: 256);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await EnsureAnchorAccessibleAsync(connection, input.NovelId, input.AnchorId, cancellationToken);
            return await ReadFeatureObservationPageAsync(connection, input, nodeId, page, cancellationToken);
        }
        finally
        {
            _mutex.Release();
        }
    }

    public async ValueTask<PageResultPayload<ReferenceCorpusTechniqueSpecimenPayload>> ListTechniqueSpecimensAsync(
        ListReferenceCorpusTechniqueSpecimensPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        ValidateListTechniqueSpecimensInput(input);
        var page = PageRequestNormalizer.Normalize(input.PageRequest, TechniqueSpecimenPagePolicy);
        ValidateTechniqueSpecimenFilters(page.Filters);
        var sourceNodeId = NormalizeOptionalIdentifier(input.SourceNodeId, maxLength: 256);

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            await EnsureAnchorAccessibleAsync(connection, input.NovelId, input.AnchorId, cancellationToken);
            return await ReadTechniqueSpecimenPageAsync(connection, input, sourceNodeId, page, cancellationToken);
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

    private static void ValidateStartTechniqueSpecimenInput(StartReferenceCorpusTechniqueSpecimenAnalysisPayload input)
    {
        ValidateNovelId(input.NovelId);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.AnchorId, "Anchor id must be positive.");
        }

        _ = NormalizeTechniqueSpecimenSourceNodeType(input.SourceNodeType);
        if (input.MinObservationConfidence is < 0 or > 0.95)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.MinObservationConfidence, "Minimum observation confidence must be between 0 and 0.95.");
        }

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

    private static void ValidateListFeatureObservationsInput(ListReferenceCorpusFeatureObservationsPayload input)
    {
        ValidateNovelId(input.NovelId);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.AnchorId, "Anchor id must be positive.");
        }

        _ = NormalizeOptionalIdentifier(input.NodeId, maxLength: 256);
        ArgumentNullException.ThrowIfNull(input.PageRequest);
    }

    private static void ValidateListTechniqueSpecimensInput(ListReferenceCorpusTechniqueSpecimensPayload input)
    {
        ValidateNovelId(input.NovelId);
        if (input.AnchorId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(input), input.AnchorId, "Anchor id must be positive.");
        }

        _ = NormalizeOptionalIdentifier(input.SourceNodeId, maxLength: 256);
        ArgumentNullException.ThrowIfNull(input.PageRequest);
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

    private static string NormalizeTechniqueSpecimenSourceNodeType(string sourceNodeType)
    {
        var normalized = (sourceNodeType ?? string.Empty).Trim();
        return normalized switch
        {
            ReferenceCorpusNodeTypes.Sentence => ReferenceCorpusNodeTypes.Sentence,
            ReferenceCorpusNodeTypes.Passage => ReferenceCorpusNodeTypes.Passage,
            _ => throw new ArgumentException("Technique specimen source_node_type must be sentence or passage.", nameof(sourceNodeType))
        };
    }

    private static string? NormalizeOptionalIdentifier(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(nameof(value), normalized.Length, $"Identifier must be at most {maxLength} characters.");
        }

        if (normalized.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.')))
        {
            throw new ArgumentException("Identifier contains unsupported characters.", nameof(value));
        }

        return normalized;
    }

    private static void ValidateObservationFilters(IReadOnlyDictionary<string, string> filters)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "feature_family",
            "feature_key",
            "node_type",
            "review_state",
            "validity_state",
            "run_id",
            "min_confidence"
        };
        ValidateFilterNames(filters, allowed);
    }

    private static void ValidateTechniqueSpecimenFilters(IReadOnlyDictionary<string, string> filters)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "technique_family",
            "review_state",
            "validity_state",
            "run_id",
            "min_confidence"
        };
        ValidateFilterNames(filters, allowed);
    }

    private static void ValidateFilterNames(IReadOnlyDictionary<string, string> filters, HashSet<string> allowed)
    {
        foreach (var filter in filters.Keys)
        {
            if (!allowed.Contains(filter))
            {
                throw new PageRequestValidationException(
                    PageRequestErrorCodes.InvalidFilterKey,
                    $"filter key '{filter}' is not supported.");
            }
        }
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

    private static string BuildTechniqueSpecimenRunId(long anchorId, string sourceNodeType)
    {
        return $"corpus-technique:{anchorId.ToString(CultureInfo.InvariantCulture)}:{sourceNodeType}:{Guid.NewGuid():N}";
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

    private static async ValueTask MarkFeatureRunFailedAsync(
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

    private static async ValueTask MarkTechniqueSpecimenRunFailedAsync(
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
                    FROM reference_technique_specimens
                    WHERE analysis_run_id = $run_id
                )
            WHERE run_id = $run_id;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$completed_at", FormatTimestamp(now));
        command.Parameters.AddWithValue("$diagnostics_json", JsonSerializer.Serialize(SanitizeDiagnostics(diagnostics), JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<PageResultPayload<ReferenceCorpusFeatureObservationPayload>> ReadFeatureObservationPageAsync(
        SqliteConnection connection,
        ListReferenceCorpusFeatureObservationsPayload input,
        string? nodeId,
        NormalizedPageRequest page,
        CancellationToken cancellationToken)
    {
        var offset = DecodeOffsetCursor(page.Cursor);
        var where = BuildObservationWhereClause(input.AnchorId, nodeId, page.Filters);
        var total = await CountAsync(
            connection,
            """
            reference_feature_observations o
            INNER JOIN reference_text_nodes n ON n.node_id = o.node_id AND n.anchor_id = o.anchor_id
            """,
            where.Sql,
            where.Parameters,
            cancellationToken);
        var orderBy = BuildObservationOrderBy(page);
        var items = new List<ReferenceCorpusFeatureObservationPayload>(page.PageSize + 1);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                SELECT o.observation_id,
                       o.node_id,
                       o.anchor_id,
                       o.node_type,
                       n.text_hash,
                       o.feature_family,
                       o.feature_key,
                       o.value_kind,
                       o.value_text,
                       o.value_num,
                       o.value_bool,
                       o.value_json,
                       o.intensity,
                       o.confidence,
                       o.evidence_start,
                       o.evidence_end,
                       o.explanation,
                       o.review_state,
                       o.validity_state,
                       o.run_id,
                       o.created_at,
                       n.text
                FROM reference_feature_observations o
                INNER JOIN reference_text_nodes n ON n.node_id = o.node_id AND n.anchor_id = o.anchor_id
                {{where.Sql}}
                {{orderBy}}
                LIMIT $limit OFFSET $offset;
                """;
            AddParameters(command, where.Parameters);
            command.Parameters.AddWithValue("$limit", page.PageSize + 1);
            command.Parameters.AddWithValue("$offset", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var valueKind = reader.GetString(7);
                var valueText = reader.IsDBNull(8) ? null : BoundAnalysisText(reader.GetString(8));
                double? valueNum = reader.IsDBNull(9) ? null : reader.GetDouble(9);
                bool? valueBool = reader.IsDBNull(10) ? null : reader.GetInt32(10) != 0;
                var valueJson = reader.IsDBNull(11) ? null : reader.GetString(11);
                int? evidenceStart = reader.IsDBNull(14) ? null : reader.GetInt32(14);
                int? evidenceEnd = reader.IsDBNull(15) ? null : reader.GetInt32(15);
                items.Add(new ReferenceCorpusFeatureObservationPayload(
                    ObservationId: reader.GetString(0),
                    NodeId: reader.GetString(1),
                    AnchorId: reader.GetInt64(2),
                    NodeType: reader.GetString(3),
                    TextHash: reader.GetString(4),
                    FeatureFamily: reader.GetString(5),
                    FeatureKey: reader.GetString(6),
                    ValueKind: valueKind,
                    ValuePreview: BuildObservationValuePreview(valueKind, valueText, valueNum, valueBool, valueJson),
                    ValueText: valueText,
                    ValueNum: valueNum,
                    ValueBool: valueBool,
                    Intensity: reader.IsDBNull(12) ? null : reader.GetDouble(12),
                    Confidence: reader.GetDouble(13),
                    EvidenceStart: evidenceStart,
                    EvidenceEnd: evidenceEnd,
                    EvidencePreview: BuildEvidencePreview(reader.GetString(21), evidenceStart, evidenceEnd),
                    Explanation: reader.IsDBNull(16) ? null : BoundAnalysisText(reader.GetString(16)),
                    ReviewState: reader.GetString(17),
                    ValidityState: reader.GetString(18),
                    RunId: reader.GetString(19),
                    CreatedAt: DateTimeOffset.Parse(reader.GetString(20), CultureInfo.InvariantCulture)));
            }
        }

        return ToPageResult(items, total, offset, page.PageSize);
    }

    private static async ValueTask<PageResultPayload<ReferenceCorpusTechniqueSpecimenPayload>> ReadTechniqueSpecimenPageAsync(
        SqliteConnection connection,
        ListReferenceCorpusTechniqueSpecimensPayload input,
        string? sourceNodeId,
        NormalizedPageRequest page,
        CancellationToken cancellationToken)
    {
        var offset = DecodeOffsetCursor(page.Cursor);
        var where = BuildTechniqueSpecimenWhereClause(input.AnchorId, sourceNodeId, page.Filters);
        var total = await CountAsync(connection, "reference_technique_specimens s", where.Sql, where.Parameters, cancellationToken);
        var orderBy = BuildTechniqueSpecimenOrderBy(page);
        var items = new List<TechniqueSpecimenRow>(page.PageSize + 1);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $$"""
                SELECT s.specimen_id,
                       s.source_node_id,
                       s.source_anchor_id,
                       s.analysis_run_id,
                       s.technique_family,
                       s.technique_abstract,
                       s.trigger_context,
                       s.transfer_template,
                       s.transfer_slots_json,
                       s.effect_on_reader,
                       s.applicability_conditions,
                       s.failure_modes,
                       s.anti_patterns,
                       s.world_context_dependencies,
                       s.why_it_works_json,
                       s.confidence,
                       s.review_state,
                       s.validity_state,
                       s.mastery_notes,
                       s.created_at
                FROM reference_technique_specimens s
                {{where.Sql}}
                {{orderBy}}
                LIMIT $limit OFFSET $offset;
                """;
            AddParameters(command, where.Parameters);
            command.Parameters.AddWithValue("$limit", page.PageSize + 1);
            command.Parameters.AddWithValue("$offset", offset);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new TechniqueSpecimenRow(
                    SpecimenId: reader.GetString(0),
                    SourceNodeId: reader.GetString(1),
                    SourceAnchorId: reader.GetInt64(2),
                    AnalysisRunId: reader.GetString(3),
                    TechniqueFamily: reader.GetString(4),
                    TechniqueAbstract: BoundAnalysisText(reader.GetString(5)),
                    TriggerContext: BoundAnalysisText(reader.GetString(6)),
                    TransferTemplate: BoundAnalysisText(reader.GetString(7)),
                    TransferSlotsJson: BoundAnalysisText(reader.GetString(8)),
                    EffectOnReader: BoundAnalysisText(reader.GetString(9)),
                    ApplicabilityConditionsJson: BoundAnalysisText(reader.GetString(10)),
                    FailureModesJson: BoundAnalysisText(reader.GetString(11)),
                    AntiPatternsJson: BoundAnalysisText(reader.GetString(12)),
                    WorldContextDependenciesJson: reader.IsDBNull(13) ? null : BoundAnalysisText(reader.GetString(13)),
                    WhyItWorksJson: BoundAnalysisText(reader.GetString(14)),
                    Confidence: reader.GetDouble(15),
                    ReviewState: reader.GetString(16),
                    ValidityState: reader.GetString(17),
                    MasteryNotes: reader.IsDBNull(18) ? null : BoundAnalysisText(reader.GetString(18)),
                    CreatedAt: DateTimeOffset.Parse(reader.GetString(19), CultureInfo.InvariantCulture)));
            }
        }

        var visibleItems = items.Take(page.PageSize).ToArray();
        var evidenceBySpecimen = await ReadSpecimenEvidenceAsync(
            connection,
            visibleItems.Select(item => item.SpecimenId).ToArray(),
            cancellationToken);
        var hydrated = visibleItems
            .Select(item =>
            {
                var evidence = evidenceBySpecimen.TryGetValue(item.SpecimenId, out var trace)
                    ? trace
                    : [];
                return ToTechniqueSpecimenPayload(item, evidence);
            })
            .ToArray();
        return ToPageResult(hydrated, items.Count > page.PageSize, total, offset, page.PageSize);
    }

    private static async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<ReferenceCorpusTechniqueSpecimenEvidencePayload>>> ReadSpecimenEvidenceAsync(
        SqliteConnection connection,
        IReadOnlyList<string> specimenIds,
        CancellationToken cancellationToken)
    {
        if (specimenIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<ReferenceCorpusTechniqueSpecimenEvidencePayload>>(StringComparer.Ordinal);
        }

        var builder = new StringBuilder("""
            SELECT e.specimen_id,
                   o.observation_id,
                   o.node_id,
                   o.node_type,
                   n.text_hash,
                   o.feature_family,
                   o.feature_key,
                   o.value_kind,
                   o.value_text,
                   o.value_num,
                   o.value_bool,
                   o.value_json,
                   o.confidence,
                   o.evidence_start,
                   o.evidence_end,
                   o.explanation,
                   n.text
            FROM reference_specimen_evidence e
            INNER JOIN reference_feature_observations o ON o.observation_id = e.observation_id
            INNER JOIN reference_text_nodes n ON n.node_id = o.node_id AND n.anchor_id = o.anchor_id
            WHERE 1 = 1
            """);
        var parameters = new List<(string Name, object Value)>();
        AppendInClause(builder, parameters, "e.specimen_id", specimenIds, "specimen_id");
        builder.AppendLine("ORDER BY e.specimen_id, o.feature_family, o.feature_key, o.observation_id;");

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        AddParameters(command, parameters);

        var result = new Dictionary<string, List<ReferenceCorpusTechniqueSpecimenEvidencePayload>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var specimenId = reader.GetString(0);
            if (!result.TryGetValue(specimenId, out var list))
            {
                list = [];
                result[specimenId] = list;
            }

            list.Add(new ReferenceCorpusTechniqueSpecimenEvidencePayload(
                ObservationId: reader.GetString(1),
                NodeId: reader.GetString(2),
                NodeType: reader.GetString(3),
                TextHash: reader.GetString(4),
                FeatureFamily: reader.GetString(5),
                FeatureKey: reader.GetString(6),
                Confidence: reader.GetDouble(12),
                EvidenceStart: reader.IsDBNull(13) ? null : reader.GetInt32(13),
                EvidenceEnd: reader.IsDBNull(14) ? null : reader.GetInt32(14),
                EvidencePreview: BuildEvidencePreview(
                    reader.GetString(16),
                    reader.IsDBNull(13) ? null : reader.GetInt32(13),
                    reader.IsDBNull(14) ? null : reader.GetInt32(14)),
                ValuePreview: BuildObservationValuePreview(
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : BoundAnalysisText(reader.GetString(8)),
                    reader.IsDBNull(9) ? null : reader.GetDouble(9),
                    reader.IsDBNull(10) ? null : reader.GetInt32(10) != 0,
                    reader.IsDBNull(11) ? null : reader.GetString(11)),
                Explanation: reader.IsDBNull(15) ? null : BoundAnalysisText(reader.GetString(15))));
        }

        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<ReferenceCorpusTechniqueSpecimenEvidencePayload>)item.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static ReferenceCorpusTechniqueSpecimenPayload ToTechniqueSpecimenPayload(
        TechniqueSpecimenRow row,
        IReadOnlyList<ReferenceCorpusTechniqueSpecimenEvidencePayload> evidence)
    {
        return new ReferenceCorpusTechniqueSpecimenPayload(
            SpecimenId: row.SpecimenId,
            SourceNodeId: row.SourceNodeId,
            SourceAnchorId: row.SourceAnchorId,
            AnalysisRunId: row.AnalysisRunId,
            TechniqueFamily: row.TechniqueFamily,
            TechniqueAbstract: row.TechniqueAbstract,
            TriggerContext: row.TriggerContext,
            TransferTemplate: row.TransferTemplate,
            TransferSlots: ReadTransferSlots(row.TransferSlotsJson),
            EffectOnReader: row.EffectOnReader,
            ApplicabilityConditions: ReadStringArrayJson(row.ApplicabilityConditionsJson),
            FailureModes: ReadStringArrayJson(row.FailureModesJson),
            AntiPatterns: ReadStringArrayJson(row.AntiPatternsJson),
            WorldContextDependencies: ReadStringArrayJson(row.WorldContextDependenciesJson),
            WhyItWorks: ReadWhyItWorks(row.WhyItWorksJson, evidence),
            Confidence: row.Confidence,
            ReviewState: row.ReviewState,
            ValidityState: row.ValidityState,
            MasteryNotes: row.MasteryNotes,
            CreatedAt: row.CreatedAt,
            Evidence: evidence);
    }

    private static IReadOnlyList<ReferenceCorpusTechniqueTransferSlotPayload> ReadTransferSlots(string transferSlotsJson)
    {
        if (string.IsNullOrWhiteSpace(transferSlotsJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(transferSlotsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var slots = new List<ReferenceCorpusTechniqueTransferSlotPayload>();
            foreach (var item in document.RootElement.EnumerateArray().Take(MaxStructuredListItems))
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                slots.Add(new ReferenceCorpusTechniqueTransferSlotPayload(
                    SlotName: ReadBoundJsonString(item, "slot_name"),
                    Purpose: ReadBoundJsonString(item, "purpose"),
                    Constraints: ReadBoundJsonString(item, "constraints")));
            }

            return slots.ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static ReferenceCorpusTechniqueWhyItWorksPayload ReadWhyItWorks(
        string whyItWorksJson,
        IReadOnlyList<ReferenceCorpusTechniqueSpecimenEvidencePayload> evidence)
    {
        var evidenceByObservationId = evidence.ToDictionary(item => item.ObservationId, StringComparer.Ordinal);
        var traceComplete = true;
        var factors = new List<ReferenceCorpusTechniqueWhyFactorPayload>();

        try
        {
            using var document = JsonDocument.Parse(whyItWorksJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("contributing_factors", out var contributingFactors) ||
                contributingFactors.ValueKind != JsonValueKind.Array)
            {
                return new ReferenceCorpusTechniqueWhyItWorksPayload([], TraceComplete: false);
            }

            foreach (var factor in contributingFactors.EnumerateArray().Take(MaxStructuredListItems))
            {
                if (factor.ValueKind != JsonValueKind.Object)
                {
                    traceComplete = false;
                    continue;
                }

                var observationIds = ReadStringArrayProperty(factor, "observation_ids");
                var factorEvidence = new List<ReferenceCorpusTechniqueSpecimenEvidencePayload>();
                foreach (var observationId in observationIds)
                {
                    if (evidenceByObservationId.TryGetValue(observationId, out var evidenceItem))
                    {
                        factorEvidence.Add(evidenceItem);
                    }
                    else
                    {
                        traceComplete = false;
                    }
                }

                if (observationIds.Count == 0)
                {
                    traceComplete = false;
                }

                factors.Add(new ReferenceCorpusTechniqueWhyFactorPayload(
                    Factor: ReadBoundJsonString(factor, "factor"),
                    ObservationIds: observationIds,
                    Explanation: ReadBoundJsonString(factor, "explanation"),
                    Evidence: factorEvidence.ToArray()));
            }
        }
        catch (JsonException)
        {
            return new ReferenceCorpusTechniqueWhyItWorksPayload([], TraceComplete: false);
        }

        if (factors.Count == 0)
        {
            traceComplete = false;
        }

        return new ReferenceCorpusTechniqueWhyItWorksPayload(factors.ToArray(), traceComplete);
    }

    private static IReadOnlyList<string> ReadStringArrayJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            return document.RootElement
                .EnumerateArray()
                .Take(MaxStructuredListItems)
                .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                .Select(item => BoundAnalysisText(item.GetString()!))
                .ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadStringArrayProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return value
            .EnumerateArray()
            .Take(MaxStructuredListItems)
            .Where(item => item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
            .Select(item => BoundAnalysisText(item.GetString()!))
            .ToArray();
    }

    private static string ReadBoundJsonString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(value.GetString())
            ? BoundAnalysisText(value.GetString()!)
            : string.Empty;
    }

    private static string? BuildObservationValuePreview(
        string valueKind,
        string? valueText,
        double? valueNum,
        bool? valueBool,
        string? valueJson)
    {
        if (!string.IsNullOrWhiteSpace(valueText))
        {
            return valueText;
        }

        if (valueNum is not null)
        {
            return valueNum.Value.ToString("0.####", CultureInfo.InvariantCulture);
        }

        if (valueBool is not null)
        {
            return valueBool.Value ? "true" : "false";
        }

        return BuildStructuredValuePreview(valueKind, valueJson);
    }

    private static string? BuildStructuredValuePreview(string valueKind, string? valueJson)
    {
        if (string.IsNullOrWhiteSpace(valueJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(valueJson);
            var preview = document.RootElement.ValueKind switch
            {
                JsonValueKind.Array => BuildArrayPreview(document.RootElement),
                JsonValueKind.Object => BuildObjectPreview(document.RootElement),
                _ => BuildScalarPreview(document.RootElement)
            };

            return string.IsNullOrWhiteSpace(preview)
                ? valueKind
                : BoundAnalysisText(preview);
        }
        catch (JsonException)
        {
            return valueKind;
        }
    }

    private static string BuildArrayPreview(JsonElement array)
    {
        var parts = new List<string>();
        foreach (var item in array.EnumerateArray().Take(3))
        {
            var preview = item.ValueKind == JsonValueKind.Object
                ? BuildObjectPreview(item)
                : BuildScalarPreview(item);
            if (!string.IsNullOrWhiteSpace(preview))
            {
                parts.Add(preview);
            }
        }

        return string.Join("; ", parts);
    }

    private static string BuildObjectPreview(JsonElement obj)
    {
        var parts = new List<string>();
        foreach (var property in obj.EnumerateObject().Take(4))
        {
            var value = BuildScalarPreview(property.Value);
            if (!string.IsNullOrWhiteSpace(value))
            {
                parts.Add(ReferencePayloadSanitizer.RedactSensitiveIdentifier(property.Name) + "=" + value);
            }
        }

        return string.Join(", ", parts);
    }

    private static string BuildScalarPreview(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => ReferencePayloadSanitizer.RedactSensitiveIdentifier(value.GetString()),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty
        };
    }

    private static string? BuildEvidencePreview(string nodeText, int? evidenceStart, int? evidenceEnd)
    {
        if (string.IsNullOrEmpty(nodeText) ||
            evidenceStart is null ||
            evidenceEnd is null ||
            evidenceStart < 0 ||
            evidenceEnd <= evidenceStart ||
            evidenceStart >= nodeText.Length)
        {
            return null;
        }

        var boundedEnd = Math.Min(evidenceEnd.Value, nodeText.Length);
        var maxEnd = Math.Min(boundedEnd, evidenceStart.Value + MaxEvidencePreviewLength);
        var preview = nodeText[evidenceStart.Value..maxEnd];
        if (boundedEnd > maxEnd)
        {
            preview = preview.TrimEnd() + "...";
        }

        return BoundAnalysisText(preview);
    }

    private sealed record TechniqueSpecimenRow(
        string SpecimenId,
        string SourceNodeId,
        long SourceAnchorId,
        string AnalysisRunId,
        string TechniqueFamily,
        string TechniqueAbstract,
        string TriggerContext,
        string TransferTemplate,
        string TransferSlotsJson,
        string EffectOnReader,
        string ApplicabilityConditionsJson,
        string FailureModesJson,
        string AntiPatternsJson,
        string? WorldContextDependenciesJson,
        string WhyItWorksJson,
        double Confidence,
        string ReviewState,
        string ValidityState,
        string? MasteryNotes,
        DateTimeOffset CreatedAt);

    private static async ValueTask<long> CountAsync(
        SqliteConnection connection,
        string fromClause,
        string whereClause,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {fromClause} {whereClause};";
        AddParameters(command, parameters);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static (string Sql, IReadOnlyList<(string Name, object Value)> Parameters) BuildObservationWhereClause(
        long anchorId,
        string? nodeId,
        IReadOnlyDictionary<string, string> filters)
    {
        var builder = new StringBuilder("WHERE o.anchor_id = $anchor_id");
        var parameters = new List<(string Name, object Value)> { ("$anchor_id", anchorId) };
        if (!string.IsNullOrWhiteSpace(nodeId))
        {
            builder.AppendLine();
            builder.Append("  AND o.node_id = $node_id");
            parameters.Add(("$node_id", nodeId));
        }

        AppendOptionalFilter(builder, parameters, filters, "feature_family", "o.feature_family", "$feature_family");
        AppendOptionalFilter(builder, parameters, filters, "feature_key", "o.feature_key", "$feature_key");
        AppendOptionalFilter(builder, parameters, filters, "node_type", "o.node_type", "$node_type");
        AppendOptionalFilter(builder, parameters, filters, "review_state", "o.review_state", "$review_state");
        AppendOptionalFilter(builder, parameters, filters, "run_id", "o.run_id", "$run_id");
        AppendOptionalMinDoubleFilter(builder, parameters, filters, "min_confidence", "o.confidence", "$min_confidence");
        if (filters.ContainsKey("validity_state"))
        {
            AppendOptionalFilter(builder, parameters, filters, "validity_state", "o.validity_state", "$validity_state");
        }
        else
        {
            builder.AppendLine();
            builder.Append("  AND o.validity_state = 'active'");
        }

        return (builder.ToString(), parameters);
    }

    private static (string Sql, IReadOnlyList<(string Name, object Value)> Parameters) BuildTechniqueSpecimenWhereClause(
        long anchorId,
        string? sourceNodeId,
        IReadOnlyDictionary<string, string> filters)
    {
        var builder = new StringBuilder("WHERE s.source_anchor_id = $anchor_id");
        var parameters = new List<(string Name, object Value)> { ("$anchor_id", anchorId) };
        if (!string.IsNullOrWhiteSpace(sourceNodeId))
        {
            builder.AppendLine();
            builder.Append("  AND s.source_node_id = $source_node_id");
            parameters.Add(("$source_node_id", sourceNodeId));
        }

        AppendOptionalFilter(builder, parameters, filters, "technique_family", "s.technique_family", "$technique_family");
        AppendOptionalFilter(builder, parameters, filters, "review_state", "s.review_state", "$review_state");
        AppendOptionalFilter(builder, parameters, filters, "run_id", "s.analysis_run_id", "$run_id");
        AppendOptionalMinDoubleFilter(builder, parameters, filters, "min_confidence", "s.confidence", "$min_confidence");
        if (filters.ContainsKey("validity_state"))
        {
            AppendOptionalFilter(builder, parameters, filters, "validity_state", "s.validity_state", "$validity_state");
        }
        else
        {
            builder.AppendLine();
            builder.Append("  AND s.validity_state = 'active'");
        }

        return (builder.ToString(), parameters);
    }

    private static void AppendOptionalFilter(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        IReadOnlyDictionary<string, string> filters,
        string filterKey,
        string columnName,
        string parameterName)
    {
        if (!filters.TryGetValue(filterKey, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine();
        builder.Append("  AND ");
        builder.Append(columnName);
        builder.Append(" = ");
        builder.Append(parameterName);
        parameters.Add((parameterName, value.Trim()));
    }

    private static void AppendOptionalMinDoubleFilter(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        IReadOnlyDictionary<string, string> filters,
        string filterKey,
        string columnName,
        string parameterName)
    {
        if (!filters.TryGetValue(filterKey, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            parsed is < 0 or > 1)
        {
            throw new PageRequestValidationException(
                PageRequestErrorCodes.InvalidFilterKey,
                $"filter key '{filterKey}' has an invalid numeric value.");
        }

        builder.AppendLine();
        builder.Append("  AND ");
        builder.Append(columnName);
        builder.Append(" >= ");
        builder.Append(parameterName);
        parameters.Add((parameterName, parsed));
    }

    private static string BuildObservationOrderBy(NormalizedPageRequest page)
    {
        return BuildOrderBy(
            page,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["created_at"] = "o.created_at",
                ["feature_family"] = "o.feature_family",
                ["confidence"] = "o.confidence",
                ["observation_id"] = "o.observation_id"
            });
    }

    private static string BuildTechniqueSpecimenOrderBy(NormalizedPageRequest page)
    {
        return BuildOrderBy(
            page,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["created_at"] = "s.created_at",
                ["technique_family"] = "s.technique_family",
                ["confidence"] = "s.confidence",
                ["specimen_id"] = "s.specimen_id"
            });
    }

    private static string BuildOrderBy(NormalizedPageRequest page, IReadOnlyDictionary<string, string> columns)
    {
        var direction = string.Equals(page.SortDir, "asc", StringComparison.Ordinal) ? "ASC" : "DESC";
        var parts = new List<string>();
        foreach (var field in page.StableSortFields)
        {
            parts.Add(columns[field] + " " + direction);
        }

        return "ORDER BY " + string.Join(", ", parts);
    }

    private static PageResultPayload<T> ToPageResult<T>(
        List<T> fetchedItems,
        long total,
        int offset,
        int pageSize)
    {
        var hasMore = fetchedItems.Count > pageSize;
        var items = fetchedItems.Take(pageSize).ToArray();
        return ToPageResult(items, hasMore, total, offset, pageSize);
    }

    private static PageResultPayload<T> ToPageResult<T>(
        IReadOnlyList<T> items,
        bool hasMore,
        long total,
        int offset,
        int pageSize)
    {
        var nextOffset = offset + items.Count;
        return new PageResultPayload<T>(
            Items: items,
            Total: total,
            Page: offset / pageSize + 1,
            Size: pageSize,
            TotalPages: total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize),
            NextCursor: hasMore ? nextOffset.ToString(CultureInfo.InvariantCulture) : null,
            HasMore: hasMore,
            TotalEstimate: total > int.MaxValue ? int.MaxValue : (int)total);
    }

    private static int DecodeOffsetCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        if (int.TryParse(cursor.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var offset) &&
            offset >= 0)
        {
            return offset;
        }

        throw new PageRequestValidationException(PageRequestErrorCodes.InvalidCursor, "cursor is invalid.");
    }

    private static void AppendInClause<T>(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        string columnName,
        IReadOnlyCollection<T> values,
        string parameterPrefix)
        where T : notnull
    {
        if (values.Count == 0)
        {
            builder.AppendLine(" AND 1 = 0");
            return;
        }

        var names = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values)
        {
            var name = "$" + parameterPrefix + "_" + index.ToString(CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add((name, value));
            index++;
        }

        builder.Append(" AND ");
        builder.Append(columnName);
        builder.Append(" IN (");
        builder.Append(string.Join(", ", names));
        builder.AppendLine(")");
    }

    private static void AddParameters(SqliteCommand command, IReadOnlyList<(string Name, object Value)> parameters)
    {
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }
    }

    private static string BoundAnalysisText(string value)
    {
        var redacted = ReferencePayloadSanitizer.RedactSensitiveIdentifier(value);
        return redacted.Length <= MaxAnalysisTextLength
            ? redacted
            : redacted[..MaxAnalysisTextLength].TrimEnd() + "...";
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
        if (scope is not (ReferenceCorpusNodeTypes.Sentence or ReferenceCorpusNodeTypes.Passage))
        {
            return null;
        }

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

    private static async ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisRunPayload?> ReadTechniqueSpecimenRunPayloadAsync(
        SqliteConnection connection,
        long novelId,
        string runId,
        int processedNodes,
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
                   r.analyzer_version,
                   r.schema_version,
                   r.model_provider,
                   r.model_id,
                   r.started_at,
                   r.completed_at,
                   r.diagnostics_json,
                   a.novel_id,
                   (
                       SELECT COUNT(*)
                       FROM reference_technique_specimens s
                       WHERE s.analysis_run_id = r.run_id
                   ) AS specimen_count
            FROM reference_analysis_runs r
            INNER JOIN reference_anchors a ON a.anchor_id = r.anchor_id
            WHERE r.run_id = $run_id
              AND r.scope = $scope;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$scope", TechniqueSpecimenScope);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var anchorNovelId = reader.IsDBNull(14) ? 0 : reader.GetInt64(14);
        if (anchorNovelId != 0 && novelId != 0 && anchorNovelId != novelId)
        {
            return null;
        }

        var persistedDiagnostics = SanitizeDiagnostics(diagnostics ?? ReadDiagnostics(reader.GetString(13)));
        return new ReferenceCorpusTechniqueSpecimenAnalysisRunPayload(
            RunId: reader.GetString(0),
            NovelId: novelId == 0 ? anchorNovelId : novelId,
            AnchorId: reader.GetInt64(1),
            Scope: reader.GetString(2),
            Status: reader.GetString(3),
            TokenBudget: reader.IsDBNull(4) ? null : reader.GetInt32(4),
            TokensSpent: reader.GetInt32(5),
            ResumeCursor: reader.IsDBNull(6) ? null : reader.GetString(6),
            SpecimenCount: reader.GetInt32(15),
            ProcessedNodes: processedNodes,
            AnalyzerVersion: reader.GetString(7),
            SchemaVersion: reader.GetString(8),
            ModelProvider: reader.GetString(9),
            ModelId: reader.GetString(10),
            StartedAt: DateTimeOffset.Parse(reader.GetString(11), CultureInfo.InvariantCulture),
            CompletedAt: reader.IsDBNull(12)
                ? null
                : DateTimeOffset.Parse(reader.GetString(12), CultureInfo.InvariantCulture),
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
