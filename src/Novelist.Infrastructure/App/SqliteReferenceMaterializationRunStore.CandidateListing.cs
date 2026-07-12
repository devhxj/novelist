using System.Text;
using Microsoft.Data.Sqlite;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal sealed partial class SqliteReferenceMaterializationRunStore
{
    private const int CandidatePreviewMaxChars = 512;

    public async ValueTask<PageResultPayload<ReferenceMaterializationCandidatePayload>> ListCandidatesAsync(
        string runId,
        string decision,
        int page,
        int size,
        CancellationToken cancellationToken)
    {
        var normalizedRunId = NormalizeRunId(runId);
        var normalizedDecision = NormalizeCandidateDecision(decision);
        if (page <= 0 || size is <= 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "Candidate list pagination is invalid.");
        }

        var databasePath = await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(databasePath, cancellationToken);
        var total = await CountCandidatesAsync(connection, normalizedRunId, normalizedDecision, cancellationToken);
        var offset = checked((page - 1) * size);
        var summaries = await ReadCandidateSummariesAsync(
            connection,
            normalizedRunId,
            normalizedDecision,
            size,
            offset,
            cancellationToken);
        var previews = await ReadCandidatePreviewsAsync(
            connection,
            summaries.Select(summary => summary.CandidateId).ToArray(),
            cancellationToken);
        var items = new List<ReferenceMaterializationCandidatePayload>(summaries.Count);
        foreach (var summary in summaries)
        {
            items.Add(new ReferenceMaterializationCandidatePayload(
                summary.CandidateId,
                normalizedRunId,
                summary.AnchorId,
                summary.ChapterIndex,
                summary.CandidateType,
                summary.Decision,
                summary.DecisionOrigin,
                summary.QualityScore,
                summary.Confidence,
                ParseTags(summary.TagsJson),
                ParseStringArray(summary.ReasonCodesJson, 12),
                previews.TryGetValue(summary.CandidateId, out var preview)
                    ? preview.TextPreview
                    : throw new InvalidOperationException("Materialization candidate has no source-node evidence."),
                preview.SourceSpans,
                summary.SourceNodeCount,
                summary.RowVersion));
        }

        var totalPages = total == 0 ? 0 : (int)Math.Ceiling(total / (double)size);
        return new PageResultPayload<ReferenceMaterializationCandidatePayload>(items, total, page, size, totalPages);
    }

    private static string NormalizeCandidateDecision(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (!ReferenceMaterializationCandidateDecisions.All.Contains(normalized, StringComparer.Ordinal))
        {
            throw new ArgumentException("Materialization candidate decision filter is invalid.", nameof(value));
        }

        return normalized;
    }

    private static async ValueTask<int> CountCandidatesAsync(
        SqliteConnection connection,
        string runId,
        string decision,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM reference_material_candidates
            WHERE run_id = $run_id
              AND decision = $decision;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$decision", decision);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async ValueTask<IReadOnlyList<CandidateListSummary>> ReadCandidateSummariesAsync(
        SqliteConnection connection,
        string runId,
        string decision,
        int limit,
        int offset,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT candidate.candidate_id,
                   candidate.anchor_id,
                   (
                     SELECT MIN(boundary.chapter_index)
                     FROM reference_material_candidate_nodes candidate_node
                     JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
                     JOIN reference_materialization_runs run ON run.run_id = candidate.run_id
                     JOIN reference_chapter_split_boundaries boundary ON boundary.split_profile_id = run.split_profile_id
                     WHERE candidate_node.candidate_id = candidate.candidate_id
                       AND node.start_offset >= boundary.content_start
                       AND node.end_offset <= boundary.content_end
                   ),
                   candidate.candidate_type,
                   candidate.decision,
                   candidate.decision_origin,
                   candidate.quality_score,
                   candidate.confidence,
                   candidate.tags_json,
                   candidate.reason_codes_json,
                   candidate.row_version,
                   (SELECT COUNT(*)
                    FROM reference_material_candidate_nodes node_count
                    WHERE node_count.candidate_id = candidate.candidate_id)
            FROM reference_material_candidates candidate
            WHERE candidate.run_id = $run_id
              AND candidate.decision = $decision
            ORDER BY (
              SELECT MIN(node.start_offset)
              FROM reference_material_candidate_nodes candidate_node
              JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
              WHERE candidate_node.candidate_id = candidate.candidate_id
            ), candidate.candidate_id
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$run_id", runId);
        command.Parameters.AddWithValue("$decision", decision);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var summaries = new List<CandidateListSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            summaries.Add(new CandidateListSummary(
                reader.GetString(0),
                reader.GetInt64(1),
                reader.GetInt32(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetDouble(6),
                reader.IsDBNull(7) ? null : reader.GetDouble(7),
                reader.GetString(8),
                reader.GetString(9),
                reader.GetInt64(10),
                reader.GetInt32(11)));
        }

        return summaries;
    }

    private static async ValueTask<IReadOnlyDictionary<string, CandidatePreview>> ReadCandidatePreviewsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> candidateIds,
        CancellationToken cancellationToken)
    {
        if (candidateIds.Count == 0)
        {
            return new Dictionary<string, CandidatePreview>(StringComparer.Ordinal);
        }

        await using var command = connection.CreateCommand();
        var parameterNames = candidateIds.Select((_, index) => $"$candidate_id_{index}").ToArray();
        command.CommandText = $"""
            SELECT node.text, candidate_node.evidence_start, candidate_node.evidence_end,
                   candidate_node.candidate_id, candidate_node.node_id
            FROM reference_material_candidate_nodes candidate_node
            JOIN reference_text_nodes node ON node.node_id = candidate_node.node_id
            WHERE candidate_node.candidate_id IN ({string.Join(", ", parameterNames)})
            ORDER BY candidate_node.candidate_id, candidate_node.ordinal;
            """;
        for (var index = 0; index < candidateIds.Count; index++)
        {
            command.Parameters.AddWithValue(parameterNames[index], candidateIds[index]);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var builders = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var spans = new Dictionary<string, List<ReferenceMaterializationCandidateSourceSpanPayload>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken))
        {
            var text = reader.GetString(0);
            var start = reader.GetInt32(1);
            var end = reader.GetInt32(2);
            var candidateId = reader.GetString(3);
            if (start < 0 || end <= start || end > text.Length)
            {
                throw new InvalidOperationException("Materialization candidate evidence offsets are invalid.");
            }

            if (!builders.TryGetValue(candidateId, out var preview))
            {
                preview = new StringBuilder(CandidatePreviewMaxChars + 1);
                builders.Add(candidateId, preview);
                spans.Add(candidateId, []);
            }

            spans[candidateId].Add(new ReferenceMaterializationCandidateSourceSpanPayload(
                reader.GetString(4),
                start,
                end));

            if (preview.Length >= CandidatePreviewMaxChars)
            {
                continue;
            }

            if (preview.Length > 0)
            {
                preview.Append('\n');
            }

            var remaining = CandidatePreviewMaxChars - preview.Length;
            preview.Append(text.AsSpan(start, Math.Min(end - start, remaining)));
        }

        return builders.ToDictionary(
            pair => pair.Key,
            pair => new CandidatePreview(
                pair.Value.Length == CandidatePreviewMaxChars
                    ? pair.Value.Append("...").ToString()
                    : pair.Value.ToString(),
                spans[pair.Key]),
            StringComparer.Ordinal);
    }

    private sealed record CandidateListSummary(
        string CandidateId,
        long AnchorId,
        int ChapterIndex,
        string CandidateType,
        string Decision,
        string DecisionOrigin,
        double? QualityScore,
        double? Confidence,
        string TagsJson,
        string ReasonCodesJson,
        long RowVersion,
        int SourceNodeCount);

    private sealed record CandidatePreview(
        string TextPreview,
        IReadOnlyList<ReferenceMaterializationCandidateSourceSpanPayload> SourceSpans);
}
