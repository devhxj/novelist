namespace Novelist.Core.App;

public static class ReferenceCorpusAnalysisRetryCategories
{
 public const string Validation = "validation";
 public const string ProviderTransient = "provider_transient";
 public const string Permanent = "permanent";

 public static IReadOnlyList<string> All { get; } = [Validation, ProviderTransient, Permanent];
}

public sealed record ReferenceCorpusAnalysisRetryRequest(
 string Category,
 int AttemptNumber,
 DateTimeOffset FailedAt,
 TimeSpan? RetryAfter);

public sealed record ReferenceCorpusAnalysisRetryDecision(
 bool ShouldRetry,
 int MaximumAttemptCount,
 int? NextAttemptNumber,
 DateTimeOffset? NextAttemptAt);

public interface IReferenceCorpusAnalysisRetryRandom
{
 double NextUnitInterval();
}

public sealed class ReferenceCorpusAnalysisRetryPolicy
{
 private const int ValidationMaximumAttemptCount = 4;
 private const int ProviderMaximumAttemptCount = 5;
 private static readonly TimeSpan[] ValidationRetryDelays =
 [
 TimeSpan.Zero,
 TimeSpan.FromSeconds(1),
 TimeSpan.FromSeconds(3)
 ];

 private readonly IReferenceCorpusAnalysisRetryRandom _random;

 public ReferenceCorpusAnalysisRetryPolicy(IReferenceCorpusAnalysisRetryRandom? random = null)
 {
 _random = random ?? SharedReferenceCorpusAnalysisRetryRandom.Instance;
 }

 public ReferenceCorpusAnalysisRetryDecision Decide(ReferenceCorpusAnalysisRetryRequest request)
 {
 ArgumentNullException.ThrowIfNull(request);
 if (request.AttemptNumber < 1)
 {
 throw new ArgumentOutOfRangeException(nameof(request), "Attempt number must be positive.");
 }
 if (request.RetryAfter < TimeSpan.Zero)
 {
 throw new ArgumentOutOfRangeException(nameof(request), "Retry-After cannot be negative.");
 }

 return request.Category switch
 {
 ReferenceCorpusAnalysisRetryCategories.Validation => DecideValidation(request),
 ReferenceCorpusAnalysisRetryCategories.ProviderTransient => DecideProviderTransient(request),
 ReferenceCorpusAnalysisRetryCategories.Permanent => DoNotRetry(maximumAttemptCount: 1),
 _ => throw new ArgumentOutOfRangeException(nameof(request), request.Category, "Unknown analysis retry category.")
 };
 }

 private static ReferenceCorpusAnalysisRetryDecision DecideValidation(
 ReferenceCorpusAnalysisRetryRequest request)
 {
 if (request.AttemptNumber >= ValidationMaximumAttemptCount)
 {
 return DoNotRetry(ValidationMaximumAttemptCount);
 }

 var delay = ValidationRetryDelays[request.AttemptNumber - 1];
 return Retry(request, ValidationMaximumAttemptCount, delay);
 }

 private ReferenceCorpusAnalysisRetryDecision DecideProviderTransient(
 ReferenceCorpusAnalysisRetryRequest request)
 {
 if (request.AttemptNumber >= ProviderMaximumAttemptCount)
 {
 return DoNotRetry(ProviderMaximumAttemptCount);
 }

 var unit = _random.NextUnitInterval();
 if (!double.IsFinite(unit) || unit < 0 || unit >= 1)
 {
 throw new InvalidOperationException("Retry random source must return a finite value in [0, 1).");
 }

 var exponentialCeilingSeconds = Math.Pow(2, request.AttemptNumber - 1);
 var jitterDelay = TimeSpan.FromSeconds(exponentialCeilingSeconds * unit);
 return Retry(request, ProviderMaximumAttemptCount, jitterDelay);
 }

 private static ReferenceCorpusAnalysisRetryDecision Retry(
 ReferenceCorpusAnalysisRetryRequest request,
 int maximumAttemptCount,
 TimeSpan policyDelay)
 {
 var delay = request.RetryAfter is { } retryAfter && retryAfter > policyDelay
 ? retryAfter
 : policyDelay;
 return new ReferenceCorpusAnalysisRetryDecision(
 true,
 maximumAttemptCount,
 request.AttemptNumber + 1,
 request.FailedAt.Add(delay));
 }

 private static ReferenceCorpusAnalysisRetryDecision DoNotRetry(int maximumAttemptCount) =>
 new(false, maximumAttemptCount, null, null);

 private sealed class SharedReferenceCorpusAnalysisRetryRandom : IReferenceCorpusAnalysisRetryRandom
 {
 public static SharedReferenceCorpusAnalysisRetryRandom Instance { get; } = new();

 public double NextUnitInterval() => Random.Shared.NextDouble();
 }
}
