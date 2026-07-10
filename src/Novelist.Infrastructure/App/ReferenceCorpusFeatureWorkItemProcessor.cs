using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceCorpusFeatureWorkItemStatuses
{
 public const string Succeeded = "succeeded";
 public const string BudgetExhausted = "budget_exhausted";
 public const string ValidationFailed = "validation_failed";
}

internal sealed record ReferenceCorpusFeatureTokenEnvelope(int RemainingTokens, int MaximumOutputTokens, int UnknownUsageCharge);

internal sealed record ReferenceCorpusFeatureWorkItemInput(
 string RunId,
 long AnchorId,
 string NodeId,
 string NodeText,
 string NodeType,
string FeatureFamily,
ReferenceCorpusFeatureAnalysisContext Context,
 ReferenceCorpusFrozenModelSelection ModelSelection,
int MaxValidationAttempts,
 ReferenceCorpusFeatureTokenEnvelope TokenEnvelope);

internal sealed record ReferenceCorpusFeatureWorkItemResult(
 string Status,
 string ValidationStatus,
 int TokensSpent,
 IReadOnlyList<ReferenceCorpusFeatureObservationCandidate> AcceptedObservations,
 IReadOnlyList<ReferenceCorpusFeatureObservationRejectedItem> RejectedObservations,
 IReadOnlyList<string> Diagnostics);

internal interface IReferenceCorpusFeatureValidationDelay
{
 ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

internal sealed class ReferenceCorpusFeatureValidationDelay : IReferenceCorpusFeatureValidationDelay
{
 public static ReferenceCorpusFeatureValidationDelay Instance { get; } = new();
 public ValueTask DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
 delay <= TimeSpan.Zero ? ValueTask.CompletedTask : new(Task.Delay(delay, cancellationToken));
}

internal sealed class ReferenceCorpusFeatureWorkItemProcessor
{
 private readonly IReferenceCorpusFeatureFamilyAnalyzer _analyzer;
 private readonly IReferenceCorpusFeatureValidationDelay _delay;
 private readonly ReferenceCorpusAnalysisRetryPolicy _retryPolicy = new();

 public ReferenceCorpusFeatureWorkItemProcessor(IReferenceCorpusFeatureFamilyAnalyzer analyzer, IReferenceCorpusFeatureValidationDelay? delay = null)
 {
 _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
 _delay = delay ?? ReferenceCorpusFeatureValidationDelay.Instance;
 }

 public async ValueTask<ReferenceCorpusFeatureWorkItemResult> ProcessAsync(ReferenceCorpusFeatureWorkItemInput input, CancellationToken cancellationToken)
 {
 Validate(input);
 var diagnostics = new List<string>();
 var tokensSpent = 0;
 ReferenceCorpusFeatureFamilyValidationResult? validation = null;

 for (var attempt = 1; attempt <= input.MaxValidationAttempts; attempt++)
 {
 cancellationToken.ThrowIfCancellationRequested();
 var remaining = input.TokenEnvelope.RemainingTokens - tokensSpent;
 if (remaining <= 0)
 {
 diagnostics.Add("Feature analysis token budget was exhausted before another model call.");
 return Build(ReferenceCorpusFeatureWorkItemStatuses.BudgetExhausted, validation, tokensSpent, diagnostics);
 }

 var output = await _analyzer.AnalyzeAsync(
 new ReferenceCorpusFeatureFamilyAnalysisInput(input.RunId, input.AnchorId, input.NodeId, input.NodeType, input.NodeText, input.FeatureFamily, ReferenceCorpusFeatureFamilySchemaRegistry.Get(input.FeatureFamily))
 {
Context = input.Context,
 ModelSelection = input.ModelSelection,
MaxOutputTokens = Math.Min(remaining, input.TokenEnvelope.MaximumOutputTokens)
 },
 cancellationToken);

 var chargedTokens = output.TokensSpent > 0 ? output.TokensSpent : Math.Min(input.TokenEnvelope.UnknownUsageCharge, remaining);
 tokensSpent += chargedTokens;
 if (output.TokensSpent <= 0)
 {
 diagnostics.Add($"Model usage was unknown; conservatively charged {chargedTokens} tokens.");
 }

 validation = ReferenceCorpusFeatureFamilyOutputValidator.Validate(output.ModelOutputJson, input.FeatureFamily, input.NodeType, input.NodeText.Length);
 diagnostics.AddRange(validation.Diagnostics);
 if (validation.Status is ReferenceCorpusFeatureFamilyValidationStatuses.Passed or ReferenceCorpusFeatureFamilyValidationStatuses.Partial)
 {
 return Build(ReferenceCorpusFeatureWorkItemStatuses.Succeeded, validation, tokensSpent, diagnostics);
 }
 if (tokensSpent >= input.TokenEnvelope.RemainingTokens)
 {
 return Build(ReferenceCorpusFeatureWorkItemStatuses.BudgetExhausted, validation, tokensSpent, diagnostics);
 }

 var retry = _retryPolicy.Decide(new(ReferenceCorpusAnalysisRetryCategories.Validation, attempt, DateTimeOffset.UtcNow, RetryAfter: null));
 if (!retry.ShouldRetry || attempt >= input.MaxValidationAttempts)
 {
 break;
 }
 var delay = retry.NextAttemptAt!.Value - DateTimeOffset.UtcNow;
 await _delay.DelayAsync(delay < TimeSpan.Zero ? TimeSpan.Zero : delay, cancellationToken);
 }

 diagnostics.Add("Feature analysis validation retry limit was reached.");
 return Build(ReferenceCorpusFeatureWorkItemStatuses.ValidationFailed, validation, tokensSpent, diagnostics);
 }

 private static ReferenceCorpusFeatureWorkItemResult Build(string status, ReferenceCorpusFeatureFamilyValidationResult? validation, int tokensSpent, IReadOnlyList<string> diagnostics) =>
 new(status, validation?.Status ?? ReferenceCorpusFeatureFamilyValidationStatuses.Rejected, tokensSpent, validation?.AcceptedObservations ?? [], validation?.RejectedObservations ?? [], diagnostics);

 private static void Validate(ReferenceCorpusFeatureWorkItemInput input)
 {
 ArgumentNullException.ThrowIfNull(input);
 if (input.AnchorId <= 0 || string.IsNullOrWhiteSpace(input.RunId) || string.IsNullOrWhiteSpace(input.NodeId) || string.IsNullOrWhiteSpace(input.NodeText))
 {
 throw new ArgumentException("Frozen feature work item identity and text are required.", nameof(input));
 }
 if (input.MaxValidationAttempts is < 1 or > 4)
 {
 throw new ArgumentOutOfRangeException(nameof(input), "Validation attempts must be between 1 and 4.");
 }
 if (input.TokenEnvelope.RemainingTokens <= 0 || input.TokenEnvelope.MaximumOutputTokens <= 0 || input.TokenEnvelope.UnknownUsageCharge <= 0)
 {
 throw new ArgumentOutOfRangeException(nameof(input), "Token envelope values must be positive.");
 }
 }
}
