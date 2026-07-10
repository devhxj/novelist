using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class SqliteReferenceCorpusService : IReferenceCorpusService
{
 private const int PerSourceCandidateLimit = MaxCandidatePoolSize;
private const int RecallRouteLimit = 128;
private const int MaxCandidatePoolSize = 512;
 private const string TextSemanticRoute = "text_semantic";
 private const string TechniqueSemanticRoute = "technique_semantic";
 private const string StructuredObservationRoute = "structured_observation";
 private const string ChapterContextRoute = "chapter_context";
    private const int NativeTechniqueRecallOverfetchMultiplier = 16;
    private const double ChapterContextRecallMinScore = 0.80;
    private const int MaxTextPreviewLength = 80;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly PageRequestPolicy CandidateSearchPagePolicy = new(
        AllowedSortFields: ["score", "created_at", "candidate_id"],
        DefaultSortBy: "score",
        StableTieBreakers: ["created_at", "candidate_id"]);

    private readonly AppInitializationOptions _options;
    private readonly IEmbeddingConfigurationService _embeddingConfiguration;
    private readonly IEmbeddingClient _embeddings;
    private readonly ISqliteVecTableProvisioner? _techniqueVectorProvisioner;
    private readonly ISqliteVecQueryProvider? _techniqueVectorQueryProvider;
    private readonly SemaphoreSlim _mutex = new(1, 1);

    public SqliteReferenceCorpusService(
        AppInitializationOptions? options = null,
        IEmbeddingConfigurationService? embeddingConfiguration = null,
        IEmbeddingClient? embeddings = null,
        ISqliteVecTableProvisioner? techniqueVectorProvisioner = null,
        ISqliteVecQueryProvider? techniqueVectorQueryProvider = null)
    {
        _options = options ?? new AppInitializationOptions();
        _embeddingConfiguration = embeddingConfiguration ?? new NullEmbeddingConfigurationService();
        _embeddings = embeddings ?? new HybridEmbeddingClient();
        _techniqueVectorProvisioner = techniqueVectorProvisioner;
        _techniqueVectorQueryProvider = techniqueVectorQueryProvider;
    }

public async ValueTask<PageResultPayload<ReferenceCorpusCandidatePayload>> SearchCandidatesAsync(
        SearchReferenceCorpusCandidatesPayload input,
        CancellationToken cancellationToken)
    {
ArgumentNullException.ThrowIfNull(input);
cancellationToken.ThrowIfCancellationRequested();
 var stopwatch = Stopwatch.StartNew();

        var page = PageRequestNormalizer.Normalize(input.PageRequest, CandidateSearchPagePolicy);
        ValidateQueryContext(input.QueryContext);
        ValidateCandidateSearchFilters(page.Filters);
        var requestedNodeType = RequestedNodeType(page.Filters);

        var embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken);
        if (embeddingOptions is null)
        {
            return Empty(page.PageSize);
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var queryEmbedding = await EmbedSingleAsync(
                BuildQueryEmbeddingText(input.QueryContext),
                embeddingOptions with { InputKind = BuiltinOnnxEmbeddingModel.QueryInputKind },
                cancellationToken);
            var nativeTechniqueRecall = await TryRecallNativeTechniqueNodesAsync(
                databasePath,
                connection,
                input.QueryContext,
                embeddingOptions,
                queryEmbedding,
                page,
                cancellationToken);
            var structuredObservationRecall = await ReadStructuredObservationRecallNodeIdsAsync(
                connection,
                input.QueryContext,
                requestedNodeType,
                page.Filters,
 RecallRouteLimit,
                cancellationToken);
            var chapterContextRecall = await ReadChapterContextRecallNodeIdsAsync(
                connection,
                input.QueryContext,
                requestedNodeType,
                page.Filters,
 RecallRouteLimit,
                cancellationToken);
 var candidates = SelectCandidatePool(await ReadScopedCandidateNodesAsync(
connection,
input,
page,
nativeTechniqueRecall,
structuredObservationRecall,
chapterContextRecall,
 cancellationToken), MaxCandidatePoolSize);
            if (candidates.Count == 0)
            {
                return Empty(page.PageSize);
            }

            var nodeEmbeddings = await EnsureNodeEmbeddingsAsync(
                connection,
                candidates,
                embeddingOptions,
                cancellationToken);
            var chapterEmbedding = await GetOrCreateCurrentChapterEmbeddingAsync(
                connection,
                input.QueryContext.ChapterContext,
                embeddingOptions,
                cancellationToken);
var techniqueVectors = await EnsureTechniqueVectorsAsync(
                connection,
                candidates,
                embeddingOptions,
cancellationToken);
var hasTechniqueVectors = techniqueVectors.Count > 0;
 if (nativeTechniqueRecall is null)
{
 foreach (var candidate in candidates)
{
 if (techniqueVectors.TryGetValue(candidate.NodeId, out var vectors) && vectors.Count > 0)
 {
 candidate.RecallRouteComponents.Add("recall_technique_semantic");
 }
}

}
var weights = BuildRetrievalWeights(input.RetrievalFeedback, hasTechniqueVectors);

            var scored = candidates
                .Select(candidate =>
                {
                    nodeEmbeddings.TryGetValue(candidate.NodeId, out var nodeEmbedding);
                    techniqueVectors.TryGetValue(candidate.NodeId, out var candidateTechniqueVectors);
                    var semantic = CosineSimilarity(queryEmbedding, nodeEmbedding);
                    var chapterFit = CosineSimilarity(chapterEmbedding, nodeEmbedding);
                    var techniqueFit = TechniqueFitScore(queryEmbedding, candidateTechniqueVectors);
                    var observationFit = ObservationFitScore(candidate.Observations, input.QueryContext);
                    var localContextFit = LocalContextFitScore(candidate, input.QueryContext.ChapterContext);
                    var positionFit = PositionFitScore(candidate, input.QueryContext.ChapterContext);
var quality = SourceQualityScore(candidate.SourceQuality);
 var sourceDiversity = SourceDiversityScore(candidate);
                    var scoreComponents = new Dictionary<string, double>
                    {
                        ["semantic"] = Math.Round(semantic, 6),
                        ["chapter_fit"] = Math.Round(chapterFit, 6),
                        ["technique_fit"] = Math.Round(techniqueFit, 6),
                        ["observation_fit"] = Math.Round(observationFit, 6),
                        ["local_context_fit"] = Math.Round(localContextFit, 6),
                        ["position_fit"] = Math.Round(positionFit, 6),
["source_quality"] = Math.Round(quality, 6)
 ,["source_diversity"] = Math.Round(sourceDiversity, 6)
                    };
                    foreach (var routeComponent in candidate.RecallRouteComponents)
                    {
                        scoreComponents[routeComponent] = 1;
                    }

var score = hasTechniqueVectors
? Math.Round(
 semantic * weights["semantic"] +
 chapterFit * weights["chapter_fit"] +
 techniqueFit * weights["technique_fit"] +
 observationFit * weights["observation_fit"] +
 localContextFit * weights["local_context_fit"] +
 positionFit * weights["position_fit"] +
 quality * weights["source_quality"] +
 sourceDiversity * weights["source_diversity"],
6)
: Math.Round(
 semantic * weights["semantic"] +
 chapterFit * weights["chapter_fit"] +
 observationFit * weights["observation_fit"] +
 localContextFit * weights["local_context_fit"] +
 positionFit * weights["position_fit"] +
 quality * weights["source_quality"] +
 sourceDiversity * weights["source_diversity"],
6);
                    return new ScoredCorpusCandidate(
                        candidate,
                        score,
                        scoreComponents);
                })
                .OrderByDescending(item => item.Score)
                .ThenBy(item => item.Candidate.AnchorId)
                .ThenBy(item => item.Candidate.SequenceIndex)
                .ThenBy(item => item.Candidate.NodeId, StringComparer.Ordinal)
                .ToArray();
 var merged = BuildRecallMergedOrder(scored, input.RetrievalFeedback);
 var fingerprint = CandidateCursorFingerprint(input.QueryContext, page, input.RetrievalFeedback, weights);
 var offset = DecodeCandidateCursor(page.Cursor, fingerprint, merged);
 var selected = merged.Skip(offset).Take(page.PageSize).ToArray();
 var hasMore = offset + selected.Length < merged.Count;
 stopwatch.Stop();
 var diagnostics = BuildRetrievalDiagnostics(
 merged,
nodeEmbeddings.Count,
techniqueVectors.Count,
 stopwatch.ElapsedMilliseconds,
 weights);
 var pageItems = selected
 .Select(item => ToPayload(item, diagnostics))
.ToArray();
 var nextCursor = hasMore && selected.Length > 0
 ? EncodeCandidateCursor(new(fingerprint, offset + selected.Length, selected[^1].Candidate.NodeId))
 : null;

return new PageResultPayload<ReferenceCorpusCandidatePayload>(
pageItems,
 merged.Count,
 Page: offset / page.PageSize + 1,
Size: page.PageSize,
 TotalPages: merged.Count == 0 ? 0 : (int)Math.Ceiling(merged.Count / (double)page.PageSize),
 NextCursor: nextCursor,
 HasMore: hasMore,
 TotalEstimate: merged.Count);
        }
        finally
        {
            _mutex.Release();
        }
}

 public async ValueTask<ReferenceCorpusCascadeImpactPayload> GetCascadeImpactAsync(
 GetReferenceCorpusCascadeImpactPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 var observationIds = input.ObservationIds
 .Select(value => value?.Trim() ?? string.Empty)
 .Where(value => value.Length > 0)
 .Distinct(StringComparer.Ordinal)
 .Order(StringComparer.Ordinal)
 .ToArray();
 if (observationIds.Length > 500)
 {
 throw new ArgumentOutOfRangeException(nameof(input), "At most 500 observation ids are allowed.");
 }

 if (observationIds.Length == 0)
 {
 return new([], [], [], []);
 }

 await _mutex.WaitAsync(cancellationToken);
 try
 {
 var databasePath = await DatabasePathAsync(cancellationToken);
 await EnsureSchemaAsync(databasePath, cancellationToken);
 await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
 await using var command = connection.CreateCommand();
 var parameters = observationIds.Select((_, index) => $"$observation_{index}").ToArray();
 for (var index = 0; index < observationIds.Length; index++)
 {
 command.Parameters.AddWithValue(parameters[index], observationIds[index]);
 }

 var inClause = string.Join(",", parameters);
 command.CommandText = $"""
 SELECT 'specimen', evidence.specimen_id, NULL
 FROM reference_specimen_evidence AS evidence
 WHERE evidence.observation_id IN ({inClause})
 UNION ALL
 SELECT 'beat', piece.beat_id, beat.blueprint_id
 FROM reference_blueprint_beat_pieces AS piece
 LEFT JOIN reference_corpus_blueprint_beats AS beat ON beat.beat_id = piece.beat_id
 WHERE piece.observation_id IN ({inClause});
 """;
 var specimenIds = new SortedSet<string>(StringComparer.Ordinal);
 var beatIds = new SortedSet<string>(StringComparer.Ordinal);
 var blueprintIds = new SortedSet<string>(StringComparer.Ordinal);
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 var kind = reader.GetString(0);
 var id = reader.GetString(1);
 if (kind == "specimen")
 {
 specimenIds.Add(id);
 }
 else
 {
 beatIds.Add(id);
 if (!reader.IsDBNull(2))
 {
 blueprintIds.Add(reader.GetString(2));
 }
 }
 }

 return new(observationIds, specimenIds.ToArray(), beatIds.ToArray(), blueprintIds.ToArray());
 }
 finally
 {
 _mutex.Release();
 }
 }

public async ValueTask<ReferenceCorpusTechniqueVectorIndexBackfillPayload> BackfillTechniqueVectorIndexAsync(
        BackfillReferenceCorpusTechniqueVectorIndexPayload input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        cancellationToken.ThrowIfCancellationRequested();
        ValidateQueryContext(input.QueryContext);

        var requestedNodeType = NormalizeRequestedNodeType(input.NodeType);
        var embeddingOptions = await _embeddingConfiguration.GetActiveEmbeddingOptionsAsync(cancellationToken);
        var dimensions = embeddingOptions?.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
        if (embeddingOptions is null)
        {
            return new ReferenceCorpusTechniqueVectorIndexBackfillPayload(
                ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Skipped,
                IndexScopeKey: null,
                TableName: null,
                ProviderKey: null,
                ModelId: null,
                Dimensions: dimensions,
                SourceCount: 0,
                VectorCount: 0,
                SkippedVectorCount: 0,
                Rebuilt: false,
                Diagnostics: ["embedding_configuration_missing"]);
        }

        if (_techniqueVectorProvisioner is null)
        {
            return new ReferenceCorpusTechniqueVectorIndexBackfillPayload(
                ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Skipped,
                IndexScopeKey: null,
                TableName: null,
                ProviderKey: embeddingOptions.ProviderKey,
                ModelId: embeddingOptions.ModelId,
                Dimensions: dimensions,
                SourceCount: 0,
                VectorCount: 0,
                SkippedVectorCount: 0,
                Rebuilt: false,
                Diagnostics: ["sqlite_vec_provisioner_missing"]);
        }

        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var databasePath = await DatabasePathAsync(cancellationToken);
            await EnsureSchemaAsync(databasePath, cancellationToken);
            await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
            var result = await EnsureNativeTechniqueIndexAsync(
                databasePath,
                connection,
                input.QueryContext,
                embeddingOptions,
                requestedNodeType,
                cancellationToken);
            return ToBackfillPayload(result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException or JsonException)
        {
            return new ReferenceCorpusTechniqueVectorIndexBackfillPayload(
                ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Failed,
                IndexScopeKey: null,
                TableName: null,
                ProviderKey: embeddingOptions.ProviderKey,
                ModelId: embeddingOptions.ModelId,
                Dimensions: dimensions,
                SourceCount: 0,
                VectorCount: 0,
                SkippedVectorCount: 0,
                Rebuilt: false,
                Diagnostics: ["native_technique_index_backfill_failed:" + ex.GetType().Name]);
        }
        finally
        {
            _mutex.Release();
        }
    }

 private static IReadOnlyList<ScoredCorpusCandidate> BuildRecallMergedOrder(
 IReadOnlyList<ScoredCorpusCandidate> scored,
 ReferenceCorpusRetrievalFeedbackPayload? feedback)
{
 if (scored.Count == 0)
{
return [];
}
 var routeOrders = new[]
 {
 BuildRouteTopK(scored, TextSemanticRoute, "semantic", RecallRouteLimit),
 BuildRouteTopK(scored, TechniqueSemanticRoute, "technique_fit", RecallRouteLimit),
 BuildRouteTopK(scored, StructuredObservationRoute, "observation_fit", RecallRouteLimit),
 BuildRouteTopK(scored, ChapterContextRoute, "local_context_fit", RecallRouteLimit)
 };
 var selected = new List<ScoredCorpusCandidate>(scored.Count);
 var selectedNodeIds = new HashSet<string>(StringComparer.Ordinal);
 var preferredRoutes = NormalizeRouteSet(feedback?.PreferredRoutes);
foreach (var route in routeOrders
 .OrderByDescending(order => preferredRoutes.Contains(order.Route)))
 {
 foreach (var candidate in route.Items)
 {
 if (selectedNodeIds.Add(candidate.Candidate.NodeId))
 {
 selected.Add(candidate);
 }
 }
 }

        foreach (var candidate in scored)
        {
if (selectedNodeIds.Add(candidate.Candidate.NodeId))
            {
                selected.Add(candidate);
            }
        }

return selected;
}

 private static RouteOrder BuildRouteTopK(
 IReadOnlyList<ScoredCorpusCandidate> scored,
 string route,
 string scoreComponent,
 int limit)
 {
 var routeMarker = "recall_" + route;
 var items = scored
 .Where(item => route == TextSemanticRoute || item.Candidate.RecallRouteComponents.Contains(routeMarker))
 .Where(item => ScoreComponent(item, scoreComponent) > 0)
 .OrderByDescending(item => ScoreComponent(item, scoreComponent))
 .ThenByDescending(item => item.Score)
 .ThenBy(item => item.Candidate.AnchorId)
 .ThenBy(item => item.Candidate.SequenceIndex)
 .ThenBy(item => item.Candidate.NodeId, StringComparer.Ordinal)
 .Take(limit)
 .ToArray();
 for (var index = 0; index < items.Length; index++)
 {
 MarkRecallComponent(items[index], routeMarker);
 items[index].Candidate.RouteProvenance[route] = new(index + 1, ScoreComponent(items[index], scoreComponent));
 }
 return new(route, items);
 }

 private static IReadOnlyList<CorpusCandidateNode> SelectCandidatePool(
 IReadOnlyList<CorpusCandidateNode> candidates,
 int limit)
 {
 return candidates
 .OrderByDescending(candidate => candidate.RecallRouteComponents.Count)
 .ThenBy(candidate => candidate.AnchorId)
 .ThenBy(candidate => candidate.SequenceIndex)
 .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
 .Take(limit)
 .ToArray();
 }

    private static void AddRecallWinner(
        List<ScoredCorpusCandidate> selected,
        HashSet<string> selectedNodeIds,
        IReadOnlyList<ScoredCorpusCandidate> scored,
        string recallComponent,
        string scoreComponent)
    {
        var winner = scored
            .Where(item => ScoreComponent(item, scoreComponent) > 0)
            .OrderByDescending(item => ScoreComponent(item, scoreComponent))
            .ThenByDescending(item => item.Score)
            .ThenBy(item => item.Candidate.AnchorId)
            .ThenBy(item => item.Candidate.SequenceIndex)
            .ThenBy(item => item.Candidate.NodeId, StringComparer.Ordinal)
            .FirstOrDefault();
        if (winner is null)
        {
            return;
        }

        MarkRecallComponent(winner, recallComponent);
        if (selectedNodeIds.Add(winner.Candidate.NodeId))
        {
            selected.Add(winner);
        }
    }

private static double ScoreComponent(ScoredCorpusCandidate candidate, string key)
{
return candidate.ScoreComponents.TryGetValue(key, out var value) ? value : 0;
}

 private static IReadOnlyDictionary<string, double> BuildRetrievalWeights(
 ReferenceCorpusRetrievalFeedbackPayload? feedback,
 bool hasTechniqueVectors)
 {
 var weights = hasTechniqueVectors
 ? new Dictionary<string, double>(StringComparer.Ordinal)
 {
 ["semantic"] = 0.29,
 ["chapter_fit"] = 0.17,
 ["technique_fit"] = 0.22,
 ["observation_fit"] = 0.13,
 ["local_context_fit"] = 0.08,
 ["position_fit"] = 0.03,
 ["source_quality"] = 0.04,
 ["source_diversity"] = 0.04
 }
 : new Dictionary<string, double>(StringComparer.Ordinal)
 {
 ["semantic"] = 0.37,
 ["chapter_fit"] = 0.21,
 ["technique_fit"] = 0,
 ["observation_fit"] = 0.15,
 ["local_context_fit"] = 0.10,
 ["position_fit"] = 0.06,
 ["source_quality"] = 0.06,
 ["source_diversity"] = 0.05
 };
 var routeToComponent = new Dictionary<string, string>(StringComparer.Ordinal)
 {
 [TextSemanticRoute] = "semantic",
 [TechniqueSemanticRoute] = "technique_fit",
 [StructuredObservationRoute] = "observation_fit",
 [ChapterContextRoute] = "local_context_fit"
 };
 foreach (var route in NormalizeRouteSet(feedback?.PreferredRoutes))
 {
 if (routeToComponent.TryGetValue(route, out var component)) weights[component] *= 1.25;
 }
 foreach (var route in NormalizeRouteSet(feedback?.AvoidedRoutes))
 {
 if (routeToComponent.TryGetValue(route, out var component)) weights[component] *= 0.60;
 }
 if (feedback?.PreferSourceDiversity == true) weights["source_diversity"] *= 1.75;
 if (feedback?.PreferSourceDiversity == false) weights["source_diversity"] *= 0.50;
 if (feedback?.WeightAdjustments is { } adjustments)
 {
 foreach (var adjustment in adjustments)
 {
 if (!weights.ContainsKey(adjustment.Key) || adjustment.Value is < -0.50 or > 0.50 || !double.IsFinite(adjustment.Value))
 {
 throw new ArgumentException($"Unsupported or out-of-range retrieval weight adjustment '{adjustment.Key}'.");
 }
 weights[adjustment.Key] *= 1 + adjustment.Value;
 }
 }
 var total = weights.Values.Sum();
 return weights.ToDictionary(
 item => item.Key,
 item => Math.Round(item.Value / total, 6),
 StringComparer.Ordinal);
 }

 private static HashSet<string> NormalizeRouteSet(IReadOnlyList<string>? routes)
 {
 var allowed = new HashSet<string>(
 [TextSemanticRoute, TechniqueSemanticRoute, StructuredObservationRoute, ChapterContextRoute],
 StringComparer.Ordinal);
 var result = new HashSet<string>(StringComparer.Ordinal);
 foreach (var route in routes ?? [])
 {
 var normalized = route.Trim().ToLowerInvariant();
 if (!allowed.Contains(normalized))
 {
 throw new ArgumentException($"Unsupported retrieval route '{route}'.");
 }
 result.Add(normalized);
 }
 return result;
 }

 private static double SourceDiversityScore(CorpusCandidateNode candidate)
 {
 return candidate.SourceCoverage.Count <= 1
 ? 0
 : Math.Min(1, candidate.SourceCoverage.Select(source => source.LibraryId).Distinct(StringComparer.Ordinal).Count() / 4.0);
 }

private static IReadOnlyList<ReferenceCorpusSourceCoveragePayload> ParseSourceCoverage(string json)
{
try
{
 using var document = JsonDocument.Parse(json);
 if (document.RootElement.ValueKind != JsonValueKind.Array) return [];
 return document.RootElement.EnumerateArray()
 .Select(item => new ReferenceCorpusSourceCoveragePayload(
 item.GetProperty("library_id").GetString() ?? string.Empty,
 item.GetProperty("anchor_id").GetInt64(),
 item.GetProperty("source_quality").GetString() ?? string.Empty,
 item.GetProperty("license_state").GetString() ?? string.Empty,
 item.GetProperty("reuse_policy").GetString() ?? string.Empty,
 ReadJsonBoolean(item.GetProperty("selected_representative"))))
 .ToArray();
}
catch (JsonException)
{
return [];
}
}

 private static bool ReadJsonBoolean(JsonElement value)
 {
 return value.ValueKind switch
 {
 JsonValueKind.True => true,
 JsonValueKind.False => false,
 JsonValueKind.Number when value.TryGetInt32(out var number) => number != 0,
 _ => false
 };
 }

private static void MarkRecallComponent(ScoredCorpusCandidate candidate, string component)
    {
        if (candidate.ScoreComponents is IDictionary<string, double> mutable)
        {
            mutable[component] = 1;
}
}

 private static ReferenceCorpusRetrievalDiagnosticsPayload BuildRetrievalDiagnostics(
 IReadOnlyList<ScoredCorpusCandidate> candidates,
 int nodeEmbeddingCount,
int techniqueVectorNodeCount,
 long elapsedMilliseconds,
 IReadOnlyDictionary<string, double> appliedWeights)
 {
 return new(
 candidates.Count,
 candidates.Count(item => ScoreComponent(item, "semantic") > 0),
 candidates.Count(item => item.Candidate.RecallRouteComponents.Contains("recall_technique_semantic")),
 candidates.Count(item => item.Candidate.RecallRouteComponents.Contains("recall_structured_observation")),
 candidates.Count(item => item.Candidate.RecallRouteComponents.Contains("recall_chapter_context")),
 nodeEmbeddingCount,
 techniqueVectorNodeCount,
 elapsedMilliseconds,
 appliedWeights);
 }

private static string CandidateCursorFingerprint(
ReferenceCorpusQueryContextPayload context,
 NormalizedPageRequest page,
 ReferenceCorpusRetrievalFeedbackPayload? feedback,
 IReadOnlyDictionary<string, double> weights)
 {
 var filters = string.Join('\u001e', page.Filters.Select(item => item.Key + "=" + item.Value));
 return StableHash(
"reference-corpus-candidate-cursor-v1",
JsonSerializer.Serialize(context, JsonOptions),
 JsonSerializer.Serialize(feedback, JsonOptions),
 JsonSerializer.Serialize(weights, JsonOptions),
JsonSerializer.Serialize(page.Filters, JsonOptions),
page.SortBy,
page.SortDir,
 filters);
 }

 private static string EncodeCandidateCursor(ReferenceCorpusCandidateCursor cursor)
 {
 return Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(cursor, JsonOptions))
 .TrimEnd('=')
 .Replace('+', '-')
 .Replace('/', '_');
 }

 private static int DecodeCandidateCursor(
 string? value,
 string fingerprint,
 IReadOnlyList<ScoredCorpusCandidate> candidates)
 {
 if (string.IsNullOrWhiteSpace(value))
 {
 return 0;
 }

 try
 {
 var normalized = value.Replace('-', '+').Replace('_', '/');
 normalized += new string('=', (4 - normalized.Length % 4) % 4);
 var cursor = JsonSerializer.Deserialize<ReferenceCorpusCandidateCursor>(
 Convert.FromBase64String(normalized),
 JsonOptions);
 if (cursor is null ||
 !string.Equals(cursor.Fingerprint, fingerprint, StringComparison.Ordinal) ||
 cursor.Offset is < 1 ||
 cursor.Offset > candidates.Count ||
 !string.Equals(candidates[cursor.Offset - 1].Candidate.NodeId, cursor.LastNodeId, StringComparison.Ordinal))
 {
 throw new FormatException();
 }

 return cursor.Offset;
 }
 catch (Exception exception) when (exception is FormatException or JsonException)
 {
 throw new PageRequestValidationException(
 PageRequestErrorCodes.InvalidCursor,
 "cursor is invalid, stale, or does not match the retrieval query.");
 }
 }

    private static PageResultPayload<ReferenceCorpusCandidatePayload> Empty(int pageSize)
    {
        return new PageResultPayload<ReferenceCorpusCandidatePayload>(
            Items: [],
            Total: 0,
            Page: 1,
            Size: pageSize,
            TotalPages: 0,
            NextCursor: null,
            HasMore: false,
            TotalEstimate: 0);
    }

    private static void ValidateQueryContext(ReferenceCorpusQueryContextPayload context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.ChapterContext);
        ArgumentNullException.ThrowIfNull(context.Scope);
        if (context.ChapterContext.NovelId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), context.ChapterContext.NovelId, "Novel id must be positive.");
        }

        if (context.ChapterContext.ChapterNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(context), context.ChapterContext.ChapterNumber, "Chapter number must be positive.");
        }
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
    }

    private static async ValueTask<SqliteConnection> OpenConnectionAsync(
        string databasePath,
        CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false };
        var connection = new SqliteConnection(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys = ON;";
        await pragma.ExecuteNonQueryAsync(cancellationToken);
        return connection;
    }

    private static async ValueTask<IReadOnlyList<CorpusCandidateNode>> ReadScopedCandidateNodesAsync(
        SqliteConnection connection,
        SearchReferenceCorpusCandidatesPayload input,
        NormalizedPageRequest page,
        NativeTechniqueRecallResult? nativeTechniqueRecall,
        StructuredObservationRecallResult? structuredObservationRecall,
        ChapterContextRecallResult? chapterContextRecall,
        CancellationToken cancellationToken)
    {
        var requestedNodeType = RequestedNodeType(page.Filters);

        ValidateCandidateSearchFilters(page.Filters);
        var libraryIds = await ResolveEffectiveLibraryIdsAsync(
            connection,
            input.QueryContext.Scope,
            input.QueryContext.ChapterContext.NovelId,
            cancellationToken);
        if (libraryIds.Count == 0)
        {
            return [];
        }

        var reusePolicies = NormalizeTextSet(input.QueryContext.Scope.ReusePolicies);
        if (reusePolicies.Count == 0)
        {
            reusePolicies = [ReferenceCorpusReusePolicies.VerbatimOk, ReferenceCorpusReusePolicies.AdaptedOnly];
        }

        var includeAnchorIds = NormalizePositiveLongSet(input.QueryContext.Scope.IncludeAnchorIds);
        var excludeAnchorIds = NormalizePositiveLongSet(input.QueryContext.Scope.ExcludeAnchorIds);
 var perSourceLimit = PerSourceCandidateLimit;
var parameters = new List<(string Name, object Value)>
        {
            ("$node_type", requestedNodeType),
            ("$novel_id", input.QueryContext.ChapterContext.NovelId),
("$per_source_limit", perSourceLimit)
};
 var eligibleScopeSql = BuildEligibleScopeSql(
 parameters,
 libraryIds,
 reusePolicies,
 includeAnchorIds,
 excludeAnchorIds);
        var recallRouteSql = BuildRecallRouteSql(parameters, input.QueryContext);
        var techniqueSpecimenFallbackSql = nativeTechniqueRecall is null
            ? """
                   OR EXISTS (
                       SELECT 1
                       FROM reference_technique_specimens ts_candidate
                       WHERE ts_candidate.source_node_id = scoped_nodes.node_id
                         AND ts_candidate.validity_state = 'active'
                         AND ts_candidate.review_state <> 'rejected'
                         AND ts_candidate.superseded_by_run_id IS NULL
                   )
            """
            : string.Empty;
        var nativeTechniqueRouteSql = BuildNativeTechniqueRouteSql(parameters, nativeTechniqueRecall);
        var structuredObservationRouteSql = BuildStructuredObservationRouteSql(parameters, structuredObservationRecall);
        var chapterContextRouteSql = BuildChapterContextRouteSql(parameters, chapterContextRecall);
        var commandText = $$"""
            WITH eligible_nodes AS (
                SELECT n.node_id,
                       n.anchor_id,
                       n.node_type,
                       n.sequence_index,
                       n.chapter_index,
                       n.text_hash,
                       n.text,
                       n.created_at,
                       lm.library_id,
                       COALESCE(NULLIF(TRIM(lm.dedup_group_id), ''), 'anchor:' || n.anchor_id) AS dedup_group_key,
                       COALESCE(lm.source_quality, '') AS source_quality,
                       CASE COALESCE(lm.source_quality, '')
                           WHEN 'trusted' THEN 3
                           WHEN 'normal' THEN 2
                           WHEN 'low' THEN 1
                           ELSE 0
                       END AS source_quality_rank,
                       lic.license_state,
                       lic.reuse_policy
                FROM reference_text_nodes n
                JOIN reference_anchors a ON a.anchor_id = n.anchor_id
                JOIN reference_library_members lm ON lm.anchor_id = n.anchor_id
                JOIN reference_corpus_libraries lib ON lib.library_id = lm.library_id
                JOIN reference_source_license lic ON lic.anchor_id = n.anchor_id
                WHERE n.node_type = $node_type
                  AND a.status = 'ready'
                  AND lm.enabled = 1
                  AND (lib.scope = 'global' OR (lib.scope = 'project' AND lib.novel_id = $novel_id))
                  AND lic.license_state IN ('public_domain', 'cc', 'authorized')
AND lic.reuse_policy IN ('verbatim_ok', 'adapted_only')
AND (a.novel_id = $novel_id OR ((a.novel_id IS NULL OR a.novel_id = 0) AND a.corpus_visibility = 'workspace'))
 {{eligibleScopeSql}}
            ),
 source_representatives AS (
                SELECT library_id,
                       anchor_id,
                       dedup_group_key
                FROM (
                    SELECT library_id,
                           anchor_id,
                           dedup_group_key,
                           ROW_NUMBER() OVER (
                               PARTITION BY dedup_group_key
                               ORDER BY source_quality_rank DESC, library_id, anchor_id
                           ) AS dedup_rank
                    FROM (
                        SELECT DISTINCT library_id,
                               anchor_id,
                               dedup_group_key,
                               source_quality_rank
                        FROM eligible_nodes
                    )
                )
WHERE dedup_rank = 1
),
 source_coverage AS (
 SELECT coverage.dedup_group_key,
 json_group_array(json_object(
 'library_id', coverage.library_id,
 'anchor_id', coverage.anchor_id,
 'source_quality', coverage.source_quality,
 'license_state', coverage.license_state,
 'reuse_policy', coverage.reuse_policy,
 'selected_representative', coverage.selected_representative)) AS coverage_json
 FROM (
 SELECT DISTINCT e.dedup_group_key,
 e.library_id,
 e.anchor_id,
 e.source_quality,
 e.license_state,
 e.reuse_policy,
 CASE WHEN EXISTS (
 SELECT 1 FROM source_representatives representative
 WHERE representative.dedup_group_key=e.dedup_group_key
 AND representative.library_id=e.library_id
 AND representative.anchor_id=e.anchor_id)
 THEN 1 ELSE 0 END AS selected_representative
 FROM eligible_nodes e
 ) coverage
 GROUP BY coverage.dedup_group_key
 ),
scoped_nodes AS (
 SELECT e.*, coverage.coverage_json,
                       ROW_NUMBER() OVER (
                           PARTITION BY e.library_id, e.anchor_id
                           ORDER BY e.sequence_index, e.node_id
                       ) AS source_rank
                FROM eligible_nodes e
JOIN source_representatives r
                  ON r.library_id = e.library_id
                 AND r.anchor_id = e.anchor_id
AND r.dedup_group_key = e.dedup_group_key
 JOIN source_coverage coverage ON coverage.dedup_group_key=e.dedup_group_key
            ),
            selected_nodes AS (
                SELECT *
                FROM scoped_nodes
                WHERE source_rank <= $per_source_limit
                   {{techniqueSpecimenFallbackSql}}
                   {{nativeTechniqueRouteSql}}
                   {{structuredObservationRouteSql}}
                   {{chapterContextRouteSql}}
                   {{recallRouteSql}}
            )
            SELECT n.node_id,
                   n.anchor_id,
                   n.node_type,
                   n.sequence_index,
                   n.chapter_index,
                   n.text_hash,
                   n.text,
                   n.created_at,
                   n.library_id,
                   n.source_quality,
                   n.license_state,
 n.reuse_policy,
 n.dedup_group_key,
 n.coverage_json,
 o.observation_id,
                   o.feature_family,
                   o.feature_key,
                   o.value_text,
                   o.confidence
            FROM selected_nodes n
            LEFT JOIN reference_feature_observations o
              ON o.node_id = n.node_id
             AND o.validity_state = 'active'
 WHERE 1 = 1
""";
var builder = new StringBuilder(commandText);
 builder.AppendLine();
AppendStructuredObservationFilters(builder, parameters, page.Filters);
        builder.AppendLine("ORDER BY n.library_id, n.anchor_id, n.sequence_index, n.node_id, o.observation_id;");

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var map = new Dictionary<string, CorpusCandidateNode>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.GetString(0);
            if (!map.TryGetValue(nodeId, out var candidate))
            {
                var recallRouteComponents = new HashSet<string>(StringComparer.Ordinal);
                if (nativeTechniqueRecall?.NodeIds.Contains(nodeId) == true)
                {
                    recallRouteComponents.Add("recall_technique_semantic");
                }

                if (structuredObservationRecall?.NodeIds.Contains(nodeId) == true)
                {
                    recallRouteComponents.Add("recall_structured_observation");
                }

                if (chapterContextRecall?.NodeIds.Contains(nodeId) == true)
                {
                    recallRouteComponents.Add("recall_chapter_context");
                }

                candidate = new CorpusCandidateNode(
                    NodeId: nodeId,
                    AnchorId: reader.GetInt64(1),
                    NodeType: reader.GetString(2),
                    SequenceIndex: reader.GetInt32(3),
                    ChapterIndex: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    TextHash: reader.GetString(5),
                    Text: reader.GetString(6),
                    CreatedAt: reader.GetString(7),
                    LibraryId: reader.GetString(8),
                    SourceQuality: reader.IsDBNull(9) ? string.Empty : reader.GetString(9),
                    LicenseState: reader.GetString(10),
ReusePolicy: reader.GetString(11),
 DedupGroupKey: reader.GetString(12),
 SourceCoverage: ParseSourceCoverage(reader.GetString(13)),
RecallRouteComponents: recallRouteComponents,
 RouteProvenance: new Dictionary<string, RouteProvenance>(StringComparer.Ordinal),
Observations: []);
                map.Add(nodeId, candidate);
            }

 if (!reader.IsDBNull(14))
{
candidate.Observations.Add(new CorpusCandidateObservation(
 ObservationId: reader.GetString(14),
 FeatureFamily: reader.GetString(15),
 FeatureKey: reader.GetString(16),
 ValueText: reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
 Confidence: reader.GetDouble(18)));
            }
        }

        return map.Values.ToArray();
    }

    private static string BuildRecallRouteSql(
        List<(string Name, object Value)> parameters,
        ReferenceCorpusQueryContextPayload queryContext)
    {
        var builder = new StringBuilder();
        var queryTerms = BuildQueryTerms(queryContext)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(24)
            .ToArray();
        var queryTextExpression = BuildLikeAnyExpression(
            parameters,
            "scoped_nodes.text",
            queryTerms,
            "route_query_text");
        if (queryTextExpression.Length > 0)
        {
            builder.AppendLine("OR (" + queryTextExpression + ")");
        }

        return builder.ToString();
    }

    private static async ValueTask<ChapterContextRecallResult> ReadChapterContextRecallNodeIdsAsync(
        SqliteConnection connection,
        ReferenceCorpusQueryContextPayload queryContext,
        string requestedNodeType,
        IReadOnlyDictionary<string, string> filters,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var contextTerms = BuildLocalContextTerms(queryContext.ChapterContext)
            .Where(term => term.Text.Length >= 2)
            .DistinctBy(term => term.Text, StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToArray();
        if (contextTerms.Length == 0)
        {
            return new ChapterContextRecallResult(new HashSet<string>(StringComparer.Ordinal));
        }

        var libraryIds = await ResolveEffectiveLibraryIdsAsync(
            connection,
            queryContext.Scope,
            queryContext.ChapterContext.NovelId,
            cancellationToken);
        if (libraryIds.Count == 0)
        {
            return new ChapterContextRecallResult(new HashSet<string>(StringComparer.Ordinal));
        }

        var reusePolicies = NormalizeTextSet(queryContext.Scope.ReusePolicies);
        if (reusePolicies.Count == 0)
        {
            reusePolicies = [ReferenceCorpusReusePolicies.VerbatimOk, ReferenceCorpusReusePolicies.AdaptedOnly];
        }

        var includeAnchorIds = NormalizePositiveLongSet(queryContext.Scope.IncludeAnchorIds);
        var excludeAnchorIds = NormalizePositiveLongSet(queryContext.Scope.ExcludeAnchorIds);
        var parameters = new List<(string Name, object Value)>
        {
            ("$node_type", requestedNodeType),
            ("$novel_id", queryContext.ChapterContext.NovelId),
 ("$limit", Math.Clamp(pageSize, 1, RecallRouteLimit)),
            ("$min_chapter_context_route_score", ChapterContextRecallMinScore)
        };
        var contextExpression = BuildLikeAnyExpression(
            parameters,
            "n.text",
            contextTerms.Select(term => term.Text).ToArray(),
            "chapter_context_route_term");
        if (contextExpression.Length == 0)
        {
            return new ChapterContextRecallResult(new HashSet<string>(StringComparer.Ordinal));
        }

        var scoreExpression = BuildWeightedLikeScoreExpression(
            parameters,
            "n.text",
            contextTerms,
            "chapter_context_route_score");
        var builder = new StringBuilder($"""
            WITH eligible_nodes AS (
                SELECT n.node_id,
                       n.anchor_id,
                       n.sequence_index,
                       n.text,
                       lm.library_id,
                       COALESCE(NULLIF(TRIM(lm.dedup_group_id), ''), 'anchor:' || n.anchor_id) AS dedup_group_key,
                       CASE COALESCE(lm.source_quality, '')
                           WHEN 'trusted' THEN 3
                           WHEN 'normal' THEN 2
                           WHEN 'low' THEN 1
                           ELSE 0
                       END AS source_quality_rank,
                       lic.reuse_policy
                FROM reference_text_nodes n
                JOIN reference_anchors a ON a.anchor_id = n.anchor_id
                JOIN reference_library_members lm ON lm.anchor_id = n.anchor_id
                JOIN reference_corpus_libraries lib ON lib.library_id = lm.library_id
                JOIN reference_source_license lic ON lic.anchor_id = n.anchor_id
                WHERE n.node_type = $node_type
                  AND a.status = 'ready'
                  AND lm.enabled = 1
                  AND (lib.scope = 'global' OR (lib.scope = 'project' AND lib.novel_id = $novel_id))
                  AND lic.license_state IN ('public_domain', 'cc', 'authorized')
                  AND lic.reuse_policy IN ('verbatim_ok', 'adapted_only')
                  AND (a.novel_id = $novel_id OR ((a.novel_id IS NULL OR a.novel_id = 0) AND a.corpus_visibility = 'workspace'))
            """);
        AppendInClause(builder, parameters, "lm.library_id", libraryIds, "chapter_context_library_id");
        AppendInClause(builder, parameters, "lic.reuse_policy", reusePolicies, "chapter_context_reuse_policy");
        if (includeAnchorIds.Count > 0)
        {
            AppendInClause(builder, parameters, "n.anchor_id", includeAnchorIds, "chapter_context_include_anchor_id");
        }

        if (excludeAnchorIds.Count > 0)
        {
            AppendNotInClause(builder, parameters, "n.anchor_id", excludeAnchorIds, "chapter_context_exclude_anchor_id");
        }

        builder.AppendLine($"""
            ),
            source_representatives AS (
                SELECT library_id,
                       anchor_id,
                       dedup_group_key
                FROM (
                    SELECT library_id,
                           anchor_id,
                           dedup_group_key,
                           ROW_NUMBER() OVER (
                               PARTITION BY dedup_group_key
                               ORDER BY source_quality_rank DESC, library_id, anchor_id
                           ) AS dedup_rank
                    FROM (
                        SELECT DISTINCT library_id,
                               anchor_id,
                               dedup_group_key,
                               source_quality_rank
                        FROM eligible_nodes
                    )
                )
                WHERE dedup_rank = 1
            ),
            scoped_nodes AS (
                SELECT e.*
                FROM eligible_nodes e
                JOIN source_representatives r
                  ON r.library_id = e.library_id
                 AND r.anchor_id = e.anchor_id
                 AND r.dedup_group_key = e.dedup_group_key
            )
            SELECT n.node_id
            FROM scoped_nodes n
            WHERE ({contextExpression})
              AND ({scoreExpression}) >= $min_chapter_context_route_score
            """);
        AppendStructuredObservationFilters(builder, parameters, filters);
        builder.AppendLine($"""
            ORDER BY ({scoreExpression}) DESC,
                     n.source_quality_rank DESC,
                     n.sequence_index,
                     n.node_id
            LIMIT $limit;
            """);

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            nodeIds.Add(reader.GetString(0));
        }

        return new ChapterContextRecallResult(nodeIds);
    }

    private static async ValueTask<StructuredObservationRecallResult> ReadStructuredObservationRecallNodeIdsAsync(
        SqliteConnection connection,
        ReferenceCorpusQueryContextPayload queryContext,
        string requestedNodeType,
        IReadOnlyDictionary<string, string> filters,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var queryTerms = BuildQueryTerms(queryContext)
            .Where(term => term.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(32)
            .ToArray();
        var hasFilterRoute = HasStructuredObservationRouteFilters(filters);
        if (queryTerms.Length == 0 && !hasFilterRoute)
        {
            return new StructuredObservationRecallResult(new HashSet<string>(StringComparer.Ordinal));
        }

        var libraryIds = await ResolveEffectiveLibraryIdsAsync(
            connection,
            queryContext.Scope,
            queryContext.ChapterContext.NovelId,
            cancellationToken);
        if (libraryIds.Count == 0)
        {
            return new StructuredObservationRecallResult(new HashSet<string>(StringComparer.Ordinal));
        }

        var reusePolicies = NormalizeTextSet(queryContext.Scope.ReusePolicies);
        if (reusePolicies.Count == 0)
        {
            reusePolicies = [ReferenceCorpusReusePolicies.VerbatimOk, ReferenceCorpusReusePolicies.AdaptedOnly];
        }

        var includeAnchorIds = NormalizePositiveLongSet(queryContext.Scope.IncludeAnchorIds);
        var excludeAnchorIds = NormalizePositiveLongSet(queryContext.Scope.ExcludeAnchorIds);
        var parameters = new List<(string Name, object Value)>
        {
            ("$node_type", requestedNodeType),
            ("$novel_id", queryContext.ChapterContext.NovelId),
 ("$limit", Math.Clamp(pageSize, 1, RecallRouteLimit))
        };
        var observationExpression = BuildInAnyExpression(
            parameters,
            ["fo.feature_family", "fo.feature_key", "fo.value_text"],
            queryTerms,
            "structured_observation_route_term");

        var routeSelects = new List<string>(capacity: 2);
        if (observationExpression.Length > 0)
        {
            routeSelects.Add($"""
                SELECT n.node_id,
                       fo.confidence AS route_confidence,
                       n.source_quality_rank AS route_source_quality,
                       n.sequence_index AS route_sequence_index
                FROM scoped_nodes n
                JOIN reference_feature_observations fo
                  ON fo.node_id = n.node_id
                 AND fo.node_type = $node_type
                 AND fo.validity_state = 'active'
                WHERE ({observationExpression})
                """);
        }

        if (hasFilterRoute)
        {
            var filterRoute = new StringBuilder("""
                SELECT n.node_id,
                       1.0 AS route_confidence,
                       n.source_quality_rank AS route_source_quality,
                       n.sequence_index AS route_sequence_index
                FROM scoped_nodes n
                WHERE 1 = 1
                """);
            filterRoute.AppendLine();
            AppendStructuredObservationFilters(filterRoute, parameters, filters);
            routeSelects.Add(filterRoute.ToString());
        }

        if (routeSelects.Count == 0)
        {
            return new StructuredObservationRecallResult(new HashSet<string>(StringComparer.Ordinal));
        }

        var builder = new StringBuilder("""
            WITH eligible_nodes AS (
                SELECT n.node_id,
                       n.anchor_id,
                       n.sequence_index,
                       lm.library_id,
                       COALESCE(NULLIF(TRIM(lm.dedup_group_id), ''), 'anchor:' || n.anchor_id) AS dedup_group_key,
                       CASE COALESCE(lm.source_quality, '')
                           WHEN 'trusted' THEN 3
                           WHEN 'normal' THEN 2
                           WHEN 'low' THEN 1
                           ELSE 0
                       END AS source_quality_rank,
                       lic.reuse_policy
                FROM reference_text_nodes n
                JOIN reference_anchors a ON a.anchor_id = n.anchor_id
                JOIN reference_library_members lm ON lm.anchor_id = n.anchor_id
                JOIN reference_corpus_libraries lib ON lib.library_id = lm.library_id
                JOIN reference_source_license lic ON lic.anchor_id = n.anchor_id
                WHERE n.node_type = $node_type
                  AND a.status = 'ready'
                  AND lm.enabled = 1
                  AND (lib.scope = 'global' OR (lib.scope = 'project' AND lib.novel_id = $novel_id))
                  AND lic.license_state IN ('public_domain', 'cc', 'authorized')
                  AND lic.reuse_policy IN ('verbatim_ok', 'adapted_only')
                  AND (a.novel_id = $novel_id OR ((a.novel_id IS NULL OR a.novel_id = 0) AND a.corpus_visibility = 'workspace'))
            """);
        AppendInClause(builder, parameters, "lm.library_id", libraryIds, "structured_observation_library_id");
        AppendInClause(builder, parameters, "lic.reuse_policy", reusePolicies, "structured_observation_reuse_policy");
        if (includeAnchorIds.Count > 0)
        {
            AppendInClause(builder, parameters, "n.anchor_id", includeAnchorIds, "structured_observation_include_anchor_id");
        }

        if (excludeAnchorIds.Count > 0)
        {
            AppendNotInClause(builder, parameters, "n.anchor_id", excludeAnchorIds, "structured_observation_exclude_anchor_id");
        }

        builder.AppendLine("""
            ),
            source_representatives AS (
                SELECT library_id,
                       anchor_id,
                       dedup_group_key
                FROM (
                    SELECT library_id,
                           anchor_id,
                           dedup_group_key,
                           ROW_NUMBER() OVER (
                               PARTITION BY dedup_group_key
                               ORDER BY source_quality_rank DESC, library_id, anchor_id
                           ) AS dedup_rank
                    FROM (
                        SELECT DISTINCT library_id,
                               anchor_id,
                               dedup_group_key,
                               source_quality_rank
                        FROM eligible_nodes
                    )
                )
                WHERE dedup_rank = 1
            ),
            scoped_nodes AS (
                SELECT e.*
                FROM eligible_nodes e
                JOIN source_representatives r
                  ON r.library_id = e.library_id
                 AND r.anchor_id = e.anchor_id
                 AND r.dedup_group_key = e.dedup_group_key
            )
            """);
        builder.AppendLine("SELECT node_id");
        builder.AppendLine("FROM (");
        builder.AppendLine(string.Join(
            Environment.NewLine + "UNION ALL" + Environment.NewLine,
            routeSelects));
        builder.AppendLine("""
            ) route_hits
            GROUP BY node_id
            ORDER BY MAX(route_confidence) DESC,
                     MAX(route_source_quality) DESC,
                     MIN(route_sequence_index),
                     node_id
            LIMIT $limit;
            """);

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var nodeIds = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            nodeIds.Add(reader.GetString(0));
        }

        return new StructuredObservationRecallResult(nodeIds);
    }

    private static string BuildNativeTechniqueRouteSql(
        List<(string Name, object Value)> parameters,
        NativeTechniqueRecallResult? nativeTechniqueRecall)
    {
        if (nativeTechniqueRecall is null || nativeTechniqueRecall.NodeIds.Count == 0)
        {
            return string.Empty;
        }

        var names = new List<string>(nativeTechniqueRecall.NodeIds.Count);
        var index = 0;
        foreach (var nodeId in nativeTechniqueRecall.NodeIds.Order(StringComparer.Ordinal))
        {
            var name = "$native_technique_node_id_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters.Add((name, nodeId));
            names.Add(name);
            index++;
        }

        return "OR scoped_nodes.node_id IN (" + string.Join(", ", names) + ")";
    }

    private static string BuildStructuredObservationRouteSql(
        List<(string Name, object Value)> parameters,
        StructuredObservationRecallResult? structuredObservationRecall)
    {
        if (structuredObservationRecall is null || structuredObservationRecall.NodeIds.Count == 0)
        {
            return string.Empty;
        }

        var names = new List<string>(structuredObservationRecall.NodeIds.Count);
        var index = 0;
        foreach (var nodeId in structuredObservationRecall.NodeIds.Order(StringComparer.Ordinal))
        {
            var name = "$structured_observation_node_id_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters.Add((name, nodeId));
            names.Add(name);
            index++;
        }

        return "OR scoped_nodes.node_id IN (" + string.Join(", ", names) + ")";
    }

    private static string BuildChapterContextRouteSql(
        List<(string Name, object Value)> parameters,
        ChapterContextRecallResult? chapterContextRecall)
    {
        if (chapterContextRecall is null || chapterContextRecall.NodeIds.Count == 0)
        {
            return string.Empty;
        }

        var names = new List<string>(chapterContextRecall.NodeIds.Count);
        var index = 0;
        foreach (var nodeId in chapterContextRecall.NodeIds.Order(StringComparer.Ordinal))
        {
            var name = "$chapter_context_node_id_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters.Add((name, nodeId));
            names.Add(name);
            index++;
        }

        return "OR scoped_nodes.node_id IN (" + string.Join(", ", names) + ")";
    }

    private static string RequestedNodeType(IReadOnlyDictionary<string, string> filters)
    {
        var requestedNodeType = filters.TryGetValue("node_type", out var nodeType) && !string.IsNullOrWhiteSpace(nodeType)
            ? nodeType.Trim()
            : ReferenceCorpusNodeTypes.Sentence;
        return NormalizeRequestedNodeType(requestedNodeType);
    }

    private static string NormalizeRequestedNodeType(string? nodeType)
    {
        var requestedNodeType = string.IsNullOrWhiteSpace(nodeType)
            ? ReferenceCorpusNodeTypes.Sentence
            : nodeType.Trim();
        if (!ReferenceCorpusNodeTypes.All.Contains(requestedNodeType, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Unsupported reference corpus node_type filter '{requestedNodeType}'.");
        }

        return requestedNodeType;
    }

    private static string BuildLikeAnyExpression(
        List<(string Name, object Value)> parameters,
        string columnName,
        IReadOnlyList<string> values,
        string parameterPrefix)
    {
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var clauses = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var name = "$" + parameterPrefix + "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters.Add((name, EscapeLikePattern(values[index])));
            clauses.Add(columnName + " LIKE '%' || " + name + " || '%' ESCAPE '\\'");
        }

        return string.Join(" OR ", clauses);
    }

    private static string BuildWeightedLikeScoreExpression(
        List<(string Name, object Value)> parameters,
        string columnName,
        IReadOnlyList<LocalContextTerm> terms,
        string parameterPrefix)
    {
        if (terms.Count == 0)
        {
            return "0";
        }

        var clauses = new List<string>(terms.Count);
        for (var index = 0; index < terms.Count; index++)
        {
            var termName = "$" + parameterPrefix + "_term_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var weightName = "$" + parameterPrefix + "_weight_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters.Add((termName, EscapeLikePattern(terms[index].Text)));
            parameters.Add((weightName, terms[index].Weight));
            clauses.Add("CASE WHEN " + columnName + " LIKE '%' || " + termName + " || '%' ESCAPE '\\' THEN " + weightName + " ELSE 0 END");
        }

        return string.Join(" + ", clauses);
    }

    private static string BuildInAnyExpression(
        List<(string Name, object Value)> parameters,
        IReadOnlyList<string> columnNames,
        IReadOnlyList<string> values,
        string parameterPrefix)
    {
        if (values.Count == 0 || columnNames.Count == 0)
        {
            return string.Empty;
        }

        var parameterNames = new List<string>(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var name = "$" + parameterPrefix + "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            parameters.Add((name, values[index]));
            parameterNames.Add(name);
        }

        var parameterList = string.Join(", ", parameterNames);
        return string.Join(" OR ", columnNames.Select(columnName => columnName + " IN (" + parameterList + ")"));
    }

    private static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    private static void ValidateCandidateSearchFilters(IReadOnlyDictionary<string, string> filters)
    {
        foreach (var key in filters.Keys)
        {
            if (key is not "node_type" and
                not "feature_family" and
                not "feature_key" and
                not "feature_value_text" and
                not "feature_value_num_min" and
                not "feature_value_num_max" and
                not "sensory_sense" and
                not "sensory_min_intensity" and
                not "sensory_max_intensity" &&
                !TryParseIndexedFeatureFilterKey(key, out _, out _))
            {
                throw new ArgumentException($"Unsupported reference corpus candidate filter '{key}'.");
            }
        }
    }

    private static void AppendStructuredObservationFilters(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        IReadOnlyDictionary<string, string> filters)
    {
        var hasFeatureFilter = HasLegacyFeatureFilter(filters);
        if (hasFeatureFilter)
        {
            builder.AppendLine("""
                AND EXISTS (
                    SELECT 1
                    FROM reference_feature_observations fo_filter
                    WHERE fo_filter.node_id = n.node_id
                      AND fo_filter.validity_state = 'active'
                """);
            AppendOptionalTextFilter(builder, parameters, filters, "feature_family", "fo_filter.feature_family", "$filter_feature_family");
            AppendOptionalTextFilter(builder, parameters, filters, "feature_key", "fo_filter.feature_key", "$filter_feature_key");
            AppendOptionalTextFilter(builder, parameters, filters, "feature_value_text", "fo_filter.value_text", "$filter_feature_value_text");
            AppendOptionalDoubleLowerBound(builder, parameters, filters, "feature_value_num_min", "fo_filter.value_num", "$filter_feature_value_num_min");
            AppendOptionalDoubleUpperBound(builder, parameters, filters, "feature_value_num_max", "fo_filter.value_num", "$filter_feature_value_num_max");
            builder.AppendLine(")");
        }

        foreach (var indexedFilter in ReadIndexedFeatureFilters(filters))
        {
            var indexText = indexedFilter.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var alias = "fo_filter_" + indexText;
            var parameterPrefix = "$filter_feature_" + indexText;
            builder.AppendLine($"""
                AND EXISTS (
                    SELECT 1
                    FROM reference_feature_observations {alias}
                    WHERE {alias}.node_id = n.node_id
                      AND {alias}.validity_state = 'active'
                """);
            AppendOptionalTextFilter(builder, parameters, filters, indexedFilter.FamilyKey, alias + ".feature_family", parameterPrefix + "_family");
            AppendOptionalTextFilter(builder, parameters, filters, indexedFilter.FeatureKey, alias + ".feature_key", parameterPrefix + "_key");
            AppendOptionalTextFilter(builder, parameters, filters, indexedFilter.ValueTextKey, alias + ".value_text", parameterPrefix + "_value_text");
            AppendOptionalDoubleLowerBound(builder, parameters, filters, indexedFilter.ValueNumMinKey, alias + ".value_num", parameterPrefix + "_value_num_min");
            AppendOptionalDoubleUpperBound(builder, parameters, filters, indexedFilter.ValueNumMaxKey, alias + ".value_num", parameterPrefix + "_value_num_max");
            builder.AppendLine(")");
        }

        var hasSensoryFilter =
            HasSensoryFilter(filters);
        if (hasSensoryFilter)
        {
            builder.AppendLine("""
                AND EXISTS (
                    SELECT 1
                    FROM reference_obs_sensory sensory_filter
                    WHERE sensory_filter.node_id = n.node_id
                """);
            AppendOptionalTextFilter(builder, parameters, filters, "sensory_sense", "sensory_filter.sense", "$filter_sensory_sense");
            AppendOptionalDoubleLowerBound(builder, parameters, filters, "sensory_min_intensity", "sensory_filter.intensity", "$filter_sensory_min_intensity");
            AppendOptionalDoubleUpperBound(builder, parameters, filters, "sensory_max_intensity", "sensory_filter.intensity", "$filter_sensory_max_intensity");
            builder.AppendLine(")");
        }
    }

    private static bool HasStructuredObservationRouteFilters(IReadOnlyDictionary<string, string> filters)
    {
        return HasLegacyFeatureFilter(filters) ||
            ReadIndexedFeatureFilters(filters).Count > 0 ||
            HasSensoryFilter(filters);
    }

    private static bool HasLegacyFeatureFilter(IReadOnlyDictionary<string, string> filters)
    {
        return HasFilterValue(filters, "feature_family") ||
            HasFilterValue(filters, "feature_key") ||
            HasFilterValue(filters, "feature_value_text") ||
            HasFilterValue(filters, "feature_value_num_min") ||
            HasFilterValue(filters, "feature_value_num_max");
    }

    private static bool HasSensoryFilter(IReadOnlyDictionary<string, string> filters)
    {
        return HasFilterValue(filters, "sensory_sense") ||
            HasFilterValue(filters, "sensory_min_intensity") ||
            HasFilterValue(filters, "sensory_max_intensity");
    }

    private static IReadOnlyList<IndexedFeatureFilter> ReadIndexedFeatureFilters(IReadOnlyDictionary<string, string> filters)
    {
        var indexes = filters.Keys
            .Select(key => TryParseIndexedFeatureFilterKey(key, out var index, out _) ? index : -1)
            .Where(index => index >= 0)
            .Distinct()
            .Order()
            .ToArray();

        return indexes
            .Select(index => new IndexedFeatureFilter(
                index,
                IndexedFeatureFilterKey(index, "family"),
                IndexedFeatureFilterKey(index, "key"),
                IndexedFeatureFilterKey(index, "value_text"),
                IndexedFeatureFilterKey(index, "value_num_min"),
                IndexedFeatureFilterKey(index, "value_num_max")))
            .Where(item =>
                HasFilterValue(filters, item.FamilyKey) ||
                HasFilterValue(filters, item.FeatureKey) ||
                HasFilterValue(filters, item.ValueTextKey) ||
                HasFilterValue(filters, item.ValueNumMinKey) ||
                HasFilterValue(filters, item.ValueNumMaxKey))
            .ToArray();
    }

    private static bool TryParseIndexedFeatureFilterKey(string key, out int index, out string suffix)
    {
        const string prefix = "feature_filter_";
        index = -1;
        suffix = string.Empty;
        if (!key.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = key[prefix.Length..];
        var separator = rest.IndexOf('_', StringComparison.Ordinal);
        if (separator <= 0)
        {
            return false;
        }

        var indexText = rest[..separator];
        suffix = rest[(separator + 1)..];
        if (!int.TryParse(indexText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out index) ||
            index < 0 ||
            index > 15)
        {
            index = -1;
            return false;
        }

        return suffix is "family" or "key" or "value_text" or "value_num_min" or "value_num_max";
    }

    private static string IndexedFeatureFilterKey(int index, string suffix)
    {
        return "feature_filter_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "_" + suffix;
    }

    private static bool HasFilterValue(IReadOnlyDictionary<string, string> filters, string key)
    {
        return filters.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value);
    }

    private static void AppendOptionalTextFilter(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        IReadOnlyDictionary<string, string> filters,
        string filterKey,
        string column,
        string parameterName)
    {
        if (!filters.TryGetValue(filterKey, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.AppendLine($"  AND {column} = {parameterName}");
        parameters.Add((parameterName, value.Trim()));
    }

    private static void AppendOptionalDoubleLowerBound(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        IReadOnlyDictionary<string, string> filters,
        string filterKey,
        string column,
        string parameterName)
    {
        if (!TryReadDoubleFilter(filters, filterKey, out var value))
        {
            return;
        }

        builder.AppendLine($"  AND {column} >= {parameterName}");
        parameters.Add((parameterName, value));
    }

    private static void AppendOptionalDoubleUpperBound(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        IReadOnlyDictionary<string, string> filters,
        string filterKey,
        string column,
        string parameterName)
    {
        if (!TryReadDoubleFilter(filters, filterKey, out var value))
        {
            return;
        }

        builder.AppendLine($"  AND {column} <= {parameterName}");
        parameters.Add((parameterName, value));
    }

    private static bool TryReadDoubleFilter(
        IReadOnlyDictionary<string, string> filters,
        string filterKey,
        out double value)
    {
        value = 0;
        if (!filters.TryGetValue(filterKey, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        if (double.TryParse(
                raw.Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out value))
        {
            return true;
        }

        throw new ArgumentException($"Filter '{filterKey}' must be a number.");
    }

    private static async ValueTask<HashSet<string>> ResolveEffectiveLibraryIdsAsync(
        SqliteConnection connection,
        ReferenceCorpusScopePayload scope,
        long novelId,
        CancellationToken cancellationToken)
    {
        var explicitLibraryIds = NormalizeTextSet(scope.LibraryIds);
        if (explicitLibraryIds.Count > 0)
        {
            return explicitLibraryIds;
        }

var sessionId = NormalizeSessionId(scope.SessionId, novelId);
var bound = await ReadSessionLibraryIdsAsync(connection, sessionId, novelId, cancellationToken);
 var explicitScope = await HasExplicitSessionLibraryScopeAsync(connection, sessionId, cancellationToken);
 if (!explicitScope && IsDefaultProjectSession(sessionId, novelId))
        {
            foreach (var libraryId in await ReadGlobalWorkspaceLibraryIdsAsync(connection, cancellationToken))
            {
                bound.Add(libraryId);
            }
        }

 if (bound.Count > 0 || explicitScope)
        {
            return bound;
        }

        return await ReadConventionalDefaultLibraryIdsAsync(connection, novelId, cancellationToken);
    }

private static async ValueTask<HashSet<string>> ReadSessionLibraryIdsAsync(
        SqliteConnection connection,
        string sessionId,
        long novelId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT b.library_id
            FROM reference_session_library_binding b
            JOIN reference_corpus_libraries lib ON lib.library_id = b.library_id
            WHERE b.session_id = $session_id
              AND (lib.scope = 'global' OR (lib.scope = 'project' AND lib.novel_id = $novel_id))
            ORDER BY b.library_id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId);
        command.Parameters.AddWithValue("$novel_id", novelId);
return await ReadLibraryIdSetAsync(command, cancellationToken);
}

 public async ValueTask<ReferenceCorpusProjectionRebuildPayload> RebuildSensoryProjectionAsync(
 RebuildReferenceCorpusSensoryProjectionPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 cancellationToken.ThrowIfCancellationRequested();

 await _mutex.WaitAsync(cancellationToken);
 try
 {
 var databasePath = await DatabasePathAsync(cancellationToken);
 await EnsureSchemaAsync(databasePath, cancellationToken);
 await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
 await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

 await using (var delete = connection.CreateCommand())
 {
 delete.Transaction = transaction;
 delete.CommandText = input.AnchorId is null
 ? "DELETE FROM reference_obs_sensory;"
 : "DELETE FROM reference_obs_sensory WHERE anchor_id=$anchor_id;";
 if (input.AnchorId is { } anchorId)
 {
 delete.Parameters.AddWithValue("$anchor_id", anchorId);
 }

 await delete.ExecuteNonQueryAsync(cancellationToken);
 }

 var observations = new List<(string ObservationId, string NodeId, long AnchorId, string ValueJson)>();
 await using (var read = connection.CreateCommand())
 {
 read.Transaction = transaction;
 read.CommandText = """
 SELECT observation_id,node_id,anchor_id,value_json
 FROM reference_feature_observations
 WHERE feature_family='sensory'
 AND validity_state='active'
 AND superseded_by_run_id IS NULL
 AND value_json IS NOT NULL
 AND ($anchor_id IS NULL OR anchor_id=$anchor_id)
 ORDER BY anchor_id,node_id,observation_id;
""";
 read.Parameters.AddWithValue("$anchor_id", input.AnchorId is { } anchorId ? anchorId : DBNull.Value);
 await using var reader = await read.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 observations.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetString(3)));
 }
 }

 var projectionRows = 0;
 var invalidObservations = 0;
 foreach (var observation in observations)
 {
 if (!TryReadSensoryProjectionItems(observation.ValueJson, out var items))
 {
 invalidObservations++;
 continue;
 }

 foreach (var item in items)
 {
 await using var insert = connection.CreateCommand();
 insert.Transaction = transaction;
 insert.CommandText = """
 INSERT INTO reference_obs_sensory(observation_id,node_id,anchor_id,sense,intensity)
 VALUES($observation_id,$node_id,$anchor_id,$sense,$intensity);
 """;
 insert.Parameters.AddWithValue("$observation_id", observation.ObservationId);
 insert.Parameters.AddWithValue("$node_id", observation.NodeId);
 insert.Parameters.AddWithValue("$anchor_id", observation.AnchorId);
 insert.Parameters.AddWithValue("$sense", item.Sense);
 insert.Parameters.AddWithValue("$intensity", item.Intensity);
 await insert.ExecuteNonQueryAsync(cancellationToken);
 projectionRows++;
 }
 }

 await transaction.CommitAsync(cancellationToken);
 return new(observations.Count, projectionRows, invalidObservations);
 }
 finally
 {
 _mutex.Release();
 }
 }

 public async ValueTask<ReferenceCorpusNodeWindowPayload?> GetNodeWindowAsync(
 GetReferenceCorpusNodeWindowPayload input,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(input);
 ArgumentException.ThrowIfNullOrWhiteSpace(input.NodeId);
 if (input.AnchorId <= 0 || input.PreviousChapterCount is < 0 or > 20 ||
 input.NextChapterCount is < 0 or > 20 || input.MaxNodes is < 1 or > 1000)
 {
 throw new ArgumentOutOfRangeException(nameof(input));
 }

 var databasePath = await DatabasePathAsync(cancellationToken);
 await EnsureSchemaAsync(databasePath, cancellationToken);
 await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);

 int? chapterIndex;
 await using (var focus = connection.CreateCommand())
 {
 focus.CommandText = "SELECT chapter_index FROM reference_text_nodes WHERE anchor_id=$anchor_id AND node_id=$node_id;";
 focus.Parameters.AddWithValue("$anchor_id", input.AnchorId);
 focus.Parameters.AddWithValue("$node_id", input.NodeId.Trim());
 var value = await focus.ExecuteScalarAsync(cancellationToken);
 if (value is null)
 {
 return null;
 }

 chapterIndex = value is DBNull ? null : Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
 }

 string? sceneNodeId = null;
 if (input.IncludeSceneSiblings)
 {
 await using var scene = connection.CreateCommand();
 scene.CommandText = """
 WITH RECURSIVE ancestors(node_id,parent_node_id,node_type,depth) AS (
 SELECT node_id,parent_node_id,node_type,0
 FROM reference_text_nodes
 WHERE anchor_id=$anchor_id AND node_id=$node_id
 UNION ALL
 SELECT parent.node_id,parent.parent_node_id,parent.node_type,ancestors.depth+1
 FROM reference_text_nodes parent
 JOIN ancestors ON ancestors.parent_node_id=parent.node_id
 WHERE parent.anchor_id=$anchor_id
 )
 SELECT node_id FROM ancestors WHERE node_type='scene' ORDER BY depth LIMIT 1;
 """;
 scene.Parameters.AddWithValue("$anchor_id", input.AnchorId);
 scene.Parameters.AddWithValue("$node_id", input.NodeId.Trim());
 sceneNodeId = await scene.ExecuteScalarAsync(cancellationToken) as string;
 }

 var chapterNodes = chapterIndex is null
 ? []
 : await ReadNodeWindowItemsAsync(
 connection,
 "anchor_id=$anchor_id AND chapter_index BETWEEN $chapter_min AND $chapter_max",
 [("$anchor_id", input.AnchorId), ("$chapter_min", chapterIndex.Value - input.PreviousChapterCount), ("$chapter_max", chapterIndex.Value + input.NextChapterCount)],
 input.MaxNodes + 1,
 cancellationToken);
 var sceneSiblings = sceneNodeId is null
 ? []
 : await ReadNodeWindowItemsAsync(
 connection,
 "anchor_id=$anchor_id AND parent_node_id=$scene_node_id",
 [("$anchor_id", input.AnchorId), ("$scene_node_id", sceneNodeId)],
 input.MaxNodes + 1,
 cancellationToken);
 var truncated = chapterNodes.Count > input.MaxNodes || sceneSiblings.Count > input.MaxNodes;
 return new(
 input.NodeId.Trim(),
 chapterIndex,
 sceneNodeId,
 chapterNodes.Take(input.MaxNodes).ToArray(),
 sceneSiblings.Take(input.MaxNodes).ToArray(),
 truncated);
 }

 private static bool TryReadSensoryProjectionItems(
 string valueJson,
 out IReadOnlyList<(string Sense, double Intensity)> items)
 {
 try
 {
 using var document = JsonDocument.Parse(valueJson);
 if (document.RootElement.ValueKind != JsonValueKind.Array)
 {
 items = [];
 return false;
 }

 var parsed = new List<(string Sense, double Intensity)>();
 foreach (var item in document.RootElement.EnumerateArray())
 {
 if (!item.TryGetProperty("sense", out var senseElement) ||
 senseElement.ValueKind != JsonValueKind.String ||
 string.IsNullOrWhiteSpace(senseElement.GetString()) ||
 !item.TryGetProperty("intensity", out var intensityElement) ||
 !intensityElement.TryGetDouble(out var intensity) ||
 !double.IsFinite(intensity))
 {
 items = [];
 return false;
 }

 parsed.Add((senseElement.GetString()!, intensity));
 }

 items = parsed;
 return true;
 }
 catch (JsonException)
 {
 items = [];
 return false;
 }
 }

 private static async ValueTask<IReadOnlyList<ReferenceCorpusNodeWindowItemPayload>> ReadNodeWindowItemsAsync(
 SqliteConnection connection,
 string predicate,
 IReadOnlyList<(string Name, object Value)> parameters,
 int limit,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = $"""
 SELECT node_id,parent_node_id,node_type,chapter_index,sequence_index,start_offset,end_offset,text_hash,text
 FROM reference_text_nodes
 WHERE {predicate}
 ORDER BY chapter_index,sequence_index,node_id
 LIMIT $limit;
 """;
 foreach (var parameter in parameters)
 {
 command.Parameters.AddWithValue(parameter.Name, parameter.Value);
 }
 command.Parameters.AddWithValue("$limit", limit);

 var items = new List<ReferenceCorpusNodeWindowItemPayload>();
 await using var reader = await command.ExecuteReaderAsync(cancellationToken);
 while (await reader.ReadAsync(cancellationToken))
 {
 items.Add(new(
 reader.GetString(0),
 reader.IsDBNull(1) ? null : reader.GetString(1),
 reader.GetString(2),
 reader.IsDBNull(3) ? null : reader.GetInt32(3),
 reader.GetInt32(4),
 reader.GetInt32(5),
 reader.GetInt32(6),
 reader.GetString(7),
 reader.GetString(8)));
 }

 return items;
 }

 private static async ValueTask<bool> HasExplicitSessionLibraryScopeAsync(
 SqliteConnection connection,
 string sessionId,
 CancellationToken cancellationToken)
 {
 await using var command = connection.CreateCommand();
 command.CommandText = "SELECT is_explicit FROM reference_session_library_scope_state WHERE session_id=$session_id;";
 command.Parameters.AddWithValue("$session_id", sessionId);
 var value = await command.ExecuteScalarAsync(cancellationToken);
 return value is not null && value is not DBNull && Convert.ToInt32(value) == 1;
 }

    private static async ValueTask<HashSet<string>> ReadGlobalWorkspaceLibraryIdsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT library_id
            FROM reference_corpus_libraries
            WHERE scope = 'global'
            ORDER BY library_id;
            """;
        return await ReadLibraryIdSetAsync(command, cancellationToken);
    }

    private static async ValueTask<HashSet<string>> ReadConventionalDefaultLibraryIdsAsync(
        SqliteConnection connection,
        long novelId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT library_id
            FROM reference_corpus_libraries
            WHERE library_id = $project_library_id
               OR scope = 'global'
            ORDER BY library_id;
            """;
        command.Parameters.AddWithValue("$project_library_id", BuildDefaultProjectSessionId(novelId));
        return await ReadLibraryIdSetAsync(command, cancellationToken);
    }

    private static async ValueTask<HashSet<string>> ReadLibraryIdSetAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(0))
            {
                var libraryId = reader.GetString(0).Trim();
                if (libraryId.Length > 0)
                {
                    result.Add(libraryId);
                }
            }
        }

        return result;
    }

    private static string NormalizeSessionId(string? sessionId, long novelId)
    {
        return string.IsNullOrWhiteSpace(sessionId)
            ? BuildDefaultProjectSessionId(novelId)
            : sessionId.Trim();
    }

    private static string BuildDefaultProjectSessionId(long novelId)
    {
        return "project:" + novelId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":default";
    }

    private static bool IsDefaultProjectSession(string sessionId, long novelId)
    {
        return string.Equals(sessionId, BuildDefaultProjectSessionId(novelId), StringComparison.Ordinal);
    }

    private async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<float>>> EnsureNodeEmbeddingsAsync(
        SqliteConnection connection,
        IReadOnlyList<CorpusCandidateNode> candidates,
        EmbeddingRequestOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        var dimensions = embeddingOptions.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
        var existing = await ReadNodeEmbeddingsAsync(connection, candidates, embeddingOptions, dimensions, cancellationToken);
        var missing = candidates
            .Where(candidate => !existing.ContainsKey(candidate.NodeId))
            .ToArray();
        if (missing.Length == 0)
        {
            return existing;
        }

        var response = await _embeddings.EmbedAsync(
            missing.Select(candidate => candidate.Text).ToArray(),
            embeddingOptions with
            {
                Dimensions = dimensions,
                InputKind = BuiltinOnnxEmbeddingModel.DocumentInputKind
            },
            cancellationToken);
        if (response.Items.Count != missing.Length)
        {
            throw new InvalidOperationException("Reference corpus node embedding response count does not match the requested batch.");
        }

        var merged = new Dictionary<string, IReadOnlyList<float>>(existing, StringComparer.Ordinal);
        foreach (var item in response.Items.OrderBy(item => item.Index))
        {
            if (item.Index < 0 || item.Index >= missing.Length)
            {
                throw new InvalidOperationException("Reference corpus node embedding response index is outside the requested batch.");
            }

            if (item.Vector.Count != dimensions)
            {
                throw new InvalidOperationException("Reference corpus node embedding dimensions are inconsistent.");
            }

            var candidate = missing[item.Index];
            await UpsertNodeEmbeddingAsync(
                connection,
                candidate,
                embeddingOptions,
                dimensions,
                item.Vector,
                cancellationToken);
            merged[candidate.NodeId] = item.Vector;
        }

        return merged;
    }

    private static async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<float>>> ReadNodeEmbeddingsAsync(
        SqliteConnection connection,
        IReadOnlyList<CorpusCandidateNode> candidates,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        }

        var nodeIds = candidates.Select(candidate => candidate.NodeId).Distinct(StringComparer.Ordinal).ToArray();
        var builder = new StringBuilder("""
            SELECT node_id, embedding_json
            FROM reference_text_node_embeddings
            WHERE provider_key = $provider_key
              AND model_id = $model_id
              AND dimensions = $dimensions
            """);
        var parameters = new List<(string Name, object Value)>
        {
            ("$provider_key", embeddingOptions.ProviderKey),
            ("$model_id", embeddingOptions.ModelId),
            ("$dimensions", dimensions)
        };
        AppendInClause(builder, parameters, "node_id", nodeIds, "node_id");

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var result = new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var nodeId = reader.GetString(0);
            var vector = DeserializeVector(reader.GetString(1));
            if (vector.Count == dimensions)
            {
                result[nodeId] = vector;
            }
        }

        return result;
    }

    private static async ValueTask UpsertNodeEmbeddingAsync(
        SqliteConnection connection,
        CorpusCandidateNode candidate,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        IReadOnlyList<float> vector,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_text_node_embeddings
              (embedding_id, node_id, anchor_id, provider_key, model_id, dimensions,
               text_hash, embedding_json, updated_at)
            VALUES
              ($embedding_id, $node_id, $anchor_id, $provider_key, $model_id, $dimensions,
               $text_hash, $embedding_json, $updated_at)
            ON CONFLICT(node_id, provider_key, model_id, dimensions) DO UPDATE SET
              anchor_id = excluded.anchor_id,
              text_hash = excluded.text_hash,
              embedding_json = excluded.embedding_json,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$embedding_id", StableHash(
            "reference_text_node_embedding",
            candidate.NodeId,
            embeddingOptions.ProviderKey,
            embeddingOptions.ModelId,
            dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        command.Parameters.AddWithValue("$node_id", candidate.NodeId);
        command.Parameters.AddWithValue("$anchor_id", candidate.AnchorId);
        command.Parameters.AddWithValue("$provider_key", embeddingOptions.ProviderKey);
        command.Parameters.AddWithValue("$model_id", embeddingOptions.ModelId);
        command.Parameters.AddWithValue("$dimensions", dimensions);
        command.Parameters.AddWithValue("$text_hash", candidate.TextHash);
        command.Parameters.AddWithValue("$embedding_json", JsonSerializer.Serialize(vector, JsonOptions));
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask<NativeTechniqueRecallResult?> TryRecallNativeTechniqueNodesAsync(
        string databasePath,
        SqliteConnection connection,
        ReferenceCorpusQueryContextPayload queryContext,
        EmbeddingRequestOptions embeddingOptions,
        IReadOnlyList<float>? queryEmbedding,
        NormalizedPageRequest page,
        CancellationToken cancellationToken)
    {
        if (_techniqueVectorProvisioner is null ||
            _techniqueVectorQueryProvider is null ||
            queryEmbedding is null ||
            queryEmbedding.Count == 0)
        {
            return null;
        }

        try
        {
            var dimensions = embeddingOptions.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
            if (queryEmbedding.Count != dimensions)
            {
                return null;
            }

            var requestedNodeType = RequestedNodeType(page.Filters);
            var index = await EnsureNativeTechniqueIndexAsync(
                databasePath,
                connection,
                queryContext,
                embeddingOptions,
                requestedNodeType,
                cancellationToken);
            if (index.Status == ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Empty)
            {
                return new NativeTechniqueRecallResult(new HashSet<string>(StringComparer.Ordinal));
            }

            if (index.Status != ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Ready ||
                string.IsNullOrWhiteSpace(index.IndexScopeKey) ||
                string.IsNullOrWhiteSpace(index.TableName))
            {
                return null;
            }

 var topK = Math.Clamp(RecallRouteLimit * NativeTechniqueRecallOverfetchMultiplier, 16, 256);
            var records = await _techniqueVectorQueryProvider.SearchAsync(
                databasePath,
                new SqliteVecSearchRequest(index.TableName, dimensions, queryEmbedding, topK),
                cancellationToken);
            var nodeIds = await ReadNativeTechniqueRecallNodeIdsAsync(
                connection,
                index.IndexScopeKey,
                index.TableName,
                embeddingOptions,
                dimensions,
                records,
                cancellationToken);
            return new NativeTechniqueRecallResult(nodeIds.ToHashSet(StringComparer.Ordinal));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is InvalidOperationException or SqliteException or JsonException)
        {
            return null;
        }
    }

    private async ValueTask<NativeTechniqueIndexEnsureResult> EnsureNativeTechniqueIndexAsync(
        string databasePath,
        SqliteConnection connection,
        ReferenceCorpusQueryContextPayload queryContext,
        EmbeddingRequestOptions embeddingOptions,
        string requestedNodeType,
        CancellationToken cancellationToken)
    {
        var dimensions = embeddingOptions.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
        var indexScopeKey = BuildNativeTechniqueIndexScopeKey(queryContext, embeddingOptions, dimensions, requestedNodeType);
        var tableName = SqliteVecTableProvisioner.BuildReferenceTechniqueVectorTableName(indexScopeKey, dimensions);
        if (_techniqueVectorProvisioner is null)
        {
            return new NativeTechniqueIndexEnsureResult(
                ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Skipped,
                indexScopeKey,
                tableName,
                embeddingOptions.ProviderKey,
                embeddingOptions.ModelId,
                dimensions,
                SourceCount: 0,
                VectorCount: 0,
                SkippedVectorCount: 0,
                Rebuilt: false,
                Diagnostics: ["sqlite_vec_provisioner_missing"]);
        }

        var sources = await ReadScopedTechniqueSpecimensAsync(
            connection,
            queryContext,
            requestedNodeType,
            cancellationToken);
        if (sources.Count == 0)
        {
            await ClearNativeTechniqueIndexStateAsync(connection, indexScopeKey, cancellationToken);
            return new NativeTechniqueIndexEnsureResult(
                ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Empty,
                indexScopeKey,
                tableName,
                embeddingOptions.ProviderKey,
                embeddingOptions.ModelId,
                dimensions,
                SourceCount: 0,
                VectorCount: 0,
                SkippedVectorCount: 0,
                Rebuilt: false,
                Diagnostics: ["scope_has_no_active_technique_specimens"]);
        }

        var vectors = await EnsureTechniqueVectorCacheAsync(
            connection,
            sources,
            embeddingOptions,
            dimensions,
            cancellationToken);
        var entries = sources
            .Select((source, index) => new NativeTechniqueVectorIndexEntry(
                RowId: index + 1,
                Source: source,
                VectorId: TechniqueVectorId(source, embeddingOptions, dimensions),
                Vector: vectors.TryGetValue(source.SpecimenId, out var vector) ? vector : []))
            .Where(entry => entry.Vector.Count == dimensions)
            .ToArray();
        if (entries.Length == 0)
        {
            await ClearNativeTechniqueIndexStateAsync(connection, indexScopeKey, cancellationToken);
            return new NativeTechniqueIndexEnsureResult(
                ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Empty,
                indexScopeKey,
                tableName,
                embeddingOptions.ProviderKey,
                embeddingOptions.ModelId,
                dimensions,
                sources.Count,
                VectorCount: 0,
                SkippedVectorCount: sources.Count,
                Rebuilt: false,
                Diagnostics: ["scope_has_no_indexable_technique_vectors"]);
        }

        var sourceHash = NativeTechniqueIndexSourceHash(
            indexScopeKey,
            tableName,
            embeddingOptions,
            dimensions,
            entries);
        var isCurrent = await IsNativeTechniqueIndexCurrentAsync(
            connection,
            indexScopeKey,
            tableName,
            embeddingOptions,
            dimensions,
            sourceHash,
            entries,
            cancellationToken);
        if (!isCurrent)
        {
            await _techniqueVectorProvisioner.ProvisionAsync(
                databasePath,
                new SqliteVecProvisionRequest(
                    tableName,
                    dimensions,
                    SqliteVecTableProvisioner.BuildCreateTableSql(tableName, dimensions),
                    entries
                        .Select(entry => new SqliteVecVectorRecord(entry.RowId, entry.Source.SpecimenId, entry.Vector))
                        .ToArray()),
                cancellationToken);
            await ReplaceNativeTechniqueVectorRowsAsync(
                connection,
                indexScopeKey,
                tableName,
                embeddingOptions,
                dimensions,
                sourceHash,
                entries,
                cancellationToken);
        }

        return new NativeTechniqueIndexEnsureResult(
            ReferenceCorpusTechniqueVectorIndexBackfillStatuses.Ready,
            indexScopeKey,
            tableName,
            embeddingOptions.ProviderKey,
            embeddingOptions.ModelId,
            dimensions,
            sources.Count,
            entries.Length,
            sources.Count - entries.Length,
            Rebuilt: !isCurrent,
            Diagnostics: isCurrent ? ["native_technique_index_current"] : ["native_technique_index_rebuilt"]);
    }

    private static ReferenceCorpusTechniqueVectorIndexBackfillPayload ToBackfillPayload(
        NativeTechniqueIndexEnsureResult result)
    {
        return new ReferenceCorpusTechniqueVectorIndexBackfillPayload(
            result.Status,
            result.IndexScopeKey,
            result.TableName,
            result.ProviderKey,
            result.ModelId,
            result.Dimensions,
            result.SourceCount,
            result.VectorCount,
            result.SkippedVectorCount,
            result.Rebuilt,
            result.Diagnostics);
    }

    private async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<float>>> EnsureTechniqueVectorCacheAsync(
        SqliteConnection connection,
        IReadOnlyList<TechniqueSpecimenVectorSource> sources,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        CancellationToken cancellationToken)
    {
        var existing = await ReadTechniqueVectorEmbeddingsAsync(
            connection,
            sources,
            embeddingOptions,
            dimensions,
            cancellationToken);
        var missing = sources
            .Where(source => !existing.ContainsKey(source.SpecimenId))
            .ToArray();

        var merged = new Dictionary<string, IReadOnlyList<float>>(existing, StringComparer.Ordinal);
        if (missing.Length == 0)
        {
            return merged;
        }

        var response = await _embeddings.EmbedAsync(
            missing.Select(source => source.EmbeddingText).ToArray(),
            embeddingOptions with
            {
                Dimensions = dimensions,
                InputKind = BuiltinOnnxEmbeddingModel.DocumentInputKind
            },
            cancellationToken);
        if (response.Items.Count != missing.Length)
        {
            throw new InvalidOperationException("Reference corpus technique vector response count does not match the requested batch.");
        }

        foreach (var item in response.Items.OrderBy(item => item.Index))
        {
            if (item.Index < 0 || item.Index >= missing.Length)
            {
                throw new InvalidOperationException("Reference corpus technique vector response index is outside the requested batch.");
            }

            if (item.Vector.Count != dimensions)
            {
                throw new InvalidOperationException("Reference corpus technique vector dimensions are inconsistent.");
            }

            var source = missing[item.Index];
            await UpsertTechniqueVectorAsync(
                connection,
                source,
                embeddingOptions,
                dimensions,
                item.Vector,
                cancellationToken);
            merged[source.SpecimenId] = item.Vector;
        }

        return merged;
    }

    private async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<IReadOnlyList<float>>>> EnsureTechniqueVectorsAsync(
        SqliteConnection connection,
        IReadOnlyList<CorpusCandidateNode> candidates,
        EmbeddingRequestOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        var sources = await ReadActiveTechniqueSpecimensAsync(connection, candidates, cancellationToken);
        if (sources.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<IReadOnlyList<float>>>(StringComparer.Ordinal);
        }

        var dimensions = embeddingOptions.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
        var merged = await EnsureTechniqueVectorCacheAsync(
            connection,
            sources,
            embeddingOptions,
            dimensions,
            cancellationToken);

        var byNode = new Dictionary<string, List<IReadOnlyList<float>>>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (!merged.TryGetValue(source.SpecimenId, out var vector))
            {
                continue;
            }

            if (!byNode.TryGetValue(source.SourceNodeId, out var vectors))
            {
                vectors = [];
                byNode.Add(source.SourceNodeId, vectors);
            }

            vectors.Add(vector);
        }

        return byNode.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<IReadOnlyList<float>>)item.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static async ValueTask<IReadOnlyList<TechniqueSpecimenVectorSource>> ReadActiveTechniqueSpecimensAsync(
        SqliteConnection connection,
        IReadOnlyList<CorpusCandidateNode> candidates,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var nodeIds = candidates.Select(candidate => candidate.NodeId).Distinct(StringComparer.Ordinal).ToArray();
        var builder = new StringBuilder("""
            SELECT specimen_id,
                   source_node_id,
                   source_anchor_id,
                   technique_abstract,
                   trigger_context,
                   transfer_template,
                   effect_on_reader
            FROM reference_technique_specimens
            WHERE validity_state = 'active'
              AND review_state <> 'rejected'
              AND superseded_by_run_id IS NULL
            """);
        var parameters = new List<(string Name, object Value)>();
        AppendInClause(builder, parameters, "source_node_id", nodeIds, "technique_source_node_id");
        builder.AppendLine("ORDER BY source_node_id, specimen_id;");

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var result = new List<TechniqueSpecimenVectorSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var embeddingText = BuildTechniqueEmbeddingText(
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6));
            if (string.IsNullOrWhiteSpace(embeddingText))
            {
                continue;
            }

            result.Add(new TechniqueSpecimenVectorSource(
                SpecimenId: reader.GetString(0),
                SourceNodeId: reader.GetString(1),
                SourceAnchorId: reader.GetInt64(2),
                TechniqueHash: StableHash("reference_technique_vector_text", embeddingText),
                EmbeddingText: embeddingText));
        }

        return result;
    }

    private static async ValueTask<IReadOnlyList<TechniqueSpecimenVectorSource>> ReadScopedTechniqueSpecimensAsync(
        SqliteConnection connection,
        ReferenceCorpusQueryContextPayload queryContext,
        string requestedNodeType,
        CancellationToken cancellationToken)
    {
        var libraryIds = await ResolveEffectiveLibraryIdsAsync(
            connection,
            queryContext.Scope,
            queryContext.ChapterContext.NovelId,
            cancellationToken);
        if (libraryIds.Count == 0)
        {
            return [];
        }

        var reusePolicies = NormalizeTextSet(queryContext.Scope.ReusePolicies);
        if (reusePolicies.Count == 0)
        {
            reusePolicies = [ReferenceCorpusReusePolicies.VerbatimOk, ReferenceCorpusReusePolicies.AdaptedOnly];
        }

        var includeAnchorIds = NormalizePositiveLongSet(queryContext.Scope.IncludeAnchorIds);
        var excludeAnchorIds = NormalizePositiveLongSet(queryContext.Scope.ExcludeAnchorIds);
        var parameters = new List<(string Name, object Value)>
        {
            ("$node_type", requestedNodeType),
            ("$novel_id", queryContext.ChapterContext.NovelId)
        };
        var builder = new StringBuilder("""
            WITH eligible_nodes AS (
                SELECT n.node_id,
                       n.anchor_id,
                       lm.library_id,
                       COALESCE(NULLIF(TRIM(lm.dedup_group_id), ''), 'anchor:' || n.anchor_id) AS dedup_group_key,
                       CASE COALESCE(lm.source_quality, '')
                           WHEN 'trusted' THEN 3
                           WHEN 'normal' THEN 2
                           WHEN 'low' THEN 1
                           ELSE 0
                       END AS source_quality_rank,
                       lic.reuse_policy
                FROM reference_text_nodes n
                JOIN reference_anchors a ON a.anchor_id = n.anchor_id
                JOIN reference_library_members lm ON lm.anchor_id = n.anchor_id
                JOIN reference_corpus_libraries lib ON lib.library_id = lm.library_id
                JOIN reference_source_license lic ON lic.anchor_id = n.anchor_id
                WHERE n.node_type = $node_type
                  AND a.status = 'ready'
                  AND lm.enabled = 1
                  AND (lib.scope = 'global' OR (lib.scope = 'project' AND lib.novel_id = $novel_id))
                  AND lic.license_state IN ('public_domain', 'cc', 'authorized')
                  AND lic.reuse_policy IN ('verbatim_ok', 'adapted_only')
                  AND (a.novel_id = $novel_id OR ((a.novel_id IS NULL OR a.novel_id = 0) AND a.corpus_visibility = 'workspace'))
            ),
            source_representatives AS (
                SELECT library_id,
                       anchor_id,
                       dedup_group_key
                FROM (
                    SELECT library_id,
                           anchor_id,
                           dedup_group_key,
                           ROW_NUMBER() OVER (
                               PARTITION BY dedup_group_key
                               ORDER BY source_quality_rank DESC, library_id, anchor_id
                           ) AS dedup_rank
                    FROM (
                        SELECT DISTINCT library_id,
                               anchor_id,
                               dedup_group_key,
                               source_quality_rank
                        FROM eligible_nodes
                    )
                )
                WHERE dedup_rank = 1
            ),
            scoped_nodes AS (
                SELECT e.*
                FROM eligible_nodes e
                JOIN source_representatives r
                  ON r.library_id = e.library_id
                 AND r.anchor_id = e.anchor_id
                 AND r.dedup_group_key = e.dedup_group_key
            )
            SELECT ts.specimen_id,
                   ts.source_node_id,
                   ts.source_anchor_id,
                   ts.technique_abstract,
                   ts.trigger_context,
                   ts.transfer_template,
                   ts.effect_on_reader
            FROM scoped_nodes n
            JOIN reference_technique_specimens ts
              ON ts.source_node_id = n.node_id
            WHERE ts.validity_state = 'active'
              AND ts.review_state <> 'rejected'
              AND ts.superseded_by_run_id IS NULL
            """);
        AppendInClause(builder, parameters, "n.library_id", libraryIds, "native_technique_library_id");
        AppendInClause(builder, parameters, "n.reuse_policy", reusePolicies, "native_technique_reuse_policy");
        if (includeAnchorIds.Count > 0)
        {
            AppendInClause(builder, parameters, "n.anchor_id", includeAnchorIds, "native_technique_include_anchor_id");
        }

        if (excludeAnchorIds.Count > 0)
        {
            AppendNotInClause(builder, parameters, "n.anchor_id", excludeAnchorIds, "native_technique_exclude_anchor_id");
        }

        builder.AppendLine("ORDER BY ts.source_node_id, ts.specimen_id;");
        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var result = new List<TechniqueSpecimenVectorSource>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var embeddingText = BuildTechniqueEmbeddingText(
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6));
            if (string.IsNullOrWhiteSpace(embeddingText))
            {
                continue;
            }

            result.Add(new TechniqueSpecimenVectorSource(
                SpecimenId: reader.GetString(0),
                SourceNodeId: reader.GetString(1),
                SourceAnchorId: reader.GetInt64(2),
                TechniqueHash: StableHash("reference_technique_vector_text", embeddingText),
                EmbeddingText: embeddingText));
        }

        return result;
    }

    private static string BuildNativeTechniqueIndexScopeKey(
        ReferenceCorpusQueryContextPayload queryContext,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        string requestedNodeType)
    {
        var scope = queryContext.Scope;
        return StableHash(
            "reference_technique_native_index_scope",
            queryContext.ChapterContext.NovelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            requestedNodeType,
            embeddingOptions.ProviderKey,
            embeddingOptions.ModelId,
            dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(',', (scope.LibraryIds ?? []).Order(StringComparer.Ordinal)),
            string.Join(',', (scope.ReusePolicies ?? []).Order(StringComparer.Ordinal)),
            string.Join(',', (scope.IncludeAnchorIds ?? []).Order()),
            string.Join(',', (scope.ExcludeAnchorIds ?? []).Order()));
    }

    private static string NativeTechniqueIndexSourceHash(
        string indexScopeKey,
        string tableName,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        IReadOnlyList<NativeTechniqueVectorIndexEntry> entries)
    {
        return StableHash(
            "reference_technique_native_index_sources",
            indexScopeKey,
            tableName,
            embeddingOptions.ProviderKey,
            embeddingOptions.ModelId,
            dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(
                '\n',
                entries
                    .Select(entry => NativeTechniqueIndexRowSignature(
                        entry.RowId,
                        entry.VectorId,
                        entry.Source.SpecimenId,
                        entry.Source.SourceNodeId,
                        entry.Source.SourceAnchorId,
                        embeddingOptions.ProviderKey,
                        embeddingOptions.ModelId,
                        dimensions,
                        entry.Source.TechniqueHash,
                        tableName))
                    .Order(StringComparer.Ordinal)));
    }

    private static string NativeTechniqueIndexRowSignature(
        long rowId,
        string vectorId,
        string specimenId,
        string sourceNodeId,
        long sourceAnchorId,
        string providerKey,
        string modelId,
        int dimensions,
        string techniqueHash,
        string tableName)
    {
        return string.Join(
            '\u001f',
            rowId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            vectorId,
            specimenId,
            sourceNodeId,
            sourceAnchorId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            providerKey,
            modelId,
            dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            techniqueHash,
            tableName);
    }

    private static async ValueTask<bool> IsNativeTechniqueIndexCurrentAsync(
        SqliteConnection connection,
        string indexScopeKey,
        string tableName,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        string sourceHash,
        IReadOnlyList<NativeTechniqueVectorIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT source_hash, source_count
            FROM reference_technique_vector_index_state
            WHERE index_scope_key = $index_scope_key
              AND table_name = $table_name
              AND provider_key = $provider_key
              AND model_id = $model_id
              AND dimensions = $dimensions
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$index_scope_key", indexScopeKey);
        command.Parameters.AddWithValue("$table_name", tableName);
        command.Parameters.AddWithValue("$provider_key", embeddingOptions.ProviderKey);
        command.Parameters.AddWithValue("$model_id", embeddingOptions.ModelId);
        command.Parameters.AddWithValue("$dimensions", dimensions);
        var stateMatches = false;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            stateMatches = string.Equals(reader.GetString(0), sourceHash, StringComparison.Ordinal) &&
                reader.GetInt32(1) == entries.Count;
        }

        if (!stateMatches)
        {
            return false;
        }

        await using var readRows = connection.CreateCommand();
        readRows.CommandText = """
            SELECT row_id,
                   vector_id,
                   specimen_id,
                   source_node_id,
                   source_anchor_id,
                   provider_key,
                   model_id,
                   dimensions,
                   technique_hash,
                   table_name
            FROM reference_technique_vector_rows
            WHERE index_scope_key = $index_scope_key
            ORDER BY row_id;
            """;
        readRows.Parameters.AddWithValue("$index_scope_key", indexScopeKey);
        var actual = new List<string>();
        await using (var reader = await readRows.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                actual.Add(NativeTechniqueIndexRowSignature(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetInt64(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    reader.GetInt32(7),
                    reader.GetString(8),
                    reader.GetString(9)));
            }
        }

        var expected = entries
            .Select(entry => NativeTechniqueIndexRowSignature(
                entry.RowId,
                entry.VectorId,
                entry.Source.SpecimenId,
                entry.Source.SourceNodeId,
                entry.Source.SourceAnchorId,
                embeddingOptions.ProviderKey,
                embeddingOptions.ModelId,
                dimensions,
                entry.Source.TechniqueHash,
                tableName))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return actual.Order(StringComparer.Ordinal).SequenceEqual(expected, StringComparer.Ordinal);
    }

    private static async ValueTask ClearNativeTechniqueIndexStateAsync(
        SqliteConnection connection,
        string indexScopeKey,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteRows = connection.CreateCommand())
        {
            deleteRows.Transaction = transaction;
            deleteRows.CommandText = "DELETE FROM reference_technique_vector_rows WHERE index_scope_key = $index_scope_key;";
            deleteRows.Parameters.AddWithValue("$index_scope_key", indexScopeKey);
            await deleteRows.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var deleteState = connection.CreateCommand())
        {
            deleteState.Transaction = transaction;
            deleteState.CommandText = "DELETE FROM reference_technique_vector_index_state WHERE index_scope_key = $index_scope_key;";
            deleteState.Parameters.AddWithValue("$index_scope_key", indexScopeKey);
            await deleteState.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ReplaceNativeTechniqueVectorRowsAsync(
        SqliteConnection connection,
        string indexScopeKey,
        string tableName,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        string sourceHash,
        IReadOnlyList<NativeTechniqueVectorIndexEntry> entries,
        CancellationToken cancellationToken)
    {
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using (var deleteRows = connection.CreateCommand())
        {
            deleteRows.Transaction = transaction;
            deleteRows.CommandText = "DELETE FROM reference_technique_vector_rows WHERE index_scope_key = $index_scope_key;";
            deleteRows.Parameters.AddWithValue("$index_scope_key", indexScopeKey);
            await deleteRows.ExecuteNonQueryAsync(cancellationToken);
        }

        var updatedAt = DateTimeOffset.UtcNow.ToString("O");
        foreach (var entry in entries)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO reference_technique_vector_rows
                  (index_scope_key, row_id, vector_id, specimen_id, source_node_id, source_anchor_id,
                   provider_key, model_id, dimensions, technique_hash, table_name, updated_at)
                VALUES
                  ($index_scope_key, $row_id, $vector_id, $specimen_id, $source_node_id, $source_anchor_id,
                   $provider_key, $model_id, $dimensions, $technique_hash, $table_name, $updated_at);
                """;
            insert.Parameters.AddWithValue("$index_scope_key", indexScopeKey);
            insert.Parameters.AddWithValue("$row_id", entry.RowId);
            insert.Parameters.AddWithValue("$vector_id", entry.VectorId);
            insert.Parameters.AddWithValue("$specimen_id", entry.Source.SpecimenId);
            insert.Parameters.AddWithValue("$source_node_id", entry.Source.SourceNodeId);
            insert.Parameters.AddWithValue("$source_anchor_id", entry.Source.SourceAnchorId);
            insert.Parameters.AddWithValue("$provider_key", embeddingOptions.ProviderKey);
            insert.Parameters.AddWithValue("$model_id", embeddingOptions.ModelId);
            insert.Parameters.AddWithValue("$dimensions", dimensions);
            insert.Parameters.AddWithValue("$technique_hash", entry.Source.TechniqueHash);
            insert.Parameters.AddWithValue("$table_name", tableName);
            insert.Parameters.AddWithValue("$updated_at", updatedAt);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var upsertState = connection.CreateCommand())
        {
            upsertState.Transaction = transaction;
            upsertState.CommandText = """
                INSERT INTO reference_technique_vector_index_state
                  (index_scope_key, table_name, provider_key, model_id, dimensions,
                   source_hash, source_count, updated_at)
                VALUES
                  ($index_scope_key, $table_name, $provider_key, $model_id, $dimensions,
                   $source_hash, $source_count, $updated_at)
                ON CONFLICT(index_scope_key) DO UPDATE SET
                  table_name = excluded.table_name,
                  provider_key = excluded.provider_key,
                  model_id = excluded.model_id,
                  dimensions = excluded.dimensions,
                  source_hash = excluded.source_hash,
                  source_count = excluded.source_count,
                  updated_at = excluded.updated_at;
                """;
            upsertState.Parameters.AddWithValue("$index_scope_key", indexScopeKey);
            upsertState.Parameters.AddWithValue("$table_name", tableName);
            upsertState.Parameters.AddWithValue("$provider_key", embeddingOptions.ProviderKey);
            upsertState.Parameters.AddWithValue("$model_id", embeddingOptions.ModelId);
            upsertState.Parameters.AddWithValue("$dimensions", dimensions);
            upsertState.Parameters.AddWithValue("$source_hash", sourceHash);
            upsertState.Parameters.AddWithValue("$source_count", entries.Count);
            upsertState.Parameters.AddWithValue("$updated_at", updatedAt);
            await upsertState.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask<IReadOnlyList<string>> ReadNativeTechniqueRecallNodeIdsAsync(
        SqliteConnection connection,
        string indexScopeKey,
        string tableName,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        IReadOnlyList<SqliteVecSearchRecord> records,
        CancellationToken cancellationToken)
    {
        if (records.Count == 0)
        {
            return [];
        }

        var rowIds = records.Select(record => record.RowId).Distinct().ToArray();
        var builder = new StringBuilder("""
            SELECT r.source_node_id
            FROM reference_technique_vector_rows r
            JOIN reference_technique_vectors v
              ON v.vector_id = r.vector_id
             AND v.specimen_id = r.specimen_id
             AND v.source_node_id = r.source_node_id
             AND v.source_anchor_id = r.source_anchor_id
             AND v.provider_key = r.provider_key
             AND v.model_id = r.model_id
             AND v.dimensions = r.dimensions
             AND v.technique_hash = r.technique_hash
            JOIN reference_technique_specimens ts
              ON ts.specimen_id = r.specimen_id
             AND ts.source_node_id = r.source_node_id
             AND ts.source_anchor_id = r.source_anchor_id
             AND ts.validity_state = 'active'
             AND ts.review_state <> 'rejected'
             AND ts.superseded_by_run_id IS NULL
            WHERE r.index_scope_key = $index_scope_key
              AND r.table_name = $table_name
              AND r.provider_key = $provider_key
              AND r.model_id = $model_id
              AND r.dimensions = $dimensions
            """);
        var parameters = new List<(string Name, object Value)>
        {
            ("$index_scope_key", indexScopeKey),
            ("$table_name", tableName),
            ("$provider_key", embeddingOptions.ProviderKey),
            ("$model_id", embeddingOptions.ModelId),
            ("$dimensions", dimensions)
        };
        AppendInClause(builder, parameters, "r.row_id", rowIds, "native_technique_row_id");
        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    private static string BuildTechniqueEmbeddingText(
        string techniqueAbstract,
        string triggerContext,
        string transferTemplate,
        string effectOnReader)
    {
        var builder = new StringBuilder();
        AppendQueryPart(builder, techniqueAbstract);
        AppendQueryPart(builder, triggerContext);
        AppendQueryPart(builder, transferTemplate);
        AppendQueryPart(builder, effectOnReader);
        return builder.ToString();
    }

    private static async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<float>>> ReadTechniqueVectorEmbeddingsAsync(
        SqliteConnection connection,
        IReadOnlyList<TechniqueSpecimenVectorSource> sources,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        }

        var bySpecimenId = sources.ToDictionary(source => source.SpecimenId, StringComparer.Ordinal);
        var builder = new StringBuilder("""
            SELECT specimen_id, source_node_id, source_anchor_id, technique_hash, embedding_json
            FROM reference_technique_vectors
            WHERE provider_key = $provider_key
              AND model_id = $model_id
              AND dimensions = $dimensions
            """);
        var parameters = new List<(string Name, object Value)>
        {
            ("$provider_key", embeddingOptions.ProviderKey),
            ("$model_id", embeddingOptions.ModelId),
            ("$dimensions", dimensions)
        };
        AppendInClause(builder, parameters, "specimen_id", bySpecimenId.Keys.ToArray(), "technique_specimen_id");

        await using var command = connection.CreateCommand();
        command.CommandText = builder.ToString();
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        var result = new Dictionary<string, IReadOnlyList<float>>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var specimenId = reader.GetString(0);
            if (!bySpecimenId.TryGetValue(specimenId, out var source) ||
                !string.Equals(reader.GetString(1), source.SourceNodeId, StringComparison.Ordinal) ||
                reader.GetInt64(2) != source.SourceAnchorId ||
                !string.Equals(reader.GetString(3), source.TechniqueHash, StringComparison.Ordinal))
            {
                continue;
            }

            var vector = DeserializeVector(reader.GetString(4));
            if (vector.Count == dimensions)
            {
                result[specimenId] = vector;
            }
        }

        return result;
    }

    private static async ValueTask UpsertTechniqueVectorAsync(
        SqliteConnection connection,
        TechniqueSpecimenVectorSource source,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions,
        IReadOnlyList<float> vector,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reference_technique_vectors
              (vector_id, specimen_id, source_node_id, source_anchor_id, provider_key, model_id,
               dimensions, technique_hash, embedding_json, updated_at)
            VALUES
              ($vector_id, $specimen_id, $source_node_id, $source_anchor_id, $provider_key, $model_id,
               $dimensions, $technique_hash, $embedding_json, $updated_at)
            ON CONFLICT(specimen_id, provider_key, model_id, dimensions) DO UPDATE SET
              source_node_id = excluded.source_node_id,
              source_anchor_id = excluded.source_anchor_id,
              technique_hash = excluded.technique_hash,
              embedding_json = excluded.embedding_json,
              updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$vector_id", TechniqueVectorId(source, embeddingOptions, dimensions));
        command.Parameters.AddWithValue("$specimen_id", source.SpecimenId);
        command.Parameters.AddWithValue("$source_node_id", source.SourceNodeId);
        command.Parameters.AddWithValue("$source_anchor_id", source.SourceAnchorId);
        command.Parameters.AddWithValue("$provider_key", embeddingOptions.ProviderKey);
        command.Parameters.AddWithValue("$model_id", embeddingOptions.ModelId);
        command.Parameters.AddWithValue("$dimensions", dimensions);
        command.Parameters.AddWithValue("$technique_hash", source.TechniqueHash);
        command.Parameters.AddWithValue("$embedding_json", JsonSerializer.Serialize(vector, JsonOptions));
        command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string TechniqueVectorId(
        TechniqueSpecimenVectorSource source,
        EmbeddingRequestOptions embeddingOptions,
        int dimensions)
    {
        return StableHash(
            "reference_technique_vector",
            source.SpecimenId,
            embeddingOptions.ProviderKey,
            embeddingOptions.ModelId,
            dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async ValueTask<IReadOnlyList<float>?> GetOrCreateCurrentChapterEmbeddingAsync(
        SqliteConnection connection,
        CurrentChapterContextPayload chapterContext,
        EmbeddingRequestOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        var draftText = chapterContext.CurrentDraftText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(draftText))
        {
            return null;
        }

        var dimensions = embeddingOptions.Dimensions ?? BuiltinOnnxEmbeddingModel.Dimensions;
        var draftHash = StableHash("current_chapter_draft", draftText);
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT embedding_json
                FROM reference_current_chapter_embedding_cache
                WHERE novel_id = $novel_id
                  AND chapter_number = $chapter_number
                  AND draft_text_hash = $draft_text_hash
                  AND provider_key = $provider_key
                  AND model_id = $model_id
                  AND dimensions = $dimensions
                LIMIT 1;
                """;
            read.Parameters.AddWithValue("$novel_id", chapterContext.NovelId);
            read.Parameters.AddWithValue("$chapter_number", chapterContext.ChapterNumber);
            read.Parameters.AddWithValue("$draft_text_hash", draftHash);
            read.Parameters.AddWithValue("$provider_key", embeddingOptions.ProviderKey);
            read.Parameters.AddWithValue("$model_id", embeddingOptions.ModelId);
            read.Parameters.AddWithValue("$dimensions", dimensions);
            var cached = await read.ExecuteScalarAsync(cancellationToken);
            if (cached is string json)
            {
                var vector = DeserializeVector(json);
                if (vector.Count == dimensions)
                {
                    return vector;
                }
            }
        }

        var vectorResult = await EmbedSingleAsync(
            draftText,
            embeddingOptions with
            {
                Dimensions = dimensions,
                InputKind = BuiltinOnnxEmbeddingModel.QueryInputKind
            },
            cancellationToken);
        if (vectorResult is null)
        {
            return null;
        }

        await using var upsert = connection.CreateCommand();
        upsert.CommandText = """
            INSERT INTO reference_current_chapter_embedding_cache
              (cache_id, novel_id, chapter_number, draft_text_hash, provider_key, model_id,
               dimensions, embedding_json, updated_at)
            VALUES
              ($cache_id, $novel_id, $chapter_number, $draft_text_hash, $provider_key, $model_id,
               $dimensions, $embedding_json, $updated_at)
            ON CONFLICT(novel_id, chapter_number, draft_text_hash, provider_key, model_id, dimensions) DO UPDATE SET
              embedding_json = excluded.embedding_json,
              updated_at = excluded.updated_at;
            """;
        upsert.Parameters.AddWithValue("$cache_id", StableHash(
            "current_chapter_embedding",
            chapterContext.NovelId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            chapterContext.ChapterNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            draftHash,
            embeddingOptions.ProviderKey,
            embeddingOptions.ModelId,
            dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        upsert.Parameters.AddWithValue("$novel_id", chapterContext.NovelId);
        upsert.Parameters.AddWithValue("$chapter_number", chapterContext.ChapterNumber);
        upsert.Parameters.AddWithValue("$draft_text_hash", draftHash);
        upsert.Parameters.AddWithValue("$provider_key", embeddingOptions.ProviderKey);
        upsert.Parameters.AddWithValue("$model_id", embeddingOptions.ModelId);
        upsert.Parameters.AddWithValue("$dimensions", dimensions);
        upsert.Parameters.AddWithValue("$embedding_json", JsonSerializer.Serialize(vectorResult, JsonOptions));
        upsert.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
        await upsert.ExecuteNonQueryAsync(cancellationToken);

        return vectorResult;
    }

    private async ValueTask<IReadOnlyList<float>?> EmbedSingleAsync(
        string text,
        EmbeddingRequestOptions embeddingOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var response = await _embeddings.EmbedAsync([text], embeddingOptions, cancellationToken);
        if (response.Items.Count != 1)
        {
            throw new InvalidOperationException("Reference corpus embedding response must contain exactly one item.");
        }

        return response.Items[0].Vector;
    }

 private static ReferenceCorpusCandidatePayload ToPayload(
 ScoredCorpusCandidate scored,
 ReferenceCorpusRetrievalDiagnosticsPayload? diagnostics = null)
    {
        var candidate = scored.Candidate;
        return new ReferenceCorpusCandidatePayload(
            CandidateId: "corpus-node:" + candidate.NodeId,
            NodeId: candidate.NodeId,
            AnchorId: candidate.AnchorId,
            LibraryId: candidate.LibraryId,
            NodeType: candidate.NodeType,
            TextPreview: Preview(candidate.Text),
            TextHash: candidate.TextHash,
            LicenseState: candidate.LicenseState,
            ReusePolicy: candidate.ReusePolicy,
            Score: scored.Score,
            ScoreComponents: scored.ScoreComponents,
            FitExplanation: BuildFitExplanation(candidate),
Evidence: candidate.Observations
                .OrderByDescending(item => item.Confidence)
                .ThenBy(item => item.ObservationId, StringComparer.Ordinal)
                .Take(3)
                .Select(item => new ReferenceCorpusCandidateEvidencePayload(
                    item.ObservationId,
                    item.FeatureFamily,
                    item.FeatureKey,
 item.Confidence))
 .ToArray(),
 diagnostics,
 candidate.RouteProvenance
 .OrderBy(item => item.Value.Rank)
 .ThenBy(item => item.Key, StringComparer.Ordinal)
 .Select(item => new ReferenceCorpusRouteProvenancePayload(
 item.Key,
 item.Value.Rank,
 Math.Round(item.Value.Score, 6)))
 .ToArray(),
 candidate.SourceCoverage);
    }

    private static string Preview(string text)
    {
        var normalized = text.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ').Trim();
        return normalized.Length <= MaxTextPreviewLength
            ? normalized
            : normalized[..MaxTextPreviewLength] + "...";
    }

    private static string BuildFitExplanation(CorpusCandidateNode candidate)
    {
        var strongest = candidate.Observations
            .OrderByDescending(item => item.Confidence)
            .FirstOrDefault();
        if (strongest is null)
        {
            return "text node is licensed and semantically close to the current chapter context";
        }

        return $"{strongest.FeatureFamily}:{strongest.FeatureKey} supports current chapter fit";
    }

    private static string BuildQueryEmbeddingText(ReferenceCorpusQueryContextPayload context)
    {
        var builder = new StringBuilder();
        AppendQueryPart(builder, context.SceneType);
        AppendQueryPart(builder, context.EmotionTarget);
        AppendQueryPart(builder, context.PacingTarget);
        AppendQueryPart(builder, context.NarrativePosition);
        AppendQueryPart(builder, context.CommercialMechanic);
        foreach (var value in context.CharacterStates ?? [])
        {
            AppendQueryPart(builder, value);
        }

        foreach (var value in context.RequiredNarrativeFunctions ?? [])
        {
            AppendQueryPart(builder, value);
        }

        AppendQueryPart(builder, context.ChapterContext.PreviousChapterSummary);
        AppendQueryPart(builder, context.ChapterContext.CurrentDraftText);
        return builder.ToString();
    }

    private static void AppendQueryPart(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (builder.Length > 0)
        {
            builder.Append('\n');
        }

        builder.Append(value.Trim());
    }

    private static double ObservationFitScore(
        IReadOnlyList<CorpusCandidateObservation> observations,
        ReferenceCorpusQueryContextPayload context)
    {
        if (observations.Count == 0)
        {
            return 0;
        }

        var queryTerms = BuildQueryTerms(context);
        var score = 0.0;
        foreach (var observation in observations)
        {
            var haystack = string.Join(
                ' ',
                observation.FeatureFamily,
                observation.FeatureKey,
                observation.ValueText);
            var matched = queryTerms.Any(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (matched)
            {
                score += observation.Confidence;
            }
            else if (observation.FeatureFamily is "sensory" or "emotion" or "narrative_function")
            {
                score += observation.Confidence * 0.25;
            }
        }

        return Math.Clamp(score / Math.Max(1, observations.Count), 0, 1);
    }

    private static double TechniqueFitScore(
        IReadOnlyList<float>? queryEmbedding,
        IReadOnlyList<IReadOnlyList<float>>? techniqueVectors)
    {
        if (queryEmbedding is null || techniqueVectors is null || techniqueVectors.Count == 0)
        {
            return 0;
        }

        return techniqueVectors.Max(vector => CosineSimilarity(queryEmbedding, vector));
    }

    private static double LocalContextFitScore(
        CorpusCandidateNode candidate,
        CurrentChapterContextPayload chapterContext)
    {
        var terms = BuildLocalContextTerms(chapterContext);
        if (terms.Count == 0)
        {
            return 0;
        }

        var candidateText = NormalizeContextTermText(candidate.Text);
        if (candidateText.Length == 0)
        {
            return 0;
        }

        var matchedWeight = 0.0;
        foreach (var term in terms)
        {
            if (candidateText.Contains(term.Text, StringComparison.OrdinalIgnoreCase))
            {
                matchedWeight += term.Weight;
            }
        }

        return Math.Clamp(1.0 - Math.Exp(-matchedWeight / 3.0), 0, 1);
    }

    private static IReadOnlyList<LocalContextTerm> BuildLocalContextTerms(CurrentChapterContextPayload chapterContext)
    {
        var terms = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        AddContextTerms(
            terms,
            ExtractInsertionWindow(chapterContext.CurrentDraftText, chapterContext.InsertionOffset, radius: 48),
            weight: 0.65);
        AddContextTerms(terms, chapterContext.PreviousChapterSummary, weight: 0.30);

        foreach (var snapshot in chapterContext.CharacterSnapshots ?? [])
        {
            AddExactContextTerm(terms, snapshot.Character, weight: 1.0);
            AddContextTerms(terms, snapshot.State, weight: 0.35);
            foreach (var knowledge in snapshot.AllowedKnowledge ?? [])
            {
                AddContextTerms(terms, knowledge, weight: 0.80);
            }
        }

        return terms
            .Select(item => new LocalContextTerm(item.Key, item.Value))
            .OrderByDescending(item => item.Weight)
            .ThenBy(item => item.Text, StringComparer.Ordinal)
            .Take(64)
            .ToArray();
    }

    private static string ExtractInsertionWindow(string? text, int insertionOffset, int radius)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var boundedOffset = Math.Clamp(insertionOffset, 0, text.Length);
        var start = Math.Max(0, boundedOffset - radius);
        var end = Math.Min(text.Length, boundedOffset + radius);
        return text[start..end];
    }

    private static void AddContextTerms(
        Dictionary<string, double> terms,
        string? text,
        double weight)
    {
        foreach (var term in ExtractContextTerms(text))
        {
            AddExactContextTerm(terms, term, weight);
        }
    }

    private static void AddExactContextTerm(
        Dictionary<string, double> terms,
        string? term,
        double weight)
    {
        var normalized = NormalizeContextTermText(term);
        if (normalized.Length < 2)
        {
            return;
        }

        if (terms.TryGetValue(normalized, out var existing))
        {
            terms[normalized] = Math.Max(existing, weight);
            return;
        }

        terms.Add(normalized, weight);
    }

    private static IReadOnlyList<string> ExtractContextTerms(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var span in SplitContextTermSpans(text))
        {
            if (span.Length < 2)
            {
                continue;
            }

            if (!ContainsCjk(span))
            {
                if (span.Length >= 3)
                {
                    result.Add(span);
                }

                continue;
            }

            if (span.Length <= 8)
            {
                result.Add(span);
            }

            var maxGram = Math.Min(4, span.Length);
            for (var gram = 2; gram <= maxGram; gram++)
            {
                for (var index = 0; index + gram <= span.Length; index++)
                {
                    result.Add(span.Substring(index, gram));
                }
            }
        }

        return result.ToArray();
    }

    private static IReadOnlyList<string> SplitContextTermSpans(string text)
    {
        var spans = new List<string>();
        var builder = new StringBuilder();
        foreach (var ch in text)
        {
            if (IsContextTermChar(ch))
            {
                builder.Append(ch);
                continue;
            }

            FlushContextTermSpan(builder, spans);
        }

        FlushContextTermSpan(builder, spans);
        return spans;
    }

    private static void FlushContextTermSpan(StringBuilder builder, List<string> spans)
    {
        if (builder.Length == 0)
        {
            return;
        }

        spans.Add(builder.ToString());
        builder.Clear();
    }

    private static string NormalizeContextTermText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.Trim())
        {
            if (IsContextTermChar(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static bool IsContextTermChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || IsCjk(ch);
    }

    private static bool ContainsCjk(string value)
    {
        return value.Any(IsCjk);
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u3400' and <= '\u9fff';
    }

    private static IReadOnlyList<string> BuildQueryTerms(ReferenceCorpusQueryContextPayload context)
    {
        var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddTermFragments(terms, context.SceneType);
        AddTermFragments(terms, context.EmotionTarget);
        AddTermFragments(terms, context.PacingTarget);
        AddTermFragments(terms, context.NarrativePosition);
        AddTermFragments(terms, context.CommercialMechanic);
        foreach (var item in context.RequiredNarrativeFunctions ?? [])
        {
            AddTermFragments(terms, item);
        }

        return terms.Where(term => term.Length >= 3).ToArray();
    }

    private static void AddTermFragments(HashSet<string> terms, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var term in value.Split(['_', '-', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            terms.Add(term);
        }

        terms.Add(value.Trim());
    }

    private static double PositionFitScore(
        CorpusCandidateNode candidate,
        CurrentChapterContextPayload chapterContext)
    {
        var draft = ReferenceCorpusSimilarityGate.NormalizeForComparison(chapterContext.CurrentDraftText);
        var node = ReferenceCorpusSimilarityGate.NormalizeForComparison(candidate.Text);
        if (draft.Length == 0 || node.Length == 0)
        {
            return 0;
        }

        var shared = node.EnumerateRunes().Count(rune => draft.Contains(rune.ToString(), StringComparison.Ordinal));
        return Math.Clamp(shared / (double)Math.Max(1, node.EnumerateRunes().Count()), 0, 1);
    }

    private static double SourceQualityScore(string sourceQuality)
    {
        return sourceQuality.Trim().ToLowerInvariant() switch
        {
            "trusted" => 1.0,
            "normal" => 0.7,
            "low" => 0.25,
            _ => 0.5
        };
    }

    private static double CosineSimilarity(
        IReadOnlyList<float>? left,
        IReadOnlyList<float>? right)
    {
        if (left is null || right is null || left.Count == 0 || left.Count != right.Count)
        {
            return 0;
        }

        var dot = 0.0;
        var leftNorm = 0.0;
        var rightNorm = 0.0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        if (leftNorm <= 0 || rightNorm <= 0)
        {
            return 0;
        }

        var cosine = dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
        return Math.Clamp((cosine + 1.0) / 2.0, 0, 1);
    }

    private static IReadOnlyList<float> DeserializeVector(string json)
    {
        return JsonSerializer.Deserialize<IReadOnlyList<float>>(json, JsonOptions) ?? [];
    }

    private static HashSet<string> NormalizeTextSet(IReadOnlyList<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.Ordinal) ?? [];
    }

    private static HashSet<long> NormalizePositiveLongSet(IReadOnlyList<long>? values)
    {
        return values?
            .Where(value => value > 0)
            .ToHashSet() ?? [];
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
            var name = "$" + parameterPrefix + "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

private static void AppendNotInClause<T>(
        StringBuilder builder,
        List<(string Name, object Value)> parameters,
        string columnName,
        IReadOnlyCollection<T> values,
        string parameterPrefix)
        where T : notnull
    {
        if (values.Count == 0)
        {
            return;
        }

        var names = new List<string>(values.Count);
        var index = 0;
        foreach (var value in values)
        {
            var name = "$" + parameterPrefix + "_" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            names.Add(name);
            parameters.Add((name, value));
            index++;
        }

        builder.Append(" AND ");
        builder.Append(columnName);
        builder.Append(" NOT IN (");
        builder.Append(string.Join(", ", names));
builder.AppendLine(")");
}

 private static string BuildEligibleScopeSql(
 List<(string Name, object Value)> parameters,
 IReadOnlyCollection<string> libraryIds,
 IReadOnlyCollection<string> reusePolicies,
 IReadOnlyCollection<long> includeAnchorIds,
 IReadOnlyCollection<long> excludeAnchorIds)
 {
 var builder = new StringBuilder();
 AppendInClause(builder, parameters, "lm.library_id", libraryIds, "eligible_library_id");
 AppendInClause(builder, parameters, "lic.reuse_policy", reusePolicies, "eligible_reuse_policy");
 if (includeAnchorIds.Count > 0)
 {
 AppendInClause(builder, parameters, "n.anchor_id", includeAnchorIds, "eligible_include_anchor_id");
 }
 if (excludeAnchorIds.Count > 0)
 {
 AppendNotInClause(builder, parameters, "n.anchor_id", excludeAnchorIds, "eligible_exclude_anchor_id");
 }
 return builder.ToString();
 }

    private static string StableHash(params string[] parts)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\u001f', parts));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private sealed record CorpusCandidateNode(
        string NodeId,
        long AnchorId,
        string NodeType,
        int SequenceIndex,
        int? ChapterIndex,
        string TextHash,
        string Text,
        string CreatedAt,
        string LibraryId,
string SourceQuality,
string LicenseState,
string ReusePolicy,
 string DedupGroupKey,
 IReadOnlyList<ReferenceCorpusSourceCoveragePayload> SourceCoverage,
 HashSet<string> RecallRouteComponents,
 Dictionary<string, RouteProvenance> RouteProvenance,
List<CorpusCandidateObservation> Observations);

    private sealed record CorpusCandidateObservation(
        string ObservationId,
        string FeatureFamily,
        string FeatureKey,
        string ValueText,
        double Confidence);

    private sealed record LocalContextTerm(
        string Text,
        double Weight);

    private sealed record IndexedFeatureFilter(
        int Index,
        string FamilyKey,
        string FeatureKey,
        string ValueTextKey,
        string ValueNumMinKey,
        string ValueNumMaxKey);

    private sealed record TechniqueSpecimenVectorSource(
        string SpecimenId,
        string SourceNodeId,
        long SourceAnchorId,
        string TechniqueHash,
        string EmbeddingText);

    private sealed record NativeTechniqueRecallResult(
        IReadOnlySet<string> NodeIds);

    private sealed record StructuredObservationRecallResult(
        IReadOnlySet<string> NodeIds);

    private sealed record ChapterContextRecallResult(
        IReadOnlySet<string> NodeIds);

    private sealed record NativeTechniqueIndexEnsureResult(
        string Status,
        string? IndexScopeKey,
        string? TableName,
        string? ProviderKey,
        string? ModelId,
        int Dimensions,
        int SourceCount,
        int VectorCount,
        int SkippedVectorCount,
        bool Rebuilt,
        IReadOnlyList<string> Diagnostics);

    private sealed record NativeTechniqueVectorIndexEntry(
        long RowId,
        TechniqueSpecimenVectorSource Source,
        string VectorId,
        IReadOnlyList<float> Vector);

private sealed record ScoredCorpusCandidate(
CorpusCandidateNode Candidate,
double Score,
IReadOnlyDictionary<string, double> ScoreComponents);

 private sealed record RouteProvenance(int Rank, double Score);

 private sealed record RouteOrder(string Route, IReadOnlyList<ScoredCorpusCandidate> Items);

 private sealed record ReferenceCorpusCandidateCursor(
 string Fingerprint,
 int Offset,
 string LastNodeId);
}
