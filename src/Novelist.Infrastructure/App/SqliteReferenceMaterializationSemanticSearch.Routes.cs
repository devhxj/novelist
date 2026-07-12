using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed partial class SqliteReferenceMaterializationSemanticSearch
{
    private static async ValueTask<IReadOnlyList<RouteMaterial>> ReadSemanticRouteAsync(
        SqliteConnection connection,
        long anchorId,
        ActiveGenerationSnapshot snapshot,
        IReadOnlyList<SqliteVecSearchRecord> vectorResults,
        CancellationToken cancellationToken)
    {
        if (vectorResults.Count == 0)
        {
            return [];
        }

        var materialsByEmbeddingRowId = await ReadActiveMaterialsByEmbeddingRowIdAsync(
            connection,
            anchorId,
            snapshot,
            vectorResults.Select(result => result.RowId).ToArray(),
            cancellationToken);
        if (materialsByEmbeddingRowId.Count != vectorResults.Count)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "Active-generation vector rows no longer match the promoted materials.");
        }

        return vectorResults
            .OrderBy(result => result.Distance)
            .ThenBy(result => result.RowId)
            .Select(result => new RouteMaterial(
                materialsByEmbeddingRowId[result.RowId],
                NormalizeRouteScore(1.0 - result.Distance, "semantic")))
            .ToArray();
    }

    private static async ValueTask<IReadOnlyList<RouteMaterial>> SearchLexicalRouteAsync(
        SqliteConnection connection,
        long anchorId,
        ActiveGenerationSnapshot snapshot,
        string query,
        int routeLimit,
        CancellationToken cancellationToken)
    {
        var matchQuery = BuildFtsMatchQuery(query);
        if (matchQuery is null)
        {
            return [];
        }

        await EnsureLexicalIndexAsync(connection, anchorId, snapshot.GenerationId, cancellationToken);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT material.material_id, material.anchor_id, material.generation_id, material.material_type,
                       material.text, material.quality_score, material.confidence, material.tags_json,
                       material.reason_codes_json, bm25(reference_materialization_material_fts) AS lexical_rank
                FROM reference_materialization_material_fts
                JOIN reference_materialization_materials material
                  ON material.rowid = reference_materialization_material_fts.rowid
                 AND material.material_id = reference_materialization_material_fts.material_id
                JOIN reference_anchor_materialization_state state
                  ON state.anchor_id = material.anchor_id
                 AND state.active_generation_id = material.generation_id
                WHERE reference_materialization_material_fts MATCH $match_query
                  AND material.anchor_id = $anchor_id
                  AND material.generation_id = $generation_id
                ORDER BY lexical_rank ASC, material.material_id ASC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$match_query", matchQuery);
            command.Parameters.AddWithValue("$anchor_id", anchorId);
            command.Parameters.AddWithValue("$generation_id", snapshot.GenerationId);
            command.Parameters.AddWithValue("$limit", routeLimit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<(ReferenceMaterializationMaterialPayload Material, double Rank)>();
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add((ReadMaterial(reader), reader.GetDouble(9)));
            }

            return results
                .Select((result, index) => new RouteMaterial(
                    result.Material,
                    NormalizeRankScore(index, results.Count)))
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ReferenceMaterializationException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.LexicalIndexFailed,
                "Active-generation lexical material search failed.");
        }
    }

    private static async ValueTask<TagRoutes> SearchTagRoutesAsync(
        SqliteConnection connection,
        long anchorId,
        ActiveGenerationSnapshot snapshot,
        string query,
        int routeLimit,
        CancellationToken cancellationToken)
    {
        var queryTerms = ExtractTerms(query);
        if (queryTerms.Count == 0)
        {
            return TagRoutes.Empty;
        }

        await using var command = connection.CreateCommand();
        var predicates = new List<string>(queryTerms.Count);
        for (var index = 0; index < queryTerms.Count; index++)
        {
            var parameterName = "$term_" + index.ToString(CultureInfo.InvariantCulture);
            predicates.Add("instr(lower(material.tags_json), " + parameterName + ") > 0");
            command.Parameters.AddWithValue(parameterName, queryTerms[index]);
        }

        command.CommandText = """
            SELECT material.material_id, material.anchor_id, material.generation_id, material.material_type,
                   material.text, material.quality_score, material.confidence, material.tags_json,
                   material.reason_codes_json
            FROM reference_materialization_materials material
            JOIN reference_anchor_materialization_state state
              ON state.anchor_id = material.anchor_id
             AND state.active_generation_id = material.generation_id
            WHERE material.anchor_id = $anchor_id
              AND material.generation_id = $generation_id
              AND (
            """ + string.Join(" OR ", predicates) + """
              )
            ORDER BY material.quality_score DESC, material.confidence DESC, material.material_id ASC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$anchor_id", anchorId);
        command.Parameters.AddWithValue("$generation_id", snapshot.GenerationId);
        command.Parameters.AddWithValue("$limit", routeLimit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var structured = new List<RouteMaterial>();
        var technique = new List<RouteMaterial>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var material = ReadMaterial(reader);
            var structuredScore = TagScore(
                queryTerms,
                material.Tags.NarrativeFunctions
                    .Concat(material.Tags.EmotionMechanics)
                    .Concat(material.Tags.Pov));
            var techniqueScore = TagScore(queryTerms, material.Tags.Techniques);
            if (structuredScore > 0)
            {
                structured.Add(new RouteMaterial(material, structuredScore));
            }

            if (techniqueScore > 0)
            {
                technique.Add(new RouteMaterial(material, techniqueScore));
            }
        }

        return new TagRoutes(
            structured
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Material.QualityScore)
                .ThenBy(item => item.Material.MaterialId, StringComparer.Ordinal)
                .Take(routeLimit)
                .ToArray(),
            technique
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Material.QualityScore)
                .ThenBy(item => item.Material.MaterialId, StringComparer.Ordinal)
                .Take(routeLimit)
                .ToArray());
    }

    private static async ValueTask<IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload>> FuseRoutesAsync(
        SqliteConnection connection,
        ActiveGenerationSnapshot snapshot,
        IReadOnlyList<RouteMaterial> semantic,
        IReadOnlyList<RouteMaterial> lexical,
        IReadOnlyList<RouteMaterial> structured,
        IReadOnlyList<RouteMaterial> technique,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<string, FusedMaterial>(StringComparer.Ordinal);
        MergeRoute(candidates, semantic, static (candidate, score) => candidate.Semantic = score, snapshot);
        MergeRoute(candidates, lexical, static (candidate, score) => candidate.Lexical = score, snapshot);
        MergeRoute(candidates, structured, static (candidate, score) => candidate.Structured = score, snapshot);
        MergeRoute(candidates, technique, static (candidate, score) => candidate.Technique = score, snapshot);
        if (candidates.Count == 0)
        {
            return [];
        }

        var spansByMaterialId = await ReadSourceSpansAsync(connection, candidates.Keys.ToArray(), cancellationToken);
        var selected = new List<ReferenceMaterializationSemanticSearchHitPayload>(maxResults);
        var selectedTexts = new HashSet<string>(StringComparer.Ordinal);
        var selectedSpans = new Dictionary<string, List<SourceSpan>>(StringComparer.Ordinal);
        foreach (var candidate in candidates.Values
                     .OrderByDescending(candidate => candidate.FusedScore)
                     .ThenByDescending(candidate => candidate.Semantic)
                     .ThenByDescending(candidate => candidate.Material.QualityScore)
                     .ThenBy(candidate => candidate.Material.MaterialId, StringComparer.Ordinal))
        {
            var spans = spansByMaterialId.TryGetValue(candidate.Material.MaterialId, out var value)
                ? value
                : Array.Empty<SourceSpan>();
            if (!selectedTexts.Add(candidate.Material.Text) || IsNearDuplicate(spans, selectedSpans))
            {
                continue;
            }

            foreach (var span in spans)
            {
                if (!selectedSpans.TryGetValue(span.NodeId, out var nodeSpans))
                {
                    nodeSpans = [];
                    selectedSpans.Add(span.NodeId, nodeSpans);
                }

                nodeSpans.Add(span);
            }

            selected.Add(new ReferenceMaterializationSemanticSearchHitPayload(
                candidate.Material,
                Math.Round(candidate.Semantic, 6),
                candidate.ScoreComponents));
            if (selected.Count == maxResults)
            {
                break;
            }
        }

        return selected;
    }

    private static void MergeRoute(
        IDictionary<string, FusedMaterial> candidates,
        IReadOnlyList<RouteMaterial> route,
        Action<FusedMaterial, double> assign,
        ActiveGenerationSnapshot snapshot)
    {
        foreach (var item in route)
        {
            if (item.Material.AnchorId <= 0 ||
                !string.Equals(item.Material.GenerationId, snapshot.GenerationId, StringComparison.Ordinal))
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.GenerationIncomplete,
                    "A retrieval route returned material outside the active generation.");
            }

            if (!candidates.TryGetValue(item.Material.MaterialId, out var candidate))
            {
                candidate = new FusedMaterial(item.Material);
                candidates.Add(item.Material.MaterialId, candidate);
            }
            else if (candidate.Material.AnchorId != item.Material.AnchorId ||
                     !string.Equals(candidate.Material.GenerationId, item.Material.GenerationId, StringComparison.Ordinal))
            {
                throw new ReferenceMaterializationException(
                    ReferenceMaterializationErrorCodes.GenerationIncomplete,
                    "Retrieval routes disagreed about material provenance.");
            }

            assign(candidate, item.Score);
        }
    }

    private static async ValueTask<IReadOnlyDictionary<string, IReadOnlyList<SourceSpan>>> ReadSourceSpansAsync(
        SqliteConnection connection,
        IReadOnlyList<string> materialIds,
        CancellationToken cancellationToken)
    {
        if (materialIds.Count == 0)
        {
            return new Dictionary<string, IReadOnlyList<SourceSpan>>(StringComparer.Ordinal);
        }

        await using var command = connection.CreateCommand();
        var parameterNames = new List<string>(materialIds.Count);
        for (var index = 0; index < materialIds.Count; index++)
        {
            var parameterName = "$material_id_" + index.ToString(CultureInfo.InvariantCulture);
            parameterNames.Add(parameterName);
            command.Parameters.AddWithValue(parameterName, materialIds[index]);
        }

        command.CommandText = """
            SELECT material_id, node_id, evidence_start, evidence_end
            FROM reference_materialization_material_nodes
            WHERE material_id IN (
            """ + string.Join(", ", parameterNames) + """
            )
            ORDER BY material_id, node_id, evidence_start, evidence_end;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var spans = new Dictionary<string, List<SourceSpan>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var materialId = reader.GetString(0);
            if (!spans.TryGetValue(materialId, out var items))
            {
                items = [];
                spans.Add(materialId, items);
            }

            items.Add(new SourceSpan(reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
        }

        return spans.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<SourceSpan>)pair.Value,
            StringComparer.Ordinal);
    }

    private static bool IsNearDuplicate(
        IReadOnlyList<SourceSpan> candidate,
        IReadOnlyDictionary<string, List<SourceSpan>> selected)
    {
        foreach (var span in candidate)
        {
            if (!selected.TryGetValue(span.NodeId, out var selectedForNode))
            {
                continue;
            }

            foreach (var existing in selectedForNode)
            {
                var overlap = Math.Max(0, Math.Min(span.End, existing.End) - Math.Max(span.Start, existing.Start));
                var shorterLength = Math.Min(span.End - span.Start, existing.End - existing.Start);
                if (shorterLength > 0 && overlap / (double)shorterLength >= 0.8)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static async ValueTask EnsureLexicalIndexAsync(
        SqliteConnection connection,
        long anchorId,
        string generationId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using (var provision = connection.CreateCommand())
            {
                provision.CommandText = """
                    CREATE VIRTUAL TABLE IF NOT EXISTS reference_materialization_material_fts
                      USING fts5(
                        material_id UNINDEXED,
                        anchor_id UNINDEXED,
                        generation_id UNINDEXED,
                        text,
                        lexical_terms,
                        tokenize = 'unicode61'
                      );
                    """;
                await provision.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT material.rowid, material.material_id, material.anchor_id, material.generation_id,
                       material.text
                FROM reference_materialization_materials material
                WHERE material.anchor_id = $anchor_id
                  AND material.generation_id = $generation_id
                  AND NOT EXISTS (
                      SELECT 1
                      FROM reference_materialization_material_fts existing
                      WHERE existing.rowid = material.rowid
                        AND existing.material_id = material.material_id
                  );
                """;
            command.Parameters.AddWithValue("$anchor_id", anchorId);
            command.Parameters.AddWithValue("$generation_id", generationId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<LexicalIndexRow>();
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add(new LexicalIndexRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetString(4)));
            }

            if (rows.Count == 0)
            {
                return;
            }

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText = """
                INSERT OR REPLACE INTO reference_materialization_material_fts (
                    rowid, material_id, anchor_id, generation_id, text, lexical_terms)
                VALUES ($rowid, $material_id, $anchor_id, $generation_id, $text, $lexical_terms);
                """;
            var rowId = upsert.Parameters.Add("$rowid", SqliteType.Integer);
            var materialId = upsert.Parameters.Add("$material_id", SqliteType.Text);
            var indexedAnchorId = upsert.Parameters.Add("$anchor_id", SqliteType.Integer);
            var indexedGenerationId = upsert.Parameters.Add("$generation_id", SqliteType.Text);
            var text = upsert.Parameters.Add("$text", SqliteType.Text);
            var lexicalTerms = upsert.Parameters.Add("$lexical_terms", SqliteType.Text);
            foreach (var row in rows)
            {
                rowId.Value = row.RowId;
                materialId.Value = row.MaterialId;
                indexedAnchorId.Value = row.AnchorId;
                indexedGenerationId.Value = row.GenerationId;
                text.Value = row.Text;
                lexicalTerms.Value = ReferenceMaterializationLexicalTerms.BuildIndexText(row.Text);
                await upsert.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.LexicalIndexFailed,
                "Active-generation lexical material index could not be prepared.");
        }
    }

    private static ReferenceMaterializationMaterialPayload ReadMaterial(SqliteDataReader reader) => new(
        reader.GetString(0),
        reader.GetInt64(1),
        reader.GetString(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetDouble(5),
        reader.GetDouble(6),
        ParseTags(reader.GetString(7)),
        ParseStringArray(reader.GetString(8), 12));

    private static string? BuildFtsMatchQuery(string query)
    {
        var terms = ReferenceMaterializationLexicalTerms.Extract(query, 64);
        return terms.Count == 0
            ? null
            : string.Join(" OR ", terms.Select(term => "\"" + term.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""));
    }

    private static IReadOnlyList<string> ExtractTerms(string query) => Regex.Matches(query, "[\\p{L}\\p{N}_]+")
        .Select(match => match.Value.ToLowerInvariant())
        .Where(value => value.Length > 0)
        .Distinct(StringComparer.Ordinal)
        .Take(12)
        .ToArray();

    private static double TagScore(IReadOnlyList<string> queryTerms, IEnumerable<string> tags)
    {
        var tagTerms = tags
            .SelectMany(tag => ExtractTerms(tag))
            .ToHashSet(StringComparer.Ordinal);
        var matches = queryTerms.Count(term => tagTerms.Contains(term));
        return matches == 0 ? 0 : Math.Round(matches / (double)queryTerms.Count, 6);
    }

    private static double NormalizeRankScore(int index, int count) =>
        count <= 0 ? 0 : Math.Round((count - index) / (double)count, 6);

    private static double NormalizeRouteScore(double value, string route)
    {
        if (!double.IsFinite(value))
        {
            throw new ReferenceMaterializationException(
                ReferenceMaterializationErrorCodes.GenerationIncomplete,
                "The " + route + " retrieval route returned an invalid score.");
        }

        return Math.Round(Math.Clamp(value, 0, 1), 6);
    }

    private sealed record RouteMaterial(ReferenceMaterializationMaterialPayload Material, double Score);

    private sealed record LexicalIndexRow(long RowId, string MaterialId, long AnchorId, string GenerationId, string Text);

    private sealed record SourceSpan(string NodeId, int Start, int End);

    private sealed record TagRoutes(IReadOnlyList<RouteMaterial> Structured, IReadOnlyList<RouteMaterial> Technique)
    {
        public static TagRoutes Empty { get; } = new([], []);
    }

    private sealed class FusedMaterial
    {
        public FusedMaterial(ReferenceMaterializationMaterialPayload material)
        {
            Material = material;
        }

        public ReferenceMaterializationMaterialPayload Material { get; }

        public double Lexical { get; set; }

        public double Semantic { get; set; }

        public double Structured { get; set; }

        public double Technique { get; set; }

        public double FusedScore => Math.Round(
            Lexical * 0.25 + Semantic * 0.4 + Structured * 0.2 + Technique * 0.1 + Quality * 0.05,
            6);

        public double Quality => NormalizeRouteScore(Material.QualityScore, "quality");

        public IReadOnlyDictionary<string, double> ScoreComponents => new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["lexical"] = Lexical,
            ["semantic"] = Semantic,
            ["structured"] = Structured,
            ["technique"] = Technique,
            ["quality"] = Quality,
            ["penalty"] = 0,
            ["fused"] = FusedScore
        };
    }
}
