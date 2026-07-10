using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text;

namespace Novelist.Core.App;

public interface IReferenceCorpusTechniqueSpecimenAnalyzer
{
    ValueTask<ReferenceCorpusTechniqueSpecimenAnalysisOutput> AnalyzeAsync(
        ReferenceCorpusTechniqueSpecimenAnalysisInput input,
        CancellationToken cancellationToken);
}

public sealed record ReferenceCorpusTechniqueSpecimenAnalysisInput(
    string RunId,
    long AnchorId,
    string NodeId,
    string NodeType,
    string NodeText,
 IReadOnlyList<ReferenceCorpusTechniqueObservationEvidence> Observations)
{
public int? MaxOutputTokens { get; init; }

 public ReferenceCorpusFrozenModelSelection? ModelSelection { get; init; }
}

public sealed record ReferenceCorpusTechniqueObservationEvidence(
    string ObservationId,
    string FeatureFamily,
    string FeatureKey,
    string ValueKind,
    string? ValueText,
    double? ValueNum,
    bool? ValueBool,
    string? ValueJson,
    double? Intensity,
    double Confidence,
    int? EvidenceStart,
    int? EvidenceEnd,
    string? Explanation);

public sealed record ReferenceCorpusTechniqueSpecimenAnalysisOutput(
    string ModelOutputJson,
    int TokensSpent);

public sealed record ReferenceCorpusTechniqueSpecimenRunRequest(
    string RunId,
    long AnchorId,
    string SourceNodeType,
    string AnalyzerVersion,
    string ModelProvider,
    string ModelId,
    double MinObservationConfidence,
 int? TokenBudget,
 bool Resume,
 DateTimeOffset StartedAt)
{
    public IReferenceCorpusAnalysisExecutionControl ExecutionControl { get; init; } =
 ContinueReferenceCorpusAnalysisExecutionControl.Instance;
}

public sealed record ReferenceCorpusTechniqueSpecimenRunResult(
    string RunId,
    string Status,
    int? TokenBudget,
    int TokensSpent,
    string? ResumeCursor,
    int SpecimenCount,
    int ProcessedNodes,
    IReadOnlyList<string> Diagnostics);

public sealed record ReferenceCorpusTechniqueSpecimenCandidate(
    string SourceNodeId,
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
    string? MasteryNotes,
    IReadOnlyList<string> EvidenceObservationIds);

public sealed record ReferenceCorpusTechniqueSpecimenValidationResult(
    string Status,
    ReferenceCorpusTechniqueSpecimenCandidate? Candidate,
    IReadOnlyList<string> Diagnostics);

public static class ReferenceCorpusTechniqueSpecimenSchemaVersions
{
    public const string V1 = "reference-corpus-technique-specimen-v1";
}

public static class ReferenceCorpusTechniqueSpecimenValidationStatuses
{
    public const string Passed = "passed";
    public const string InvalidJson = "invalid_json";
    public const string InvalidSchema = "invalid_schema";
    public const string Rejected = "rejected";
}

public static class ReferenceCorpusTechniqueSpecimenOutputValidator
{
    private const int MaxShortTextLength = 160;
    private const int MaxLongTextLength = 600;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowedRootProperties = new(StringComparer.Ordinal)
    {
        "schema_version",
        "source_node_id",
        "technique_family",
        "technique_abstract",
        "trigger_context",
        "transfer_template",
        "transfer_slots",
        "effect_on_reader",
        "applicability_conditions",
        "failure_modes",
        "anti_patterns",
        "world_context_dependencies",
        "why_it_works",
        "confidence",
        "mastery_notes"
    };

    private static readonly HashSet<string> AllowedSlotProperties = new(StringComparer.Ordinal)
    {
        "slot_name",
        "purpose",
        "constraints"
    };

    private static readonly HashSet<string> AllowedWhyFactorProperties = new(StringComparer.Ordinal)
    {
        "factor",
        "observation_ids",
        "explanation"
    };

    private static readonly HashSet<string> SourceTermStopList = new(StringComparer.Ordinal)
    {
        "没有",
        "只是",
        "一个",
        "这个",
        "那个",
        "以及",
        "然后",
        "自己",
        "他们",
        "她们",
        "我们",
        "你们",
        "他的",
        "她的"
    };

    private static readonly HashSet<char> GenericShortTermChars =
    [
        '的', '了', '着', '过', '是', '在', '有', '和', '与', '或', '而', '及',
        '很', '更', '也', '还', '只', '没', '不', '无', '把', '被', '将',
        '他', '她', '它', '我', '你', '们', '说', '话', '开', '口',
        '角', '色', '情', '绪', '动', '作', '细', '节', '沉', '默'
    ];

    public static ReferenceCorpusTechniqueSpecimenValidationResult Validate(
        string modelOutputJson,
        string expectedSourceNodeId,
        string sourceText,
        IReadOnlyList<ReferenceCorpusTechniqueObservationEvidence> observations)
    {
        if (string.IsNullOrWhiteSpace(expectedSourceNodeId))
        {
            throw new ArgumentException("Source node id is required.", nameof(expectedSourceNodeId));
        }

        var knownObservationIds = observations
            .Select(item => item.ObservationId)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.Ordinal);
        if (knownObservationIds.Count == 0)
        {
            return Reject("Technique specimen synthesis requires at least one grounded observation.");
        }

        using var document = TryParse(modelOutputJson, out var parseDiagnostics);
        if (document is null)
        {
            return new ReferenceCorpusTechniqueSpecimenValidationResult(
                ReferenceCorpusTechniqueSpecimenValidationStatuses.InvalidJson,
                null,
                parseDiagnostics);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return InvalidSchema("Technique specimen output must be a JSON object.");
        }

        if (HasUnexpectedProperties(root, AllowedRootProperties, out var unexpectedRoot))
        {
            return InvalidSchema($"Technique specimen output contains unsupported property '{unexpectedRoot}'.");
        }

        if (!HasStringValue(root, "schema_version", ReferenceCorpusTechniqueSpecimenSchemaVersions.V1))
        {
            return InvalidSchema($"Technique specimen output schema_version must be '{ReferenceCorpusTechniqueSpecimenSchemaVersions.V1}'.");
        }

        if (!HasStringValue(root, "source_node_id", expectedSourceNodeId))
        {
            return InvalidSchema("Technique specimen source_node_id must match the current source node.");
        }

        if (!TryReadBoundedString(root, "technique_family", MaxShortTextLength, out var techniqueFamily, out var error) ||
            !TryReadBoundedString(root, "technique_abstract", MaxLongTextLength, out var techniqueAbstract, out error) ||
            !TryReadBoundedString(root, "trigger_context", MaxLongTextLength, out var triggerContext, out error) ||
            !TryReadBoundedString(root, "transfer_template", MaxLongTextLength, out var transferTemplate, out error) ||
            !TryReadBoundedString(root, "effect_on_reader", MaxLongTextLength, out var effectOnReader, out error))
        {
            return InvalidSchema(error);
        }

        if (ContainsSourceTermLeak(sourceText, techniqueAbstract) ||
            ContainsSourceTermLeak(sourceText, transferTemplate))
        {
            return Reject("Technique specimen output contains a source term in an abstraction field.");
        }

        if (!TryReadStringArray(root, "applicability_conditions", 1, 8, out var applicabilityConditions, out error) ||
            !TryReadStringArray(root, "failure_modes", 1, 8, out var failureModes, out error) ||
            !TryReadStringArray(root, "anti_patterns", 1, 8, out var antiPatterns, out error))
        {
            return InvalidSchema(error);
        }

        if (!TryReadOptionalStringArray(root, "world_context_dependencies", 12, out var worldContextDependencies, out error))
        {
            return InvalidSchema(error);
        }

        if (!TryReadTransferSlots(root, out var transferSlotsJson, out error))
        {
            return InvalidSchema(error);
        }

        if (!TryReadWhyItWorks(root, knownObservationIds, out var whyItWorksJson, out var evidenceIds, out error))
        {
            return Reject(error);
        }

        if (!TryReadDouble(root, "confidence", out var confidence) ||
            confidence < 0 ||
            confidence > 0.95)
        {
            return InvalidSchema("Technique specimen confidence must be a number between 0 and 0.95.");
        }

        string? masteryNotes = null;
        if (root.TryGetProperty("mastery_notes", out var masteryNotesElement) &&
            masteryNotesElement.ValueKind != JsonValueKind.Null)
        {
            if (masteryNotesElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(masteryNotesElement.GetString()) ||
                masteryNotesElement.GetString()!.Length > MaxLongTextLength)
            {
                return InvalidSchema($"Technique specimen mastery_notes must be a non-empty string up to {MaxLongTextLength} characters.");
            }

            masteryNotes = masteryNotesElement.GetString();
        }

        var candidate = new ReferenceCorpusTechniqueSpecimenCandidate(
            SourceNodeId: expectedSourceNodeId,
            TechniqueFamily: techniqueFamily,
            TechniqueAbstract: techniqueAbstract,
            TriggerContext: triggerContext,
            TransferTemplate: transferTemplate,
            TransferSlotsJson: transferSlotsJson,
            EffectOnReader: effectOnReader,
            ApplicabilityConditionsJson: ToJsonArray(applicabilityConditions),
            FailureModesJson: ToJsonArray(failureModes),
            AntiPatternsJson: ToJsonArray(antiPatterns),
            WorldContextDependenciesJson: worldContextDependencies.Count == 0 ? null : ToJsonArray(worldContextDependencies),
            WhyItWorksJson: whyItWorksJson,
            Confidence: Math.Round(confidence, 4),
            MasteryNotes: masteryNotes,
            EvidenceObservationIds: evidenceIds);

        return new ReferenceCorpusTechniqueSpecimenValidationResult(
            ReferenceCorpusTechniqueSpecimenValidationStatuses.Passed,
            candidate,
            ["Technique specimen output accepted."]);
    }

    private static JsonDocument? TryParse(string modelOutputJson, out IReadOnlyList<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(modelOutputJson))
        {
            diagnostics = ["Technique specimen output must be valid JSON and must not be empty."];
            return null;
        }

        try
        {
            diagnostics = [];
            return JsonDocument.Parse(modelOutputJson);
        }
        catch (JsonException exception)
        {
            diagnostics = [$"Technique specimen output must be valid JSON. {exception.Message}"];
            return null;
        }
    }

    private static bool TryReadTransferSlots(JsonElement root, out string transferSlotsJson, out string error)
    {
        transferSlotsJson = string.Empty;
        error = string.Empty;
        if (!root.TryGetProperty("transfer_slots", out var slots) ||
            slots.ValueKind != JsonValueKind.Array ||
            slots.GetArrayLength() is < 1 or > 12)
        {
            error = "Technique specimen transfer_slots must contain 1 to 12 slot objects.";
            return false;
        }

        var normalized = new JsonArray();
        foreach (var slot in slots.EnumerateArray())
        {
            if (slot.ValueKind != JsonValueKind.Object)
            {
                error = "Technique specimen transfer slot must be an object.";
                return false;
            }

            if (HasUnexpectedProperties(slot, AllowedSlotProperties, out var unexpected))
            {
                error = $"Technique specimen transfer slot contains unsupported property '{unexpected}'.";
                return false;
            }

            if (!TryReadBoundedString(slot, "slot_name", MaxShortTextLength, out var slotName, out error) ||
                !TryReadBoundedString(slot, "purpose", MaxLongTextLength, out var purpose, out error) ||
                !TryReadBoundedString(slot, "constraints", MaxLongTextLength, out var constraints, out error))
            {
                return false;
            }

            normalized.Add(new JsonObject
            {
                ["slot_name"] = slotName,
                ["purpose"] = purpose,
                ["constraints"] = constraints
            });
        }

        transferSlotsJson = normalized.ToJsonString(JsonOptions);
        return true;
    }

    private static bool TryReadWhyItWorks(
        JsonElement root,
        HashSet<string> knownObservationIds,
        out string whyItWorksJson,
        out IReadOnlyList<string> evidenceIds,
        out string error)
    {
        whyItWorksJson = string.Empty;
        evidenceIds = [];
        error = string.Empty;
        if (!root.TryGetProperty("why_it_works", out var factors) ||
            factors.ValueKind != JsonValueKind.Array ||
            factors.GetArrayLength() is < 1 or > 10)
        {
            error = "Technique specimen why_it_works must contain 1 to 10 contributing factors.";
            return false;
        }

        var normalizedFactors = new JsonArray();
        var allEvidenceIds = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var factor in factors.EnumerateArray())
        {
            if (factor.ValueKind != JsonValueKind.Object)
            {
                error = "Technique specimen why_it_works factor must be an object.";
                return false;
            }

            if (HasUnexpectedProperties(factor, AllowedWhyFactorProperties, out var unexpected))
            {
                error = $"Technique specimen why_it_works factor contains unsupported property '{unexpected}'.";
                return false;
            }

            if (!TryReadBoundedString(factor, "factor", MaxLongTextLength, out var factorText, out error) ||
                !TryReadBoundedString(factor, "explanation", MaxLongTextLength, out var explanation, out error))
            {
                return false;
            }

            if (!factor.TryGetProperty("observation_ids", out var ids) ||
                ids.ValueKind != JsonValueKind.Array ||
                ids.GetArrayLength() == 0)
            {
                error = "Technique specimen why_it_works factor must reference at least one observation_id.";
                return false;
            }

            var normalizedIds = new JsonArray();
            foreach (var id in ids.EnumerateArray())
            {
                if (id.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(id.GetString()))
                {
                    error = "Technique specimen why_it_works observation_ids must be non-empty strings.";
                    return false;
                }

                var observationId = id.GetString()!;
                if (!knownObservationIds.Contains(observationId))
                {
                    error = "Technique specimen why_it_works factor references unknown observation_id.";
                    return false;
                }

                normalizedIds.Add(observationId);
                allEvidenceIds.Add(observationId);
            }

            normalizedFactors.Add(new JsonObject
            {
                ["factor"] = factorText,
                ["observation_ids"] = normalizedIds,
                ["explanation"] = explanation
            });
        }

        if (allEvidenceIds.Count == 0)
        {
            error = "Technique specimen why_it_works must reference real observation evidence.";
            return false;
        }

        evidenceIds = allEvidenceIds.ToArray();
        whyItWorksJson = new JsonObject
        {
            ["contributing_factors"] = normalizedFactors
        }.ToJsonString(JsonOptions);
        return true;
    }

    private static bool TryReadStringArray(
        JsonElement root,
        string propertyName,
        int minItems,
        int maxItems,
        out IReadOnlyList<string> values,
        out string error)
    {
        values = [];
        error = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() < minItems ||
            element.GetArrayLength() > maxItems)
        {
            error = $"Technique specimen {propertyName} must contain {minItems} to {maxItems} strings.";
            return false;
        }

        return TryBuildStringArray(element, propertyName, out values, out error);
    }

    private static bool TryReadOptionalStringArray(
        JsonElement root,
        string propertyName,
        int maxItems,
        out IReadOnlyList<string> values,
        out string error)
    {
        values = [];
        error = string.Empty;
        if (!root.TryGetProperty(propertyName, out var element) ||
            element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > maxItems)
        {
            error = $"Technique specimen {propertyName} must contain at most {maxItems} strings.";
            return false;
        }

        return TryBuildStringArray(element, propertyName, out values, out error);
    }

    private static bool TryBuildStringArray(
        JsonElement element,
        string propertyName,
        out IReadOnlyList<string> values,
        out string error)
    {
        var items = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()) ||
                item.GetString()!.Length > MaxLongTextLength)
            {
                values = [];
                error = $"Technique specimen {propertyName} entries must be non-empty strings up to {MaxLongTextLength} characters.";
                return false;
            }

            items.Add(item.GetString()!);
        }

        values = items;
        error = string.Empty;
        return true;
    }

    private static bool TryReadBoundedString(
        JsonElement element,
        string propertyName,
        int maxLength,
        out string value,
        out string error)
    {
        value = string.Empty;
        error = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            error = $"Technique specimen {propertyName} must be a non-empty string.";
            return false;
        }

        value = property.GetString()!;
        if (value.Any(char.IsControl) || value.Length > maxLength)
        {
            error = $"Technique specimen {propertyName} must be at most {maxLength} characters and contain no control characters.";
            return false;
        }

        return true;
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value) &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value);
    }

    private static bool HasStringValue(JsonElement element, string propertyName, string expected)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static bool HasUnexpectedProperties(JsonElement element, HashSet<string> allowed, out string unexpected)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                unexpected = property.Name;
                return true;
            }
        }

        unexpected = string.Empty;
        return false;
    }

    private static bool ContainsSourceTermLeak(string sourceText, string outputText)
    {
        if (string.IsNullOrWhiteSpace(sourceText) || string.IsNullOrWhiteSpace(outputText))
        {
            return false;
        }

        var normalizedOutput = NormalizeCjkAndAscii(outputText);
        if (normalizedOutput.Length == 0)
        {
            return false;
        }

        var normalizedSource = NormalizeCjkAndAscii(sourceText);
        if (normalizedSource.Length >= 8 && normalizedOutput.Contains(normalizedSource, StringComparison.Ordinal))
        {
            return true;
        }

        return ExtractSourceTerms(sourceText).Any(term => normalizedOutput.Contains(term, StringComparison.Ordinal));
    }

    private static IReadOnlySet<string> ExtractSourceTerms(string sourceText)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in ExtractCjkRuns(sourceText))
        {
            for (var length = 4; length <= Math.Min(8, run.Length); length++)
            {
                for (var start = 0; start + length <= run.Length; start++)
                {
                    AddSourceTerm(terms, run.Substring(start, length));
                }
            }

            for (var start = 0; start + 2 <= run.Length; start++)
            {
                var shortTerm = run.Substring(start, 2);
                if (IsLikelySpecificShortSourceTerm(shortTerm))
                {
                    AddSourceTerm(terms, shortTerm);
                }
            }
        }

        return terms;
    }

    private static void AddSourceTerm(HashSet<string> terms, string term)
    {
        if (!SourceTermStopList.Contains(term))
        {
            terms.Add(term);
        }
    }

    private static bool IsLikelySpecificShortSourceTerm(string term)
    {
        return term.Length == 2 &&
            !SourceTermStopList.Contains(term) &&
            term.All(IsCjk) &&
            !term.Any(GenericShortTermChars.Contains);
    }

    private static IReadOnlyList<string> ExtractCjkRuns(string value)
    {
        var runs = new List<string>();
        var buffer = new StringBuilder();
        foreach (var ch in value)
        {
            if (IsCjk(ch))
            {
                buffer.Append(ch);
                continue;
            }

            Flush();
        }

        Flush();
        return runs;

        void Flush()
        {
            if (buffer.Length > 0)
            {
                runs.Add(buffer.ToString());
                buffer.Clear();
            }
        }
    }

    private static string NormalizeCjkAndAscii(string value)
    {
        var buffer = new char[value.Length];
        var index = 0;
        foreach (var ch in value)
        {
            if (IsCjk(ch) || char.IsAsciiLetterOrDigit(ch))
            {
                buffer[index++] = char.ToLowerInvariant(ch);
            }
        }

        return new string(buffer, 0, index);
    }

    private static bool IsCjk(char ch)
    {
        return ch is >= '\u3400' and <= '\u9fff';
    }

    private static string ToJsonArray(IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(value);
        }

        return array.ToJsonString(JsonOptions);
    }

    private static ReferenceCorpusTechniqueSpecimenValidationResult InvalidSchema(string diagnostic)
    {
        return new ReferenceCorpusTechniqueSpecimenValidationResult(
            ReferenceCorpusTechniqueSpecimenValidationStatuses.InvalidSchema,
            null,
            [diagnostic]);
    }

    private static ReferenceCorpusTechniqueSpecimenValidationResult Reject(string diagnostic)
    {
        return new ReferenceCorpusTechniqueSpecimenValidationResult(
            ReferenceCorpusTechniqueSpecimenValidationStatuses.Rejected,
            null,
            [diagnostic]);
    }
}
