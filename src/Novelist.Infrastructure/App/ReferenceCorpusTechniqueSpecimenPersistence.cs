using System.Globalization;
using Microsoft.Data.Sqlite;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed record ReferenceCorpusTechniqueSpecimenPersistenceRequest(
 string RunId,
 long AnchorId,
 DateTimeOffset CreatedAt,
 ReferenceCorpusTechniqueSpecimenCandidate Candidate);

internal sealed class ReferenceCorpusTechniqueSpecimenPersistence
{
 public async ValueTask<string> PersistAsync(
 SqliteConnection connection,
 SqliteTransaction transaction,
 ReferenceCorpusTechniqueSpecimenPersistenceRequest request,
 CancellationToken cancellationToken)
 {
 ArgumentNullException.ThrowIfNull(connection);
 ArgumentNullException.ThrowIfNull(transaction);
 ArgumentNullException.ThrowIfNull(request);
 Validate(request);

 var candidate = request.Candidate;
 var specimenId = ReferenceCorpusTechniqueSpecimenIdentity.Create(
 request.RunId,
 candidate.SourceNodeId,
 candidate.TechniqueFamily).SpecimenId;
 await using (var command = connection.CreateCommand())
 {
 command.Transaction = transaction;
 command.CommandText = """
 INSERT INTO reference_technique_specimens
 (specimen_id, source_node_id, source_anchor_id, analysis_run_id, technique_family,
 technique_abstract, trigger_context, transfer_template, transfer_slots_json,
 effect_on_reader, applicability_conditions, failure_modes, anti_patterns,
 world_context_dependencies, why_it_works_json, confidence, review_state,
 validity_state, superseded_by_run_id, mastery_notes, created_at)
 VALUES
 ($specimen_id, $source_node_id, $source_anchor_id, $analysis_run_id, $technique_family,
 $technique_abstract, $trigger_context, $transfer_template, $transfer_slots_json,
 $effect_on_reader, $applicability_conditions, $failure_modes, $anti_patterns,
 $world_context_dependencies, $why_it_works_json, $confidence, 'unverified',
 'active', NULL, $mastery_notes, $created_at)
 ON CONFLICT(specimen_id) DO UPDATE SET
 technique_abstract = excluded.technique_abstract,
 trigger_context = excluded.trigger_context,
 transfer_template = excluded.transfer_template,
 transfer_slots_json = excluded.transfer_slots_json,
 effect_on_reader = excluded.effect_on_reader,
 applicability_conditions = excluded.applicability_conditions,
 failure_modes = excluded.failure_modes,
 anti_patterns = excluded.anti_patterns,
 world_context_dependencies = excluded.world_context_dependencies,
 why_it_works_json = excluded.why_it_works_json,
 confidence = excluded.confidence,
 validity_state = excluded.validity_state,
 superseded_by_run_id = excluded.superseded_by_run_id,
 mastery_notes = excluded.mastery_notes;
 """;
 command.Parameters.AddWithValue("$specimen_id", specimenId);
 command.Parameters.AddWithValue("$source_node_id", candidate.SourceNodeId);
 command.Parameters.AddWithValue("$source_anchor_id", request.AnchorId);
 command.Parameters.AddWithValue("$analysis_run_id", request.RunId);
 command.Parameters.AddWithValue("$technique_family", candidate.TechniqueFamily);
 command.Parameters.AddWithValue("$technique_abstract", candidate.TechniqueAbstract);
 command.Parameters.AddWithValue("$trigger_context", candidate.TriggerContext);
 command.Parameters.AddWithValue("$transfer_template", candidate.TransferTemplate);
 command.Parameters.AddWithValue("$transfer_slots_json", candidate.TransferSlotsJson);
 command.Parameters.AddWithValue("$effect_on_reader", candidate.EffectOnReader);
 command.Parameters.AddWithValue("$applicability_conditions", candidate.ApplicabilityConditionsJson);
 command.Parameters.AddWithValue("$failure_modes", candidate.FailureModesJson);
 command.Parameters.AddWithValue("$anti_patterns", candidate.AntiPatternsJson);
 command.Parameters.AddWithValue("$world_context_dependencies", DbValue(candidate.WorldContextDependenciesJson));
 command.Parameters.AddWithValue("$why_it_works_json", candidate.WhyItWorksJson);
 command.Parameters.AddWithValue("$confidence", candidate.Confidence);
 command.Parameters.AddWithValue("$mastery_notes", DbValue(candidate.MasteryNotes));
 command.Parameters.AddWithValue("$created_at", request.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
 await command.ExecuteNonQueryAsync(cancellationToken);
 }

 await using (var delete = connection.CreateCommand())
 {
 delete.Transaction = transaction;
 delete.CommandText = "DELETE FROM reference_specimen_evidence WHERE specimen_id = $specimen_id;";
 delete.Parameters.AddWithValue("$specimen_id", specimenId);
 await delete.ExecuteNonQueryAsync(cancellationToken);
 }

 foreach (var observationId in candidate.EvidenceObservationIds.Distinct(StringComparer.Ordinal))
 {
 await using var insert = connection.CreateCommand();
 insert.Transaction = transaction;
 insert.CommandText = "INSERT INTO reference_specimen_evidence (specimen_id, observation_id) VALUES ($specimen_id, $observation_id);";
 insert.Parameters.AddWithValue("$specimen_id", specimenId);
 insert.Parameters.AddWithValue("$observation_id", observationId);
 await insert.ExecuteNonQueryAsync(cancellationToken);
 }

 return specimenId;
 }

 private static void Validate(ReferenceCorpusTechniqueSpecimenPersistenceRequest request)
 {
 if (request.AnchorId <= 0 || string.IsNullOrWhiteSpace(request.RunId) ||
 string.IsNullOrWhiteSpace(request.Candidate.SourceNodeId) ||
 string.IsNullOrWhiteSpace(request.Candidate.TechniqueFamily) ||
 request.Candidate.EvidenceObservationIds.Count == 0)
 {
 throw new ArgumentException("Technique specimen persistence requires a run, source, family, anchor, and evidence.", nameof(request));
 }
 }

 private static object DbValue(string? value) => value is null ? DBNull.Value : value;
}
