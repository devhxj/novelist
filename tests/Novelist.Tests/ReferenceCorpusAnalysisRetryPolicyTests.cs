using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceCorpusAnalysisRetryPolicyTests
{
 [Theory]
 [InlineData(1, 0)]
 [InlineData(2, 1)]
 [InlineData(3, 3)]
 public void ValidationFailureUsesBoundedSchedule(int attemptNumber, int expectedDelaySeconds)
 {
 var now = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
 var decision = new ReferenceCorpusAnalysisRetryPolicy(new FixedRandom(0.5)).Decide(
 new ReferenceCorpusAnalysisRetryRequest(
 ReferenceCorpusAnalysisRetryCategories.Validation,
 attemptNumber,
 now,
 RetryAfter: null));

 Assert.True(decision.ShouldRetry);
 Assert.Equal(attemptNumber + 1, decision.NextAttemptNumber);
 Assert.Equal(now.AddSeconds(expectedDelaySeconds), decision.NextAttemptAt);
 Assert.Equal(4, decision.MaximumAttemptCount);
 }

 [Fact]
 public void ValidationFailureStopsAfterThreeRetries()
 {
 var decision = new ReferenceCorpusAnalysisRetryPolicy().Decide(
 new ReferenceCorpusAnalysisRetryRequest(
 ReferenceCorpusAnalysisRetryCategories.Validation,
 AttemptNumber: 4,
 FailedAt: DateTimeOffset.UtcNow,
 RetryAfter: null));

 Assert.False(decision.ShouldRetry);
 Assert.Null(decision.NextAttemptAt);
 Assert.Null(decision.NextAttemptNumber);
 Assert.Equal(4, decision.MaximumAttemptCount);
 }

 [Theory]
 [InlineData(1, 0.00, 0.00)]
 [InlineData(1, 0.50, 0.50)]
 [InlineData(2, 0.50, 1.00)]
 [InlineData(4, 0.75, 6.00)]
 public void ProviderTransientUsesInjectableFullJitter(
 int attemptNumber,
 double randomValue,
 double expectedDelaySeconds)
 {
 var now = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
 var decision = new ReferenceCorpusAnalysisRetryPolicy(new FixedRandom(randomValue)).Decide(
 new ReferenceCorpusAnalysisRetryRequest(
 ReferenceCorpusAnalysisRetryCategories.ProviderTransient,
 attemptNumber,
 now,
 RetryAfter: null));

 Assert.True(decision.ShouldRetry);
 Assert.Equal(attemptNumber + 1, decision.NextAttemptNumber);
 Assert.Equal(now.AddSeconds(expectedDelaySeconds), decision.NextAttemptAt);
 Assert.Equal(5, decision.MaximumAttemptCount);
 }

 [Fact]
 public void ProviderTransientRespectsRetryAfter()
 {
 var now = new DateTimeOffset(2026, 7, 10, 8, 0, 0, TimeSpan.Zero);
 var decision = new ReferenceCorpusAnalysisRetryPolicy(new FixedRandom(0.1)).Decide(
 new ReferenceCorpusAnalysisRetryRequest(
 ReferenceCorpusAnalysisRetryCategories.ProviderTransient,
 AttemptNumber: 3,
 FailedAt: now,
 RetryAfter: TimeSpan.FromSeconds(17)));

 Assert.True(decision.ShouldRetry);
 Assert.Equal(now.AddSeconds(17), decision.NextAttemptAt);
 }

 [Fact]
 public void ProviderTransientStopsAtFifthAttempt()
 {
 var decision = new ReferenceCorpusAnalysisRetryPolicy(new FixedRandom(0.5)).Decide(
 new ReferenceCorpusAnalysisRetryRequest(
 ReferenceCorpusAnalysisRetryCategories.ProviderTransient,
 AttemptNumber: 5,
 FailedAt: DateTimeOffset.UtcNow,
 RetryAfter: null));

 Assert.False(decision.ShouldRetry);
 Assert.Equal(5, decision.MaximumAttemptCount);
 }

 [Fact]
 public void PermanentFailureNeverRetries()
 {
 var decision = new ReferenceCorpusAnalysisRetryPolicy().Decide(
 new ReferenceCorpusAnalysisRetryRequest(
 ReferenceCorpusAnalysisRetryCategories.Permanent,
 AttemptNumber: 1,
 FailedAt: DateTimeOffset.UtcNow,
 RetryAfter: TimeSpan.FromHours(1)));

 Assert.False(decision.ShouldRetry);
 Assert.Null(decision.NextAttemptAt);
 Assert.Null(decision.NextAttemptNumber);
 Assert.Equal(1, decision.MaximumAttemptCount);
 }

 [Fact]
 public void PolicyRejectsUnknownCategoryAndInvalidRandomValue()
 {
 var now = DateTimeOffset.UtcNow;

 Assert.Throws<ArgumentOutOfRangeException>(() =>
 new ReferenceCorpusAnalysisRetryPolicy().Decide(
 new ReferenceCorpusAnalysisRetryRequest("unknown", 1, now, null)));
 Assert.Throws<InvalidOperationException>(() =>
 new ReferenceCorpusAnalysisRetryPolicy(new FixedRandom(1)).Decide(
 new ReferenceCorpusAnalysisRetryRequest(
 ReferenceCorpusAnalysisRetryCategories.ProviderTransient,
 1,
 now,
 null)));
 }

 private sealed class FixedRandom(double value) : IReferenceCorpusAnalysisRetryRandom
 {
 public double NextUnitInterval() => value;
 }
}
