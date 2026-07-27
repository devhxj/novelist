namespace Novelist.Core.App;

public sealed record ReferenceMaterialSourceSpan(int StartLine, int EndLine);

public sealed record ReferenceMaterialEntity(string Name, string Kind);

public sealed record ReferenceMaterialSetting(
    string? Location,
    string? Time,
    string? Environment);

public sealed record ReferenceMaterialPerspective(string Mode, string? FocusEntity);

public sealed record ReferenceMaterialFact(string Content, string? Subject);

public sealed record ReferenceMaterialCausality(string? Cause, string? Consequence);

public sealed record ReferenceMaterialStateChange(string Subject, string Before, string After);

public sealed record ReferenceMaterialConflict(string? Pressure, string? Cost);

public sealed record ReferenceMaterialInformation(string? Role, string? Content);

public sealed record ReferenceMaterialEmotion(string? Tone, string? Subtext);

public sealed record ReferenceMaterialForeshadowing(string Phase, string Target);

public sealed record ReferenceMaterialMetadata(
    ReferenceMaterialSourceSpan SourceSpan,
    string SourceKind,
    IReadOnlyList<ReferenceMaterialEntity> Entities,
    ReferenceMaterialSetting? Setting,
    ReferenceMaterialPerspective? Perspective,
    string? Event,
    IReadOnlyList<ReferenceMaterialFact> Facts,
    ReferenceMaterialCausality? Causality,
    IReadOnlyList<ReferenceMaterialStateChange> StateChanges,
    string? CharacterDynamics,
    ReferenceMaterialConflict? Conflict,
    ReferenceMaterialInformation? Information,
    ReferenceMaterialEmotion? Emotion,
    IReadOnlyList<string> NarrativeFunctions,
    IReadOnlyList<ReferenceMaterialForeshadowing> Foreshadowing,
    IReadOnlyList<string> Motifs,
    IReadOnlyList<string> ExpressionTechniques,
    string ReuseHint);

public static class ReferenceMaterialMetadataValidator
{
    private static readonly HashSet<string> SourceKinds =
    [
        "对话", "动作", "叙述", "心理", "设定", "场景"
    ];

    private static readonly HashSet<string> EntityKinds =
    [
        "人物", "地点", "物件", "组织", "概念"
    ];

    private static readonly HashSet<string> PerspectiveModes =
    [
        "全知", "限知", "客观"
    ];

    private static readonly HashSet<string> EmotionalTones =
    [
        "紧张", "克制", "阴郁", "温柔", "惆怅", "紧迫", "激烈", "神秘", "幽默", "庄重",
        "希望", "敌意", "平静", "羞惭", "愤怒", "恐惧", "悲伤", "愉悦", "暧昧", "荒诞"
    ];

    private static readonly HashSet<string> NarrativeFunctionValues =
    [
        "人物塑造", "关系转变", "冲突升级", "压力积累", "信息揭示", "伏笔", "误导", "转折", "悬念",
        "世界观构建", "场景铺陈", "情绪释放", "钩子", "收束", "主题呼应", "节奏调整", "因果铺垫",
        "状态确认", "视角校准", "线索回收"
    ];

    private static readonly HashSet<string> InformationRoles =
    [
        "已确立", "隐藏", "伏笔", "误导", "揭示", "回收"
    ];

    private static readonly HashSet<string> ForeshadowingPhases =
    [
        "埋设", "强化", "回收"
    ];

    private static readonly HashSet<string> ExpressionTechniqueValues =
    [
        "动作替代解释", "对白留白", "环境烘托", "信息延迟", "反应对照", "感官描写", "象征意象",
        "节奏停顿", "内心独白", "反讽", "细节特写", "场景切换", "叙述省略", "对比映照", "重复回环"
    ];

    public static bool TryValidate(ReferenceMaterialMetadata? metadata, out string error)
    {
        if (metadata is null ||
            metadata.SourceSpan is null ||
            metadata.SourceSpan.StartLine <= 0 ||
            metadata.SourceSpan.EndLine < metadata.SourceSpan.StartLine)
        {
            error = "source_span must be a positive inclusive line range.";
            return false;
        }

        if (!SourceKinds.Contains(metadata.SourceKind)) return Invalid("source_kind must use the configured Chinese taxonomy", out error);

        if (!ValidateEntities(metadata.Entities)) return Invalid("entities must be an array of distinct non-empty names with configured Chinese kinds", out error);
        if (!ValidateSetting(metadata.Setting)) return Invalid("setting must be null or contain at least one non-empty location, time, or environment", out error);
        if (!ValidatePerspective(metadata.Perspective)) return Invalid("perspective must use 全知、限知 or 客观; only 限知 requires a non-empty focus_entity", out error);
        if (!IsOptionalText(metadata.Event)) return Invalid("event must be null or non-empty trimmed text", out error);
        if (!ValidateFacts(metadata.Facts)) return Invalid("facts must contain distinct non-empty content values", out error);
        if (!ValidateCausality(metadata.Causality)) return Invalid("causality must be null or contain a non-empty cause or consequence", out error);
        if (!ValidateStateChanges(metadata.StateChanges)) return Invalid("state_changes must use non-empty subject, before, and after values that differ", out error);
        if (!IsOptionalText(metadata.CharacterDynamics)) return Invalid("character_dynamics must be null or non-empty trimmed text", out error);
        if (!ValidateConflict(metadata.Conflict)) return Invalid("conflict must be null or contain a non-empty pressure or cost", out error);
        if (!ValidateInformation(metadata.Information)) return Invalid("information must be null or contain a configured role or non-empty content", out error);
        if (!ValidateEmotion(metadata.Emotion)) return Invalid("emotion must be null or contain a configured tone or non-empty subtext", out error);
        if (!TryValidateDistinctEnumValues(
                metadata.NarrativeFunctions,
                "narrative_functions",
                NarrativeFunctionValues,
                "人物塑造、关系转变、冲突升级、压力积累、信息揭示、伏笔、误导、转折、悬念、世界观构建、场景铺陈、情绪释放、钩子、收束、主题呼应、节奏调整、因果铺垫、状态确认、视角校准、线索回收",
                out error)) return false;
        if (!ValidateForeshadowing(metadata.Foreshadowing)) return Invalid("foreshadowing must use configured phases and non-empty targets", out error);
        if (!ValidateTextSet(metadata.Motifs)) return Invalid("motifs must use distinct non-empty values", out error);
        if (!TryValidateDistinctEnumValues(
                metadata.ExpressionTechniques,
                "expression_techniques",
                ExpressionTechniqueValues,
                "动作替代解释、对白留白、环境烘托、信息延迟、反应对照、感官描写、象征意象、节奏停顿、内心独白、反讽、细节特写、场景切换、叙述省略、对比映照、重复回环",
                out error)) return false;
        if (!IsRequiredText(metadata.ReuseHint)) return Invalid("reuse_hint must be non-empty trimmed text", out error);

        error = string.Empty;
        return true;
    }

    private static bool Invalid(string reason, out string error)
    {
        error = reason + ".";
        return false;
    }

    private static bool ValidateEntities(IReadOnlyList<ReferenceMaterialEntity>? values) =>
        values is not null &&
        values.All(value => value is not null && IsRequiredText(value.Name) && EntityKinds.Contains(value.Kind)) &&
        values.Select(value => $"{value.Kind}\u001f{value.Name}").Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool ValidateSetting(ReferenceMaterialSetting? value) =>
        value is null ||
        (IsOptionalText(value.Location) && IsOptionalText(value.Time) && IsOptionalText(value.Environment) &&
         (value.Location is not null || value.Time is not null || value.Environment is not null));

    private static bool ValidatePerspective(ReferenceMaterialPerspective? value) =>
        value is null ||
        (PerspectiveModes.Contains(value.Mode) && IsOptionalText(value.FocusEntity) &&
         (value.Mode == "限知" ? value.FocusEntity is not null : value.FocusEntity is null));

    private static bool ValidateFacts(IReadOnlyList<ReferenceMaterialFact>? values) =>
        values is not null &&
        values.All(value => value is not null && IsRequiredText(value.Content) && IsOptionalText(value.Subject)) &&
        values.Select(value => value.Content).Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool ValidateCausality(ReferenceMaterialCausality? value) =>
        value is null ||
        (IsOptionalText(value.Cause) && IsOptionalText(value.Consequence) &&
         (value.Cause is not null || value.Consequence is not null));

    private static bool ValidateStateChanges(IReadOnlyList<ReferenceMaterialStateChange>? values) =>
        values is not null &&
        values.All(value => value is not null && IsRequiredText(value.Subject) && IsRequiredText(value.Before) && IsRequiredText(value.After) &&
                            !string.Equals(value.Before, value.After, StringComparison.Ordinal));

    private static bool ValidateConflict(ReferenceMaterialConflict? value) =>
        value is null ||
        (IsOptionalText(value.Pressure) && IsOptionalText(value.Cost) &&
         (value.Pressure is not null || value.Cost is not null));

    private static bool ValidateInformation(ReferenceMaterialInformation? value) =>
        value is null ||
        ((value.Role is null || InformationRoles.Contains(value.Role)) && IsOptionalText(value.Content) &&
         (value.Role is not null || value.Content is not null));

    private static bool ValidateEmotion(ReferenceMaterialEmotion? value) =>
        value is null ||
        ((value.Tone is null || EmotionalTones.Contains(value.Tone)) && IsOptionalText(value.Subtext) &&
         (value.Tone is not null || value.Subtext is not null));

    private static bool ValidateForeshadowing(IReadOnlyList<ReferenceMaterialForeshadowing>? values) =>
        values is not null &&
        values.All(value => value is not null && ForeshadowingPhases.Contains(value.Phase) && IsRequiredText(value.Target));

    private static bool ValidateTextSet(IReadOnlyList<string>? values) =>
        values is not null &&
        values.All(IsRequiredText) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Count;

    private static bool TryValidateDistinctEnumValues(
        IReadOnlyList<string>? values,
        string field,
        HashSet<string> allowedValues,
        string allowedValuesDescription,
        out string error)
    {
        if (values is null)
        {
            error = $"{field} must be an array.";
            return false;
        }

        var firstIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (value is null || !allowedValues.Contains(value))
            {
                error = $"{field}[{index}] has unsupported value {FormatDiagnosticValue(value)}; allowed values are: {allowedValuesDescription}.";
                return false;
            }

            if (!firstIndexes.TryAdd(value, index))
            {
                error = $"{field}[{index}] duplicates {field}[{firstIndexes[value]}].";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static string FormatDiagnosticValue(string? value)
    {
        if (value is null)
        {
            return "null";
        }

        const int maxLength = 128;
        var formatted = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
        if (formatted.Length > maxLength)
        {
            formatted = formatted[..maxLength] + "...";
        }

        return $"\"{formatted}\"";
    }

    private static bool IsOptionalText(string? value) => value is null || IsRequiredText(value);

    private static bool IsRequiredText(string? value) =>
        value is not null &&
        value.Length > 0 &&
        !string.IsNullOrWhiteSpace(value) &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Contains('\0');
}
