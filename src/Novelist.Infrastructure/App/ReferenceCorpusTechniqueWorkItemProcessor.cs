using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceCorpusTechniqueWorkItemStatuses
{
 public const string Succeeded = "succeeded";
 public const string BudgetExhausted = "budget_exhausted";
 public const string ValidationFailed = "validation_failed";
}

internal sealed record ReferenceCorpusTechniqueWorkItemInput(
 string RunId,
 long AnchorId,
 string NodeId,
 string NodeType,
 string NodeText,
IReadOnlyList<ReferenceCorpusTechniqueObservationEvidence> Observations,
 ReferenceCorpusFrozenModelSelection ModelSelection,
int MaxValidationAttempts,
 ReferenceCorpusFeatureTokenEnvelope TokenEnvelope);

internal sealed record ReferenceCorpusTechniqueWorkItemResult(
 string Status,
 string ValidationStatus,
 int TokensSpent,
 ReferenceCorpusTechniqueSpecimenCandidate? Candidate,
 IReadOnlyList<string> Diagnostics);

internal sealed class ReferenceCorpusTechniqueWorkItemProcessor
{
 private readonly IReferenceCorpusTechniqueSpecimenAnalyzer _analyzer;
 private readonly IReferenceCorpusFeatureValidationDelay _delay;
 private readonly ReferenceCorpusAnalysisRetryPolicy _retryPolicy = new();

 public ReferenceCorpusTechniqueWorkItemProcessor(
 IReferenceCorpusTechniqueSpecimenAnalyzer analyzer,
 IReferenceCorpusFeatureValidationDelay? delay = null)
 {
 _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
 _delay = delay ?? ReferenceCorpusFeatureValidationDelay.Instance;
 }

 public async ValueTask<ReferenceCorpusTechniqueWorkItemResult> ProcessAsync(
 ReferenceCorpusTechniqueWorkItemInput input,
 CancellationToken cancellationToken)
 {
 Validate(input);
 var diagnostics = new List<string>();
 var tokensSpent = 0;
 ReferenceCorpusTechniqueSpecimenValidationResult? validation = null;

 for (var attempt = 1; attempt <= input.MaxValidationAttempts; attempt++)
 {
 cancellationToken.ThrowIfCancellationRequested();
 var remaining = input.TokenEnvelope.RemainingTokens - tokensSpent;
 if (remaining <= 0)
 {
 diagnostics.Add("Technique analysis token budget was exhausted before another model call.");
 return Build(ReferenceCorpusTechniqueWorkItemStatuses.BudgetExhausted, validation, tokensSpent, diagnostics);
 }

 var output = await _analyzer.AnalyzeAsync(
 new ReferenceCorpusTechniqueSpecimenAnalysisInput(
 input.RunId,
 input.AnchorId,
 input.NodeId,
 input.NodeType,
 input.NodeText,
 input.Observations)
{
 ModelSelection = input.ModelSelection,
MaxOutputTokens = Math.Min(remaining, input.TokenEnvelope.MaximumOutputTokens)
 },
 cancellationToken);

 var chargedTokens = output.TokensSpent > 0
 ? output.TokensSpent
 : Math.Min(input.TokenEnvelope.UnknownUsageCharge, remaining);
 tokensSpent += chargedTokens;
 if (output.TokensSpent <= 0)
 {
 diagnostics.Add($"Model usage was unknown; conservatively charged {chargedTokens} tokens.");
 }

 validation = ReferenceCorpusTechniqueSpecimenOutputValidator.Validate(
 output.ModelOutputJson,
 input.NodeId,
 input.NodeText,
 input.Observations);
 diagnostics.AddRange(validation.Diagnostics);
 if (validation.Status == ReferenceCorpusTechniqueSpecimenValidationStatuses.Passed &&
 validation.Candidate is not null)
 {
 return Build(ReferenceCorpusTechniqueWorkItemStatuses.Succeeded, validation, tokensSpent, diagnostics);
 }

 if (tokensSpent >= input.TokenEnvelope.RemainingTokens)
 {
 return Build(ReferenceCorpusTechniqueWorkItemStatuses.BudgetExhausted, validation, tokensSpent, diagnostics);
 }

 var retry = _retryPolicy.Decide(new(
 ReferenceCorpusAnalysisRetryCategories.Validation,
 attempt,
 DateTimeOffset.UtcNow,
 RetryAfter: null));
 if (!retry.ShouldRetry || attempt >= input.MaxValidationAttempts)
 {
 break;
 }

 var delay = retry.NextAttemptAt!.Value - DateTimeOffset.UtcNow;
 await _delay.DelayAsync(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
 }

 diagnostics.Add("Technique analysis validation retry limit was reached.");
 return Build(ReferenceCorpusTechniqueWorkItemStatuses.ValidationFailed, validation, tokensSpent, diagnostics);
 }

 private static ReferenceCorpusTechniqueWorkItemResult Build(
 string status,
 ReferenceCorpusTechniqueSpecimenValidationResult? validation,
 int tokensSpent,
 IReadOnlyList<string> diagnostics) =>
 new(
 status,
 validation?.Status ?? ReferenceCorpusTechniqueSpecimenValidationStatuses.Rejected,
 tokensSpent,
 validation?.Candidate,
 diagnostics);

 private static void Validate(ReferenceCorpusTechniqueWorkItemInput input)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.AnchorId <= 0 || string.IsNullOrWhiteSpace(input.RunId) ||
 string.IsNullOrWhiteSpace(input.NodeId) || string.IsNullOrWhiteSpace(input.NodeText))
 {
 throw new ArgumentException("Frozen technique work item identity and text are required.", nameof(input));
 }
 if (input.Observations.Count == 0)
 {
 throw new ArgumentException("Frozen technique evidence is required.", nameof(input));
 }
 if (input.MaxValidationAttempts is < 1 or > 4)
 {
 throw new ArgumentOutOfRangeException(nameof(input), "Validation attempts must be between 1 and 4.");
 }
 if (input.TokenEnvelope.RemainingTokens <= 0 ||
 input.TokenEnvelope.MaximumOutputTokens <= 0 ||
 input.TokenEnvelope.UnknownUsageCharge <= 0)
 {
 throw new ArgumentOutOfRangeException(nameof(input), "Token envelope values must be positive.");
 }
 }
}
