using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Novelist.Core.App;

public static class ReferenceCorpusFeatureFamilySchemaVersions
{
    public const string V1 = "reference-corpus-feature-family-v1";
}

public static class ReferenceCorpusFeatureFamilies
{
    public const string Syntax = "syntax";
    public const string Rhythm = "rhythm";
    public const string Sensory = "sensory";
    public const string Emotion = "emotion";
    public const string Rhetoric = "rhetoric";
    public const string Narrative = "narrative";
    public const string Pov = "pov";
    public const string Action = "action";
    public const string Character = "character";
    public const string Commercial = "commercial";

    public static IReadOnlyList<string> SentenceFamilies { get; } =
    [
        Syntax,
        Rhythm,
        Sensory,
        Emotion,
        Rhetoric
    ];

    public static IReadOnlyList<string> PassageFamilies { get; } =
    [
        Narrative,
        Pov,
        Action,
        Character,
        Commercial
    ];

    public static IReadOnlyList<string> All { get; } =
    [
        Syntax,
        Rhythm,
        Sensory,
        Emotion,
        Rhetoric,
        Narrative,
        Pov,
        Action,
        Character,
        Commercial
    ];
}

public static class ReferenceCorpusFeatureFamilyValidationStatuses
{
    public const string Passed = "passed";
    public const string Partial = "partial";
    public const string Rejected = "rejected";
    public const string InvalidJson = "invalid_json";
    public const string InvalidSchema = "invalid_schema";
}

public static class ReferenceCorpusFeatureObservationReviewStates
{
    public const string Unverified = "unverified";
    public const string LowConfidence = "low_confidence";
}

public sealed record ReferenceCorpusFeatureFamilySchema(
    string SchemaId,
    string SchemaVersion,
    string Family,
    string NodeType,
    int MaxObservations,
    IReadOnlyList<string> RequiredObservationFields,
    IReadOnlyDictionary<string, ReferenceCorpusFeatureFieldSchema> ObservationFields);

public sealed record ReferenceCorpusFeatureFieldSchema(
    string Type,
    IReadOnlyList<string> Enum,
    double? Minimum,
    double? Maximum,
    int? MaxLength);

public sealed record ReferenceCorpusFeatureObservationCandidate(
    string FeatureFamily,
    string FeatureKey,
    string ValueKind,
    string? ValueText,
    double? ValueNum,
    bool? ValueBool,
    string? ValueJson,
    double? Intensity,
    double Confidence,
    int EvidenceStart,
    int EvidenceEnd,
    string Explanation);

public sealed record ReferenceCorpusFeatureObservationRejectedItem(
    int Index,
    string FeatureKey,
    string Reason);

public sealed record ReferenceCorpusFeatureFamilyValidationResult(
    string Status,
    string Family,
    string NodeType,
    IReadOnlyList<ReferenceCorpusFeatureObservationCandidate> AcceptedObservations,
    IReadOnlyList<ReferenceCorpusFeatureObservationRejectedItem> RejectedObservations,
    IReadOnlyList<string> Diagnostics);

public static class ReferenceCorpusFeatureFamilySchemaRegistry
{
    private static readonly Lazy<IReadOnlyDictionary<string, ReferenceCorpusFeatureFamilySchema>> Schemas =
        new(LoadSchemas);

    public static IReadOnlyDictionary<string, ReferenceCorpusFeatureFamilySchema> All => Schemas.Value;

    public static ReferenceCorpusFeatureFamilySchema Get(string family)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            throw new ArgumentException("Feature family is required.", nameof(family));
        }

        return Schemas.Value.TryGetValue(family, out var schema)
            ? schema
            : throw new ArgumentException($"Unsupported feature family '{family}'.", nameof(family));
    }

    private static IReadOnlyDictionary<string, ReferenceCorpusFeatureFamilySchema> LoadSchemas()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resources = assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".ReferenceCorpusFeatureSchemas.", StringComparison.Ordinal) &&
                name.EndsWith(".json", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var result = new Dictionary<string, ReferenceCorpusFeatureFamilySchema>(StringComparer.Ordinal);
        foreach (var resource in resources)
        {
            using var stream = assembly.GetManifestResourceStream(resource) ??
                throw new InvalidOperationException($"Missing embedded feature schema resource '{resource}'.");
            using var document = JsonDocument.Parse(stream);
            var schema = ParseSchema(document.RootElement, resource);
            if (!ReferenceCorpusFeatureFamilies.All.Contains(schema.Family, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"Feature schema '{resource}' declares unsupported family '{schema.Family}'.");
            }

            if (!result.TryAdd(schema.Family, schema))
            {
                throw new InvalidOperationException($"Duplicate feature schema for family '{schema.Family}'.");
            }
        }

        var missing = ReferenceCorpusFeatureFamilies.All
            .Where(family => !result.ContainsKey(family))
            .ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"Missing feature family schemas: {string.Join(", ", missing)}.");
        }

        return result;
    }

    private static ReferenceCorpusFeatureFamilySchema ParseSchema(JsonElement root, string resourceName)
    {
        var fields = new Dictionary<string, ReferenceCorpusFeatureFieldSchema>(StringComparer.Ordinal);
        foreach (var property in root.GetProperty("observation_fields").EnumerateObject())
        {
            fields[property.Name] = ParseField(property.Value);
        }

        return new ReferenceCorpusFeatureFamilySchema(
            ReadRequiredString(root, "schema_id", resourceName),
            ReadRequiredString(root, "schema_version", resourceName),
            ReadRequiredString(root, "family", resourceName),
            ReadRequiredString(root, "node_type", resourceName),
            root.GetProperty("max_observations").GetInt32(),
            root.GetProperty("required_observation_fields")
                .EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty)
                .Where(value => value.Length > 0)
                .ToArray(),
            fields);
    }

    private static ReferenceCorpusFeatureFieldSchema ParseField(JsonElement element)
    {
        return new ReferenceCorpusFeatureFieldSchema(
            ReadRequiredString(element, "type", "feature field"),
            element.TryGetProperty("enum", out var enumElement)
                ? enumElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(value => value.Length > 0).ToArray()
                : [],
            element.TryGetProperty("minimum", out var minElement) ? minElement.GetDouble() : null,
            element.TryGetProperty("maximum", out var maxElement) ? maxElement.GetDouble() : null,
            element.TryGetProperty("max_length", out var maxLengthElement) ? maxLengthElement.GetInt32() : null);
    }

    private static string ReadRequiredString(JsonElement element, string propertyName, string source)
    {
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException($"Feature schema '{source}' must declare string property '{propertyName}'.");
        }

        return property.GetString()!;
    }
}

public static class ReferenceCorpusFeatureFamilyOutputValidator
{
    private const int MaxExplanationLength = 360;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly HashSet<string> AllowedRootProperties = new(StringComparer.Ordinal)
    {
        "schema_version",
        "family",
        "node_type",
        "observations"
    };

    public static ReferenceCorpusFeatureFamilyValidationResult Validate(
        string modelOutputJson,
        string expectedFamily,
        string expectedNodeType,
        int sourceTextLength)
    {
        if (sourceTextLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceTextLength), sourceTextLength, "Source text length cannot be negative.");
        }

        var schema = ReferenceCorpusFeatureFamilySchemaRegistry.Get(expectedFamily);
        if (!string.Equals(schema.NodeType, expectedNodeType, StringComparison.Ordinal))
        {
            return InvalidSchema(expectedFamily, expectedNodeType, $"Family '{expectedFamily}' expects node_type '{schema.NodeType}', got '{expectedNodeType}'.");
        }

        using var document = TryParse(modelOutputJson, out var parseDiagnostics);
        if (document is null)
        {
            return new ReferenceCorpusFeatureFamilyValidationResult(
                ReferenceCorpusFeatureFamilyValidationStatuses.InvalidJson,
                expectedFamily,
                expectedNodeType,
                [],
                [],
                parseDiagnostics);
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return InvalidSchema(expectedFamily, expectedNodeType, "Model output must be a JSON object.");
        }

        if (HasUnexpectedProperties(root, AllowedRootProperties, out var unexpectedRoot))
        {
            return InvalidSchema(expectedFamily, expectedNodeType, $"Model output contains unsupported property '{unexpectedRoot}'.");
        }

        if (!root.TryGetProperty("schema_version", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.String ||
            schemaVersion.GetString() != schema.SchemaVersion ||
            schema.SchemaVersion != ReferenceCorpusFeatureFamilySchemaVersions.V1)
        {
            return InvalidSchema(expectedFamily, expectedNodeType, $"Model output schema_version must be '{ReferenceCorpusFeatureFamilySchemaVersions.V1}'.");
        }

        if (!HasStringValue(root, "family", schema.Family))
        {
            return InvalidSchema(expectedFamily, expectedNodeType, $"Model output family must be '{schema.Family}'.");
        }

        if (!HasStringValue(root, "node_type", schema.NodeType))
        {
            return InvalidSchema(expectedFamily, expectedNodeType, $"Model output node_type must be '{schema.NodeType}'.");
        }

        if (!root.TryGetProperty("observations", out var observations) ||
            observations.ValueKind != JsonValueKind.Array)
        {
            return InvalidSchema(expectedFamily, expectedNodeType, "Model output observations must be an array.");
        }

        if (observations.GetArrayLength() > schema.MaxObservations)
        {
            return InvalidSchema(expectedFamily, expectedNodeType, $"Model output observations must contain at most {schema.MaxObservations} items.");
        }

        var acceptedItems = new List<ReferenceCorpusFeatureObservationCandidate>();
        var rejected = new List<ReferenceCorpusFeatureObservationRejectedItem>();
        var index = 0;
        foreach (var observation in observations.EnumerateArray())
        {
            if (!TryBuildObservation(schema, observation, index, sourceTextLength, out var candidate, out var rejectedItem))
            {
                rejected.Add(rejectedItem);
            }
            else
            {
                acceptedItems.Add(candidate);
            }

            index++;
        }

        var accepted = NormalizeAcceptedObservations(schema, acceptedItems);
        var status = accepted.Count switch
        {
            > 0 when rejected.Count == 0 => ReferenceCorpusFeatureFamilyValidationStatuses.Passed,
            > 0 => ReferenceCorpusFeatureFamilyValidationStatuses.Partial,
            _ when rejected.Count == 0 => ReferenceCorpusFeatureFamilyValidationStatuses.Passed,
            _ => ReferenceCorpusFeatureFamilyValidationStatuses.Rejected
        };
        var diagnostics = accepted.Count == 0 && rejected.Count == 0
            ? ["Feature family output accepted with no grounded observations."]
            : status == ReferenceCorpusFeatureFamilyValidationStatuses.Passed
            ? ["Feature family output accepted."]
            : rejected.Select(item => $"Rejected observation {item.Index}: {item.Reason}").ToArray();

        return new ReferenceCorpusFeatureFamilyValidationResult(
            status,
            schema.Family,
            schema.NodeType,
            accepted,
            rejected,
            diagnostics);
    }

    private static bool TryBuildObservation(
        ReferenceCorpusFeatureFamilySchema schema,
        JsonElement observation,
        int index,
        int sourceTextLength,
        out ReferenceCorpusFeatureObservationCandidate candidate,
        out ReferenceCorpusFeatureObservationRejectedItem rejected)
    {
        candidate = null!;
        rejected = null!;
        if (observation.ValueKind != JsonValueKind.Object)
        {
            rejected = Reject(index, string.Empty, "Observation must be an object.");
            return false;
        }

        if (HasUnexpectedProperties(observation, schema.ObservationFields.Keys.ToHashSet(StringComparer.Ordinal), out var unexpected))
        {
            rejected = Reject(index, ReadOptionalString(observation, "feature_key"), $"Observation contains unsupported property '{unexpected}'.");
            return false;
        }

        foreach (var required in schema.RequiredObservationFields)
        {
            if (!observation.TryGetProperty(required, out var value) || value.ValueKind == JsonValueKind.Null)
            {
                rejected = Reject(index, ReadOptionalString(observation, "feature_key"), $"Observation requires property '{required}'.");
                return false;
            }
        }

        foreach (var field in schema.ObservationFields)
        {
            if (!observation.TryGetProperty(field.Key, out var value))
            {
                continue;
            }

            if (!ValidateField(field.Key, value, field.Value, out var reason))
            {
                rejected = Reject(index, ReadOptionalString(observation, "feature_key"), reason);
                return false;
            }
        }

        var featureKey = ReadOptionalString(observation, "feature_key");
        if (string.IsNullOrWhiteSpace(featureKey))
        {
            rejected = Reject(index, string.Empty, "Observation feature_key is required.");
            return false;
        }

        if (!TryReadInt(observation, "evidence_start", out var evidenceStart) ||
            !TryReadInt(observation, "evidence_end", out var evidenceEnd) ||
            evidenceStart < 0 ||
            evidenceEnd <= evidenceStart ||
            evidenceEnd > sourceTextLength)
        {
            rejected = Reject(index, featureKey, "Evidence offsets must be inside the source node and strictly increasing.");
            return false;
        }

        if (!TryReadDouble(observation, "confidence", out var confidence) ||
            confidence < 0 ||
            confidence > 0.95)
        {
            rejected = Reject(index, featureKey, "Confidence must be a number between 0 and 0.95.");
            return false;
        }

        var explanation = ReadOptionalString(observation, "explanation");
        if (string.IsNullOrWhiteSpace(explanation) || explanation.Length > MaxExplanationLength)
        {
            rejected = Reject(index, featureKey, $"Explanation is required and must be at most {MaxExplanationLength} characters.");
            return false;
        }

        double? intensity = TryReadDouble(observation, "intensity", out var intensityValue)
            ? intensityValue
            : null;
        var valueNum = SelectValueNum(schema.Family, featureKey, observation);
        bool? valueBool = TryReadBool(observation, "has_dialogue", out var hasDialogue)
            ? hasDialogue
            : null;
        var valueText = SelectValueText(observation, featureKey);
        var valueJson = NormalizeObservationJson(observation, schema);

        candidate = new ReferenceCorpusFeatureObservationCandidate(
            schema.Family,
            featureKey,
            InferValueKind(schema.Family, featureKey, valueText, valueNum, valueBool, valueJson),
            valueText,
            valueNum,
            valueBool,
            valueJson,
            intensity,
            Math.Round(confidence, 4),
            evidenceStart,
            evidenceEnd,
            explanation);
        return true;
    }

    private static IReadOnlyList<ReferenceCorpusFeatureObservationCandidate> NormalizeAcceptedObservations(
        ReferenceCorpusFeatureFamilySchema schema,
        IReadOnlyList<ReferenceCorpusFeatureObservationCandidate> accepted)
    {
        if (accepted.Count == 0 ||
            schema.Family is not (ReferenceCorpusFeatureFamilies.Sensory or ReferenceCorpusFeatureFamilies.Rhetoric))
        {
            return accepted;
        }

        var items = new JsonArray();
        foreach (var observation in accepted)
        {
            items.Add(JsonNode.Parse(observation.ValueJson ?? "{}"));
        }

        var featureKey = schema.Family == ReferenceCorpusFeatureFamilies.Sensory ? "senses" : "devices";
        var valueText = string.Join(
            ',',
            accepted
                .Select(item => item.ValueText)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal));
        var maxIntensity = accepted
            .Select(item => item.Intensity)
            .Where(value => value is not null)
            .DefaultIfEmpty(null)
            .Max();
        var maxValueNum = accepted
            .Select(item => item.ValueNum)
            .Where(value => value is not null)
            .DefaultIfEmpty(null)
            .Max();
        var explanation = string.Join(
            "；",
            accepted.Select(item => item.Explanation).Where(value => !string.IsNullOrWhiteSpace(value)));
        if (explanation.Length > MaxExplanationLength)
        {
            explanation = explanation[..MaxExplanationLength];
        }

        return
        [
            new ReferenceCorpusFeatureObservationCandidate(
                schema.Family,
                featureKey,
                "array",
                valueText,
                maxValueNum,
                null,
                items.ToJsonString(JsonOptions),
                maxIntensity,
                Math.Round(accepted.Min(item => item.Confidence), 4),
                accepted.Min(item => item.EvidenceStart),
                accepted.Max(item => item.EvidenceEnd),
                explanation)
        ];
    }

    private static bool ValidateField(
        string fieldName,
        JsonElement value,
        ReferenceCorpusFeatureFieldSchema field,
        out string reason)
    {
        reason = string.Empty;
        switch (field.Type)
        {
            case "string":
            case "enum":
                if (value.ValueKind != JsonValueKind.String)
                {
                    reason = $"Field '{fieldName}' must be a string.";
                    return false;
                }

                var text = value.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
                {
                    reason = $"Field '{fieldName}' must be a non-empty string without control characters.";
                    return false;
                }

                if (field.MaxLength is { } maxLength && text.Length > maxLength)
                {
                    reason = $"Field '{fieldName}' must be at most {maxLength} characters.";
                    return false;
                }

                if (field.Enum.Count > 0 && !field.Enum.Contains(text, StringComparer.Ordinal))
                {
                    reason = $"Field '{fieldName}' contains unsupported enum value.";
                    return false;
                }

                return true;
            case "number":
                if (!TryReadDouble(value, out var number))
                {
                    reason = $"Field '{fieldName}' must be a number.";
                    return false;
                }

                if ((field.Minimum is { } min && number < min) ||
                    (field.Maximum is { } max && number > max))
                {
                    reason = $"Field '{fieldName}' must be between {field.Minimum?.ToString() ?? "-inf"} and {field.Maximum?.ToString() ?? "+inf"}.";
                    return false;
                }

                return true;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var integer))
                {
                    reason = $"Field '{fieldName}' must be an integer.";
                    return false;
                }

                if ((field.Minimum is { } intMin && integer < intMin) ||
                    (field.Maximum is { } intMax && integer > intMax))
                {
                    reason = $"Field '{fieldName}' must be inside the allowed numeric range.";
                    return false;
                }

                return true;
            case "boolean":
                if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    return true;
                }

                reason = $"Field '{fieldName}' must be a boolean.";
                return false;
            default:
                reason = $"Field '{fieldName}' uses unsupported schema type '{field.Type}'.";
                return false;
        }
    }

    private static JsonDocument? TryParse(string modelOutputJson, out IReadOnlyList<string> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(modelOutputJson))
        {
            diagnostics = ["Model output must be valid JSON and must not be empty."];
            return null;
        }

        try
        {
            diagnostics = [];
            return JsonDocument.Parse(modelOutputJson);
        }
        catch (JsonException exception)
        {
            diagnostics = [$"Model output must be valid JSON. {exception.Message}"];
            return null;
        }
    }

    private static ReferenceCorpusFeatureFamilyValidationResult InvalidSchema(
        string family,
        string nodeType,
        string diagnostic)
    {
        return new ReferenceCorpusFeatureFamilyValidationResult(
            ReferenceCorpusFeatureFamilyValidationStatuses.InvalidSchema,
            family,
            nodeType,
            [],
            [],
            [diagnostic]);
    }

    private static ReferenceCorpusFeatureObservationRejectedItem Reject(int index, string? featureKey, string reason)
    {
        return new ReferenceCorpusFeatureObservationRejectedItem(index, featureKey ?? string.Empty, reason);
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

    private static bool HasStringValue(JsonElement element, string propertyName, string expected)
    {
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            string.Equals(value.GetString(), expected, StringComparison.Ordinal);
    }

    private static string ReadOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static bool TryReadInt(JsonElement element, string propertyName, out int result)
    {
        result = 0;
        return element.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt32(out result);
    }

    private static bool TryReadDouble(JsonElement element, string propertyName, out double result)
    {
        result = 0;
        return element.TryGetProperty(propertyName, out var value) && TryReadDouble(value, out result);
    }

    private static bool TryReadDouble(JsonElement value, out double result)
    {
        result = 0;
        return value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out result) &&
            !double.IsNaN(result) &&
            !double.IsInfinity(result);
    }

    private static bool TryReadBool(JsonElement element, string propertyName, out bool result)
    {
        result = false;
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            result = true;
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            result = false;
            return true;
        }

        return false;
    }

    private static string InferValueKind(
        string family,
        string featureKey,
        string? valueText,
        double? valueNum,
        bool? valueBool,
        string? valueJson)
    {
        if (family is ReferenceCorpusFeatureFamilies.Sensory or ReferenceCorpusFeatureFamilies.Rhetoric)
        {
            return "array";
        }

        if (valueBool is not null)
        {
            return "bool";
        }

        if (valueNum is not null && IsNumericPrimaryValue(family, featureKey))
        {
            return "number";
        }

        if (!string.IsNullOrWhiteSpace(valueText))
        {
            return "enum";
        }

        return "object";
    }

    private static bool IsNumericPrimaryValue(string family, string featureKey)
    {
        return (family, featureKey) switch
        {
            (ReferenceCorpusFeatureFamilies.Rhythm, "length_band") => true,
            (ReferenceCorpusFeatureFamilies.Rhythm, "pause_density") => true,
            _ => false
        };
    }

    private static double? SelectValueNum(string family, string featureKey, JsonElement observation)
    {
        return (family, featureKey) switch
        {
            (ReferenceCorpusFeatureFamilies.Sensory, _) => TryReadDouble(observation, "intensity", out var sensoryIntensity) ? sensoryIntensity : null,
            (ReferenceCorpusFeatureFamilies.Emotion, _) => TryReadDouble(observation, "intensity", out var emotionIntensity) ? emotionIntensity : null,
            (ReferenceCorpusFeatureFamilies.Rhythm, "length_band") => TryReadDouble(observation, "char_count", out var charCount) ? charCount : null,
            (ReferenceCorpusFeatureFamilies.Rhythm, "pause_density") => TryReadDouble(observation, "pause_density", out var pauseDensity) ? pauseDensity : null,
            _ => null
        };
    }

    private static string SelectValueText(JsonElement observation, string featureKey)
    {
        foreach (var propertyName in new[]
        {
            "label",
            "sense",
            "type",
            "function",
            "mode",
            "pov_type",
            "granularity",
            "power_position",
            "hook_type",
            "cadence",
            "surface"
        })
        {
            var value = ReadOptionalString(observation, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return featureKey;
    }

    private static string NormalizeObservationJson(JsonElement observation, ReferenceCorpusFeatureFamilySchema schema)
    {
        var root = new JsonObject();
        foreach (var fieldName in schema.ObservationFields.Keys)
        {
            if (!observation.TryGetProperty(fieldName, out var value))
            {
                continue;
            }

            root[fieldName] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
                JsonValueKind.Number when value.TryGetDouble(out var number) => Math.Round(number, 6),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => JsonNode.Parse(value.GetRawText())
            };
        }

        return root.ToJsonString(JsonOptions);
    }
}
