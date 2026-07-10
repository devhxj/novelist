using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

internal sealed class MultiStrategyReferenceCorpusBlueprintCandidateAssembler : IReferenceCorpusBlueprintCandidateAssembler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public ValueTask<IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload>> AssembleCandidatesAsync(
        ReferenceCorpusBlueprintCandidateAssemblyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ReferenceCorpusBlueprintCandidatePayload> candidates = BuildBlueprintCandidates(
                request.QueryContext,
                request.Candidates,
                request.RequestedCount,
                request.Feedback,
                request.DiagnosticGapReasons,
                request.FeedbackReason,
                request.HistoricalFeedback)
            .ToArray();
        return ValueTask.FromResult(candidates);
    }

    private static IEnumerable<ReferenceCorpusBlueprintCandidatePayload> BuildBlueprintCandidates(
        ReferenceCorpusQueryContextPayload queryContext,
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        int requestedCount,
        ReferenceCorpusBlueprintFeedbackPayload? feedback,
        IReadOnlyList<string> diagnosticGapReasons,
        string feedbackReason,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback)
    {
        var queryHash = StableHash(JsonSerializer.Serialize(queryContext, JsonOptions));
        var strategies = CandidateStrategies(queryContext, candidates, feedback, historicalFeedback);
        var rejectedBlueprintIds = NormalizeTextSet(feedback?.RejectedBlueprintIds);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var yielded = 0;
        foreach (var strategy in strategies)
        {
            var selected = strategy.Candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.NodeId))
                .GroupBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                .Take(3)
                .ToArray();
            var key = BlueprintNodeSetKey(selected);
            if (selected.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            var candidate = BuildBlueprintCandidate(
                queryContext,
                queryHash,
                strategy.Name,
                selected,
                feedback,
                diagnosticGapReasons,
                feedbackReason);
            if (rejectedBlueprintIds.Contains(candidate.Blueprint.BlueprintId))
            {
                continue;
            }

            yield return candidate;
            yielded++;
            if (yielded >= requestedCount)
            {
                yield break;
            }
        }

        var fallbackOffset = 0;
        var fallbackCandidates = OrderCandidatesByHistoricalFeedback(candidates, historicalFeedback);
        while (yielded < requestedCount && fallbackOffset < candidates.Count)
        {
            var selected = fallbackCandidates
                .Skip(fallbackOffset)
                .Take(1)
                .ToArray();
            fallbackOffset++;
            var key = BlueprintNodeSetKey(selected);
            if (selected.Length == 0 || !seen.Add(key))
            {
                continue;
            }

            var candidate = BuildBlueprintCandidate(
                queryContext,
                queryHash,
                "fallback_single_source_m1",
                selected,
                feedback,
                diagnosticGapReasons,
                feedbackReason);
            if (rejectedBlueprintIds.Contains(candidate.Blueprint.BlueprintId))
            {
                continue;
            }

            yield return candidate;
            yielded++;
        }
    }

    private static IReadOnlyList<BlueprintCandidateStrategy> CandidateStrategies(
        ReferenceCorpusQueryContextPayload queryContext,
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        ReferenceCorpusBlueprintFeedbackPayload? feedback,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback)
    {
        var byScore = OrderCandidatesByHistoricalFeedback(candidates, historicalFeedback);
        var byLibrary = candidates
            .GroupBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .Select(group => group
                .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                .First())
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .ToArray();
        var byAnchor = candidates
            .GroupBy(candidate => candidate.AnchorId)
            .Select(group => group
                .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                .First())
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.AnchorId)
            .ToArray();
        var hook = candidates
            .Where(candidate => IsHookLike(queryContext, candidate))
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
            .ToArray();

        var avoidLibraryIds = NormalizeTextSet(feedback?.AvoidLibraryIds);
        var avoidAnchorIds = NormalizeLongSet(feedback?.AvoidAnchorIds);
        var feedbackFirst = feedback is null
            ? []
            : byScore
                .OrderBy(candidate => avoidLibraryIds.Contains(candidate.LibraryId) ? 1 : 0)
                .ThenBy(candidate => avoidAnchorIds.Contains(candidate.AnchorId) ? 1 : 0)
                .ThenBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                .ToArray();

        var strategies = new List<BlueprintCandidateStrategy>();
        var problemTags = NormalizeTextSet(feedback?.ProblemTags);
        if (HasM4StrategySignals(candidates))
        {
            strategies.AddRange([
                new BlueprintCandidateStrategy(
                    "emotion_priority_m4",
                    BuildStrategyProfileCandidates(candidates, byScore, historicalFeedback, EmotionPriorityScore, ["rhythm", "technique", "narrative", "emotion"])),
                new BlueprintCandidateStrategy(
                    "rhythm_priority_m4",
                    BuildStrategyProfileCandidates(candidates, byScore, historicalFeedback, RhythmPriorityScore, ["emotion", "narrative", "technique", "rhythm"])),
                new BlueprintCandidateStrategy(
                    "technique_diversity_m4",
                    BuildStrategyProfileCandidates(candidates, byScore, historicalFeedback, TechniqueDiversityScore, ["emotion", "narrative", "rhythm", "technique"])),
                new BlueprintCandidateStrategy(
                    "scene_template_m4",
                    BuildStrategyProfileCandidates(candidates, byScore, historicalFeedback, SceneTemplateScore, ["rhythm", "technique", "emotion", "narrative"]))
            ]);
        }

        if (WantsSlowerRhythm(feedback))
        {
            var rhythmSlowFirst = BuildRhythmSlowCandidates(candidates, byScore, historicalFeedback);
            if (rhythmSlowFirst.Count > 0)
            {
                strategies.Add(new BlueprintCandidateStrategy("rhythm_slow_m1", rhythmSlowFirst));
            }
        }

        if (problemTags.Contains("source_repetition"))
        {
            var sourceRepetitionFirst = BuildSourceRepetitionDiversityCandidates(candidates, byScore);
            if (sourceRepetitionFirst.Count > 0)
            {
                strategies.Add(new BlueprintCandidateStrategy("source_repetition_diversity_m1", sourceRepetitionFirst));
            }
        }

        strategies.AddRange([
            new BlueprintCandidateStrategy(feedback is null ? "score_focus_m1" : "feedback_shift_m1", feedbackFirst.Length == 0 ? byScore : feedbackFirst),
            new BlueprintCandidateStrategy("source_diversity_m1", byLibrary.Length == 0 ? byScore : byLibrary),
            new BlueprintCandidateStrategy("anchor_diversity_m1", byAnchor.Length == 0 ? byScore : byAnchor),
            new BlueprintCandidateStrategy("withheld_hook_m1", hook.Length == 0 ? byScore : hook)
        ]);
        return strategies;
    }

    private static bool HasM4StrategySignals(IReadOnlyList<ReferenceCorpusCandidatePayload> candidates)
    {
        return candidates.Any(candidate => FeatureEvidenceScore(candidate, "emotion") > 0) &&
            candidates.Any(candidate => FeatureEvidenceScore(candidate, "rhythm") > 0) &&
            candidates.Any(candidate => FeatureEvidenceScore(candidate, "narrative") > 0) &&
            candidates.Any(candidate => ScoreComponent(candidate, "technique_fit") > 0);
    }

    private static IReadOnlyList<ReferenceCorpusCandidatePayload> BuildStrategyProfileCandidates(
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        IReadOnlyList<ReferenceCorpusCandidatePayload> byScore,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback,
        Func<ReferenceCorpusCandidatePayload, double> strategyScore,
        IReadOnlyList<string> backfillPriority)
    {
        if (candidates.Count == 0)
        {
            return byScore;
        }

        var pool = candidates
            .Where(candidate => strategyScore(candidate) > 0)
            .ToArray();
        if (pool.Length == 0)
        {
            pool = candidates.ToArray();
        }

        var ordered = new List<ReferenceCorpusCandidatePayload>();
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);

        void AddDistinct(IEnumerable<ReferenceCorpusCandidatePayload> source)
        {
            foreach (var candidate in source)
            {
                if (seenNodeIds.Add(candidate.NodeId))
                {
                    ordered.Add(candidate);
                }
            }
        }

        AddDistinct(BuildCoverageAwareProfilePrefix(candidates, pool, historicalFeedback, strategyScore, backfillPriority));

        AddDistinct(pool
            .GroupBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .Select(group => OrderByStrategyProfile(group, historicalFeedback, strategyScore).First())
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(strategyScore)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal));

        AddDistinct(pool
            .GroupBy(candidate => new { candidate.LibraryId, candidate.AnchorId })
            .Select(group => OrderByStrategyProfile(group, historicalFeedback, strategyScore).First())
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(strategyScore)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AnchorId)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal));

        AddDistinct(OrderByStrategyProfile(pool, historicalFeedback, strategyScore));
        AddDistinct(byScore);
        return ordered.Count == 0 ? byScore : ordered;
    }

    private static IReadOnlyList<ReferenceCorpusCandidatePayload> BuildCoverageAwareProfilePrefix(
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        IReadOnlyList<ReferenceCorpusCandidatePayload> strategyPool,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback,
        Func<ReferenceCorpusCandidatePayload, double> strategyScore,
        IReadOnlyList<string> backfillPriority)
    {
        var selected = new List<ReferenceCorpusCandidatePayload>(capacity: 3);
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);

        void Add(ReferenceCorpusCandidatePayload candidate)
        {
            if (seenNodeIds.Add(candidate.NodeId))
            {
                selected.Add(candidate);
            }
        }

        var profileHead = OrderByStrategyProfile(strategyPool, historicalFeedback, strategyScore).FirstOrDefault();
        if (profileHead is not null)
        {
            Add(profileHead);
        }

        while (selected.Count < 3)
        {
            var missingDimensions = MissingM4CoverageDimensions(selected, backfillPriority);
            if (missingDimensions.Count == 0)
            {
                break;
            }

            var next = BestCoverageBackfillCandidate(
                candidates,
                selected,
                seenNodeIds,
                missingDimensions,
                historicalFeedback,
                strategyScore);
            if (next is null)
            {
                break;
            }

            Add(next);
        }

        return selected;
    }

    private static ReferenceCorpusCandidatePayload? BestCoverageBackfillCandidate(
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        IReadOnlyList<ReferenceCorpusCandidatePayload> selected,
        IReadOnlySet<string> seenNodeIds,
        IReadOnlyList<string> missingDimensions,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback,
        Func<ReferenceCorpusCandidatePayload, double> strategyScore)
    {
        foreach (var targetDimension in missingDimensions)
        {
            var next = candidates
                .Where(candidate => !seenNodeIds.Contains(candidate.NodeId))
                .Select(candidate => new
                {
                    Candidate = candidate,
                    TargetCoverage = M4DimensionCoverage(candidate, targetDimension),
                    CoverageGain = M4CoverageGain(candidate, missingDimensions),
                    SourceDiversityGain = SourceDiversityGain(candidate, selected)
                })
                .Where(item => item.TargetCoverage > 0)
                .OrderBy(item => HistoricalFeedbackPenalty(item.Candidate, historicalFeedback))
                .ThenByDescending(item => item.SourceDiversityGain)
                .ThenByDescending(item => item.TargetCoverage)
                .ThenByDescending(item => item.CoverageGain)
                .ThenByDescending(item => strategyScore(item.Candidate))
                .ThenByDescending(item => item.Candidate.Score)
                .ThenBy(item => item.Candidate.NodeId, StringComparer.Ordinal)
                .Select(item => item.Candidate)
                .FirstOrDefault();
            if (next is not null)
            {
                return next;
            }
        }

        return null;
    }

    private static int SourceDiversityGain(
        ReferenceCorpusCandidatePayload candidate,
        IReadOnlyList<ReferenceCorpusCandidatePayload> selected)
    {
        var gain = 0;
        if (!selected.Any(item => string.Equals(item.LibraryId, candidate.LibraryId, StringComparison.Ordinal)))
        {
            gain++;
        }

        if (!selected.Any(item => item.AnchorId == candidate.AnchorId))
        {
            gain++;
        }

        return gain;
    }

    private static IReadOnlyList<string> MissingM4CoverageDimensions(
        IReadOnlyList<ReferenceCorpusCandidatePayload> selected,
        IReadOnlyList<string> backfillPriority)
    {
        var missing = new HashSet<string>(StringComparer.Ordinal);
        if (FeatureCoverageScore(selected, "emotion") <= 0)
        {
            missing.Add("emotion");
        }

        if (FeatureCoverageScore(selected, "rhythm") <= 0)
        {
            missing.Add("rhythm");
        }

        if (FeatureCoverageScore(selected, "narrative", "narrative_function") <= 0)
        {
            missing.Add("narrative");
        }

        if (selected.Select(candidate => ScoreComponent(candidate, "technique_fit")).DefaultIfEmpty(0).Max() <= 0)
        {
            missing.Add("technique");
        }

        return backfillPriority
            .Concat(["emotion", "rhythm", "narrative", "technique"])
            .Where(missing.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static double M4CoverageGain(
        ReferenceCorpusCandidatePayload candidate,
        IReadOnlyList<string> missingDimensions)
    {
        var gain = 0.0;
        foreach (var dimension in missingDimensions)
        {
            gain += M4DimensionCoverage(candidate, dimension);
        }

        return gain;
    }

    private static double M4DimensionCoverage(
        ReferenceCorpusCandidatePayload candidate,
        string dimension)
    {
        return dimension switch
        {
            "emotion" => FeatureEvidenceScore(candidate, "emotion"),
            "rhythm" => FeatureEvidenceScore(candidate, "rhythm"),
            "narrative" => Math.Max(
                FeatureEvidenceScore(candidate, "narrative"),
                FeatureEvidenceScore(candidate, "narrative_function")),
            "technique" => ScoreComponent(candidate, "technique_fit"),
            _ => 0
        };
    }

    private static IOrderedEnumerable<ReferenceCorpusCandidatePayload> OrderByStrategyProfile(
        IEnumerable<ReferenceCorpusCandidatePayload> candidates,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback,
        Func<ReferenceCorpusCandidatePayload, double> strategyScore)
    {
        return candidates
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(strategyScore)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal);
    }

    private static double EmotionPriorityScore(ReferenceCorpusCandidatePayload candidate)
    {
        return FeatureEvidenceScore(candidate, "emotion") * 100 +
            FeatureEvidenceScore(candidate, "action") * 10 +
            ScoreComponent(candidate, "observation_fit");
    }

    private static double RhythmPriorityScore(ReferenceCorpusCandidatePayload candidate)
    {
        return FeatureEvidenceScore(candidate, "rhythm") * 100 +
            RhythmSlowFitScore(candidate) +
            ScoreComponent(candidate, "observation_fit");
    }

    private static double TechniqueDiversityScore(ReferenceCorpusCandidatePayload candidate)
    {
        return ScoreComponent(candidate, "technique_fit") * 100 +
            FeatureEvidenceScore(candidate, "action") * 10 +
            ScoreComponent(candidate, "source_quality");
    }

    private static double SceneTemplateScore(ReferenceCorpusCandidatePayload candidate)
    {
        return FeatureEvidenceScore(candidate, "narrative") * 100 +
            FeatureEvidenceScore(candidate, "action") * 10 +
            ScoreComponent(candidate, "chapter_fit");
    }

    private static double FeatureEvidenceScore(ReferenceCorpusCandidatePayload candidate, string featureFamily)
    {
        return candidate.Evidence
            .Where(evidence => string.Equals(evidence.FeatureFamily, featureFamily, StringComparison.Ordinal))
            .Select(evidence => evidence.Confidence)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static double ScoreComponent(ReferenceCorpusCandidatePayload candidate, string componentName)
    {
        return candidate.ScoreComponents.TryGetValue(componentName, out var value) ? value : 0;
    }

    private static IReadOnlyList<ReferenceCorpusCandidatePayload> BuildRhythmSlowCandidates(
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        IReadOnlyList<ReferenceCorpusCandidatePayload> byScore,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback)
    {
        if (candidates.Count == 0)
        {
            return byScore;
        }

        var rhythmCandidates = candidates
            .Where(HasRhythmLengthEvidence)
            .ToArray();
        var pool = rhythmCandidates.Length == 0 ? candidates : rhythmCandidates;
        var ordered = new List<ReferenceCorpusCandidatePayload>();
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);

        void AddDistinct(IEnumerable<ReferenceCorpusCandidatePayload> source)
        {
            foreach (var candidate in source)
            {
                if (seenNodeIds.Add(candidate.NodeId))
                {
                    ordered.Add(candidate);
                }
            }
        }

        AddDistinct(pool
            .GroupBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .Select(group => OrderByRhythmSlowFit(group, historicalFeedback).First())
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(RhythmSlowFitScore)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal));

        AddDistinct(pool
            .GroupBy(candidate => new { candidate.LibraryId, candidate.AnchorId })
            .Select(group => OrderByRhythmSlowFit(group, historicalFeedback).First())
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(RhythmSlowFitScore)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AnchorId)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal));

        AddDistinct(OrderByRhythmSlowFit(pool, historicalFeedback));
        AddDistinct(byScore);
        return ordered.Count == 0 ? byScore : ordered;
    }

    private static IReadOnlyList<ReferenceCorpusCandidatePayload> BuildSourceRepetitionDiversityCandidates(
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        IReadOnlyList<ReferenceCorpusCandidatePayload> byScore)
    {
        if (candidates.Count == 0)
        {
            return byScore;
        }

        var ordered = new List<ReferenceCorpusCandidatePayload>();
        var seenNodeIds = new HashSet<string>(StringComparer.Ordinal);

        void AddDistinct(IEnumerable<ReferenceCorpusCandidatePayload> source)
        {
            foreach (var candidate in source)
            {
                if (seenNodeIds.Add(candidate.NodeId))
                {
                    ordered.Add(candidate);
                }
            }
        }

        AddDistinct(candidates
            .GroupBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.AnchorId)
                .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AnchorId));

        AddDistinct(candidates
            .GroupBy(candidate => new { candidate.LibraryId, candidate.AnchorId })
            .Select(group => group
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
                .First())
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.LibraryId, StringComparer.Ordinal)
            .ThenBy(candidate => candidate.AnchorId));

        AddDistinct(byScore);
        return ordered.Count == 0 ? byScore : ordered;
    }

    private static IOrderedEnumerable<ReferenceCorpusCandidatePayload> OrderByRhythmSlowFit(
        IEnumerable<ReferenceCorpusCandidatePayload> candidates,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback)
    {
        return candidates
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(RhythmSlowFitScore)
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal);
    }

    private static double RhythmSlowFitScore(ReferenceCorpusCandidatePayload candidate)
    {
        var rhythmEvidenceBonus = HasRhythmLengthEvidence(candidate) ? 100.0 : 0.0;
        return rhythmEvidenceBonus + candidate.TextPreview.EnumerateRunes().Count();
    }

    private static bool HasRhythmLengthEvidence(ReferenceCorpusCandidatePayload candidate)
    {
        return candidate.Evidence.Any(evidence =>
            string.Equals(evidence.FeatureFamily, "rhythm", StringComparison.Ordinal) &&
            string.Equals(evidence.FeatureKey, "length_band", StringComparison.Ordinal));
    }

    private static ReferenceCorpusCandidatePayload[] OrderCandidatesByHistoricalFeedback(
        IReadOnlyList<ReferenceCorpusCandidatePayload> candidates,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback)
    {
        return candidates
            .OrderBy(candidate => HistoricalFeedbackPenalty(candidate, historicalFeedback))
            .ThenByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.NodeId, StringComparer.Ordinal)
            .ToArray();
    }

    private static int HistoricalFeedbackPenalty(
        ReferenceCorpusCandidatePayload candidate,
        ReferenceCorpusHistoricalFeedbackProfile historicalFeedback)
    {
        if (historicalFeedback.IsEmpty)
        {
            return 0;
        }

        var penalty = 0;
        if (historicalFeedback.NodeHashes.Contains(FeedbackNodeHash(candidate.NodeId)))
        {
            penalty += 100;
        }

        if (historicalFeedback.LibraryHashes.Contains(FeedbackLibraryHash(candidate.LibraryId)))
        {
            penalty += 10;
        }

        if (historicalFeedback.AnchorIds.Contains(candidate.AnchorId))
        {
            penalty += 10;
        }

        return penalty;
    }

    private static ReferenceCorpusBlueprintCandidatePayload BuildBlueprintCandidate(
        ReferenceCorpusQueryContextPayload queryContext,
        string queryHash,
        string strategy,
        IReadOnlyList<ReferenceCorpusCandidatePayload> selected,
        ReferenceCorpusBlueprintFeedbackPayload? feedback,
        IReadOnlyList<string> diagnosticGapReasons,
        string feedbackReason)
    {
        var fingerprint = StableHash(queryHash, BlueprintNodeSetKey(selected));
        var beats = selected
            .Select((candidate, index) => new ReferenceCorpusInsertionBlueprintBeatPayload(
                BeatId: "corpus-beat-" + StableHash(fingerprint, index.ToString(CultureInfo.InvariantCulture), candidate.NodeId)[..16],
                BeatIndex: index,
                RoleInBeat: index == 0 ? "opening_source_sentence" : "supporting_source_sentence",
                NarrativeFunction: queryContext.RequiredNarrativeFunctions.ElementAtOrDefault(index) ??
                    queryContext.RequiredNarrativeFunctions.FirstOrDefault() ??
                    "support_current_chapter",
                NodeIds: [candidate.NodeId]))
            .ToArray();
        var blueprint = new ReferenceCorpusInsertionBlueprintPayload(
            BlueprintId: "corpus-blueprint-" + fingerprint[..16],
            QueryContextHash: queryHash,
            Strategy: strategy,
            Beats: beats);
        var sourceDistribution = selected
            .GroupBy(candidate => (candidate.LibraryId, candidate.AnchorId))
            .Select(group => new ReferenceCorpusBlueprintSourcePayload(
                group.Key.LibraryId,
                group.Key.AnchorId,
                group.Count()))
            .OrderBy(item => item.LibraryId, StringComparer.Ordinal)
            .ThenBy(item => item.AnchorId)
            .ToArray();
        var coverage = BlueprintCoverageDiagnostics(selected, diagnosticGapReasons);
        var gapPositions = BuildBlueprintGapPositions(beats, selected);

        return new ReferenceCorpusBlueprintCandidatePayload(
            blueprint,
            sourceDistribution,
            CoverageScore: coverage.CoverageScore,
            GapReasons: coverage.GapReasons,
            FeedbackReason: feedback is null ? "initial_candidate" : feedbackReason,
            GapPositions: gapPositions);
    }

    private static string BlueprintNodeSetKey(IReadOnlyList<ReferenceCorpusCandidatePayload> selected)
    {
        return string.Join(
            "|",
            selected
                .Select(candidate => candidate.NodeId)
                .Where(nodeId => !string.IsNullOrWhiteSpace(nodeId))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));
    }

    private static double BlueprintBaseCoverageScore(IReadOnlyList<ReferenceCorpusCandidatePayload> selected)
    {
        if (selected.Count == 0)
        {
            return 0;
        }

        var averageScore = selected.Average(candidate => candidate.Score);
        var libraryDiversity = selected.Select(candidate => candidate.LibraryId).Distinct(StringComparer.Ordinal).Count() > 1 ? 0.15 : 0;
        var beatCoverage = Math.Min(1.0, selected.Count / 3.0);
        return Math.Round(Math.Clamp(averageScore * 0.7 + beatCoverage * 0.15 + libraryDiversity, 0, 1), 6);
    }

    private static IReadOnlyList<ReferenceCorpusBlueprintGapPositionPayload> BuildBlueprintGapPositions(
        IReadOnlyList<ReferenceCorpusInsertionBlueprintBeatPayload> beats,
        IReadOnlyList<ReferenceCorpusCandidatePayload> selected)
    {
        if (!HasM4CoverageSignals(selected))
        {
            return [];
        }

        var missingDimensions = MissingM4CoverageDimensions(selected, ["emotion", "rhythm", "narrative", "technique"]);
        if (missingDimensions.Count == 0)
        {
            return [];
        }

        var positions = new List<ReferenceCorpusBlueprintGapPositionPayload>();
        for (var index = 0; index < beats.Count && index < selected.Count; index++)
        {
            var beat = beats[index];
            var candidate = selected[index];
            var coveredDimensions = M4CoveredDimensions(candidate);
            var beatMissingDimensions = missingDimensions
                .Where(dimension => !coveredDimensions.Contains(dimension))
                .ToArray();
            if (beatMissingDimensions.Length == 0)
            {
                continue;
            }

            positions.Add(new ReferenceCorpusBlueprintGapPositionPayload(
                BeatId: beat.BeatId,
                BeatIndex: beat.BeatIndex,
                RoleInBeat: beat.RoleInBeat,
                NarrativeFunction: beat.NarrativeFunction,
                NodeIds: beat.NodeIds,
                CoveredDimensions: coveredDimensions,
                MissingDimensions: beatMissingDimensions,
                GapReasons: beatMissingDimensions.Select(M4DimensionGapReason).ToArray()));
        }

        return positions;
    }

    private static IReadOnlyList<string> M4CoveredDimensions(ReferenceCorpusCandidatePayload candidate)
    {
        var covered = new List<string>(capacity: 4);
        foreach (var dimension in new[] { "emotion", "rhythm", "narrative", "technique" })
        {
            if (M4DimensionCoverage(candidate, dimension) > 0)
            {
                covered.Add(dimension);
            }
        }

        return covered;
    }

    private static string M4DimensionGapReason(string dimension)
    {
        return dimension switch
        {
            "emotion" => "missing_emotion_evidence",
            "rhythm" => "missing_rhythm_evidence",
            "narrative" => "missing_narrative_evidence",
            "technique" => "missing_technique_coverage",
            _ => "missing_" + dimension
        };
    }

    private static BlueprintCoverageDiagnostic BlueprintCoverageDiagnostics(
        IReadOnlyList<ReferenceCorpusCandidatePayload> selected,
        IReadOnlyList<string> diagnosticGapReasons)
    {
        var gaps = new List<string>();
        gaps.AddRange(diagnosticGapReasons);
        var baseCoverageScore = BlueprintBaseCoverageScore(selected);
        if (selected.Count < 2)
        {
            gaps.Add("insufficient_beats");
        }

        if (selected.Select(candidate => candidate.LibraryId).Distinct(StringComparer.Ordinal).Count() < 2)
        {
            gaps.Add("single_library_source");
        }

        if (selected.Select(candidate => candidate.AnchorId).Distinct().Count() < 2)
        {
            gaps.Add("single_anchor_source");
        }

        if (!HasM4CoverageSignals(selected))
        {
            return new BlueprintCoverageDiagnostic(baseCoverageScore, NormalizeDiagnosticGapReasons(gaps));
        }

        var m4CoverageScore = BlueprintM4CoverageScore(selected, gaps);
        var sourceDiversityScore = BlueprintSourceDiversityScore(selected);
        var coverageScore = Math.Round(
            Math.Clamp(
                baseCoverageScore * 0.45 +
                m4CoverageScore * 0.45 +
                sourceDiversityScore * 0.10,
                0,
                1),
            6);
        return new BlueprintCoverageDiagnostic(coverageScore, NormalizeDiagnosticGapReasons(gaps));
    }

    private static bool HasM4CoverageSignals(IReadOnlyList<ReferenceCorpusCandidatePayload> selected)
    {
        return selected.Any(candidate =>
            FeatureEvidenceScore(candidate, "emotion") > 0 ||
            FeatureEvidenceScore(candidate, "rhythm") > 0 ||
            FeatureEvidenceScore(candidate, "narrative") > 0 ||
            FeatureEvidenceScore(candidate, "narrative_function") > 0 ||
            ScoreComponent(candidate, "technique_fit") > 0);
    }

    private static double BlueprintM4CoverageScore(
        IReadOnlyList<ReferenceCorpusCandidatePayload> selected,
        List<string> gaps)
    {
        var emotionCoverage = FeatureCoverageScore(selected, "emotion");
        var rhythmCoverage = FeatureCoverageScore(selected, "rhythm");
        var narrativeCoverage = FeatureCoverageScore(selected, "narrative", "narrative_function");
        var techniqueCoverage = selected
            .Select(candidate => ScoreComponent(candidate, "technique_fit"))
            .DefaultIfEmpty(0)
            .Max();

        AddMissingCoverageGap(gaps, emotionCoverage, "missing_emotion_evidence");
        AddMissingCoverageGap(gaps, rhythmCoverage, "missing_rhythm_evidence");
        AddMissingCoverageGap(gaps, narrativeCoverage, "missing_narrative_evidence");
        AddMissingCoverageGap(gaps, techniqueCoverage, "missing_technique_coverage");

        return Math.Clamp(
            (ClampUnit(emotionCoverage) +
                ClampUnit(rhythmCoverage) +
                ClampUnit(narrativeCoverage) +
                ClampUnit(techniqueCoverage)) / 4.0,
            0,
            1);
    }

    private static double FeatureCoverageScore(
        IReadOnlyList<ReferenceCorpusCandidatePayload> selected,
        params string[] featureFamilies)
    {
        var familySet = featureFamilies.ToHashSet(StringComparer.Ordinal);
        return selected
            .SelectMany(candidate => candidate.Evidence)
            .Where(evidence => familySet.Contains(evidence.FeatureFamily))
            .Select(evidence => evidence.Confidence)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static double BlueprintSourceDiversityScore(IReadOnlyList<ReferenceCorpusCandidatePayload> selected)
    {
        if (selected.Count == 0)
        {
            return 0;
        }

        var libraryDiversity = selected.Select(candidate => candidate.LibraryId).Distinct(StringComparer.Ordinal).Count() > 1 ? 0.5 : 0;
        var anchorDiversity = selected.Select(candidate => candidate.AnchorId).Distinct().Count() > 1 ? 0.5 : 0;
        return libraryDiversity + anchorDiversity;
    }

    private static void AddMissingCoverageGap(List<string> gaps, double coverage, string gapReason)
    {
        if (coverage <= 0)
        {
            gaps.Add(gapReason);
        }
    }

    private static double ClampUnit(double value)
    {
        return Math.Clamp(value, 0, 1);
    }

    private static IReadOnlyList<string> NormalizeDiagnosticGapReasons(IEnumerable<string> reasons)
    {
        var normalized = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reason in reasons)
        {
            var value = reason.Trim();
            if (value.Length > 0 && seen.Add(value))
            {
                normalized.Add(value);
            }
        }

        return normalized;
    }

    private static bool WantsSlowerRhythm(ReferenceCorpusBlueprintFeedbackPayload? feedback)
    {
        var problemTags = NormalizeTextSet(feedback?.ProblemTags);
        return problemTags.Contains("too_fast") ||
            ContainsAny(feedback?.Notes ?? string.Empty, "节奏太快", "节奏太急", "太快", "放慢", "慢一点");
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));
    }

    private static bool IsHookLike(
        ReferenceCorpusQueryContextPayload queryContext,
        ReferenceCorpusCandidatePayload candidate)
    {
        if (queryContext.RequiredNarrativeFunctions.Contains("withhold_answer", StringComparer.Ordinal) ||
            queryContext.CommercialMechanic.Contains("withheld", StringComparison.OrdinalIgnoreCase))
        {
            return candidate.TextPreview.Contains("没有立刻", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("不立刻", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("不开口", StringComparison.Ordinal) ||
                candidate.TextPreview.Contains("回头", StringComparison.Ordinal);
        }

        return false;
    }

    private static HashSet<string> NormalizeTextSet(IReadOnlyList<string>? values)
    {
        return values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .ToHashSet(StringComparer.Ordinal) ?? [];
    }

    private static HashSet<long> NormalizeLongSet(IReadOnlyList<long>? values)
    {
        return values?.ToHashSet() ?? [];
    }

    private static string FeedbackNodeHash(string nodeId)
    {
        return StableHash(nodeId)[..16];
    }

    private static string FeedbackLibraryHash(string libraryId)
    {
        return StableHash(libraryId)[..16];
    }

    private static string StableHash(params string[] parts)
    {
        var payload = Encoding.UTF8.GetBytes(string.Join('\u001f', parts));
        return Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
    }

    private sealed record BlueprintCandidateStrategy(
        string Name,
        IReadOnlyList<ReferenceCorpusCandidatePayload> Candidates);

    private sealed record BlueprintCoverageDiagnostic(
        double CoverageScore,
        IReadOnlyList<string> GapReasons);
}
