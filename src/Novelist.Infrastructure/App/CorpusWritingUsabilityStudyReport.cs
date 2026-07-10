using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Novelist.Infrastructure.App;

internal static class CorpusWritingUsabilityStudyReport
{
    public const string FixtureSchemaVersion = "corpus-writing-usability-fixtures-v1";
    public const string ReportSchemaVersion = "corpus-writing-usability-report-v1";
    public const long MaximumFixtureBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex SafeIdentifierPattern = new(
        "^[a-z0-9][a-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Sha256Pattern = new(
        "^sha256:[a-f0-9]{64}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly IReadOnlyList<string> CoreTaskIds =
    [
        "import_start_analysis",
        "leave_resume_analysis",
        "target_to_blueprint",
        "feedback_select_prose",
        "blocked_recover_insert"
    ];

    private static readonly ISet<string> FailureCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_action_not_found",
        "background_job_not_found",
        "task_status_not_understood",
        "resume_action_not_found",
        "goal_input_unclear",
        "blueprint_choice_unclear",
        "feedback_action_not_found",
        "prose_choice_unclear",
        "transition_blocked",
        "blocked_state_not_understood",
        "recovery_action_not_found",
        "insert_action_not_found",
        "environment_interrupted",
        "other_observed"
    };

    private static readonly ISet<string> RecoveryActionCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "start_analysis",
        "open_background_jobs",
        "resume_analysis",
        "retry_analysis",
        "generate_blueprint",
        "revise_blueprint",
        "choose_alternative_blueprint",
        "generate_prose",
        "choose_alternative_prose",
        "return_to_blueprint",
        "insert_at_cursor",
        "append_to_chapter",
        "restart_task",
        "other_observed"
    };

    private static readonly ISet<string> RootProperties = PropertySet(
        "schema_version", "study_id", "study_revision", "study_kind", "participants");
    private static readonly ISet<string> ParticipantProperties = PropertySet(
        "participant_id_hash", "tasks");
    private static readonly ISet<string> TaskProperties = PropertySet(
        "task_id", "outcome", "completed_without_prompt", "duration_seconds", "backtrack_count",
        "first_failure_code", "recovery_action_code", "difficulty");

    public static async Task<CorpusWritingUsabilityStudyReportResult> EvaluateFileAsync(
        string fixturePath,
        string outputDirectory,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

        var fixtureInfo = new FileInfo(fixturePath);
        if (!fixtureInfo.Exists)
        {
            throw new FileNotFoundException("Usability study fixture does not exist.");
        }

        if (fixtureInfo.Length > MaximumFixtureBytes)
        {
            throw new InvalidDataException($"Usability study fixture exceeds the {MaximumFixtureBytes} byte maximum.");
        }

        CorpusWritingUsabilityStudyFixture fixture;
        try
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath, cancellationToken));
            ValidateJsonShape(document.RootElement);
            fixture = JsonSerializer.Deserialize<CorpusWritingUsabilityStudyFixture>(
                document.RootElement.GetRawText(),
                JsonOptions)
                ?? throw new InvalidDataException("Usability study fixture is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Usability study fixture is not valid JSON.", exception);
        }

        ValidateFixture(fixture);
        var report = BuildReport(fixture, generatedAt.ToUniversalTime());
        Directory.CreateDirectory(outputDirectory);
        await WriteAtomicallyAsync(
            Path.Combine(outputDirectory, "corpus-writing-usability-report.json"),
            JsonSerializer.Serialize(report, JsonOptions),
            cancellationToken);
        await WriteAtomicallyAsync(
            Path.Combine(outputDirectory, "corpus-writing-usability-report.md"),
            BuildMarkdown(report),
            cancellationToken);
        return report;
    }

    private static CorpusWritingUsabilityStudyReportResult BuildReport(
        CorpusWritingUsabilityStudyFixture fixture,
        DateTimeOffset generatedAt)
    {
        var participants = fixture.Participants!;
        var fullPathCompletions = participants.Count(CompletesAutomaticPathWithoutPrompt);
        var taskReports = CoreTaskIds
            .Select(taskId => BuildTaskReport(taskId, participants.SelectMany(item => item.Tasks!).Where(item => item.TaskId == taskId).ToArray()))
            .ToArray();

        return new CorpusWritingUsabilityStudyReportResult(
            ReportSchemaVersion,
            fixture.StudyId!,
            fixture.StudyRevision!,
            fixture.StudyKind!,
            generatedAt,
            participants.Count,
            fullPathCompletions,
            Round(fullPathCompletions / (double)participants.Count),
            fixture.StudyKind == "human" && fullPathCompletions >= 4,
            taskReports);
    }

    private static CorpusWritingUsabilityTaskReport BuildTaskReport(
        string taskId,
        IReadOnlyList<CorpusWritingUsabilityTask> tasks)
    {
        var completionCount = tasks.Count(item => item.Outcome == "completed");
        var unpromptedCount = tasks.Count(item => item.Outcome == "completed" && item.CompletedWithoutPrompt);
        var failureCounts = BuildCodeCounts(tasks.Select(item => item.FirstFailureCode));
        var recoveryActionCounts = BuildCodeCounts(tasks.Select(item => item.RecoveryActionCode));

        return new CorpusWritingUsabilityTaskReport(
            taskId,
            completionCount,
            Round(completionCount / (double)tasks.Count),
            unpromptedCount,
            Round(unpromptedCount / (double)tasks.Count),
            Round(tasks.Average(item => item.DurationSeconds)),
            Round(tasks.Average(item => item.BacktrackCount)),
            Round(tasks.Average(item => item.Difficulty)),
            failureCounts,
            recoveryActionCounts);
    }

    private static IReadOnlyList<CorpusWritingUsabilityCodeCount> BuildCodeCounts(IEnumerable<string?> codes) =>
        codes
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .GroupBy(code => code!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new CorpusWritingUsabilityCodeCount(group.Key, group.Count()))
            .ToArray();

    private static bool CompletesAutomaticPathWithoutPrompt(CorpusWritingUsabilityParticipant participant) =>
        participant.Tasks!.All(task => task.Outcome == "completed" && task.CompletedWithoutPrompt);

    private static string BuildMarkdown(CorpusWritingUsabilityStudyReportResult report)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Corpus Writing Usability Study Report");
        builder.AppendLine();
        builder.AppendLine($"- Study: `{report.StudyId}`");
        builder.AppendLine($"- Revision: `{report.StudyRevision}`");
        builder.AppendLine($"- Study kind: `{report.StudyKind}`");
        builder.AppendLine($"- Generated at: `{report.GeneratedAt:O}`");
        builder.AppendLine($"- Participants: `{report.ParticipantCount}`");
        builder.AppendLine($"- Unprompted full-path completions: `{report.UnpromptedFullPathCompletionCount}` ({Format(report.UnpromptedFullPathCompletionRate)})");
        builder.AppendLine($"- Acceptance: `{(report.AcceptancePassed ? "passed" : "not-passed")}`");
        builder.AppendLine();
        builder.AppendLine("| Task | Completion | Unprompted | Avg seconds | Avg backtracks | Avg difficulty |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var task in report.Tasks)
        {
            builder.AppendLine($"| `{task.TaskId}` | {Format(task.CompletionRate)} | {Format(task.UnpromptedCompletionRate)} | {Format(task.AverageDurationSeconds)} | {Format(task.AverageBacktrackCount)} | {Format(task.AverageDifficulty)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Coded Observations");
        foreach (var task in report.Tasks)
        {
            builder.AppendLine($"- `{task.TaskId}`: first failures {FormatCodeCounts(task.FirstFailureCounts)}; recovery actions {FormatCodeCounts(task.RecoveryActionCounts)}.");
        }

        return builder.ToString();
    }

    private static string FormatCodeCounts(IReadOnlyList<CorpusWritingUsabilityCodeCount> codeCounts) =>
        codeCounts.Count == 0
            ? "none"
            : string.Join(", ", codeCounts.Select(item => $"`{item.Code}` ({item.Count})"));

    private static void ValidateJsonShape(JsonElement root)
    {
        ValidateObject(root, RootProperties, "$", RootProperties.ToArray());
        foreach (var participant in ValidateObjectArray(root.GetProperty("participants"), "$.participants"))
        {
            ValidateObject(participant, ParticipantProperties, "$.participants[]", ParticipantProperties.ToArray());
            foreach (var task in ValidateObjectArray(participant.GetProperty("tasks"), "$.participants[].tasks"))
            {
                ValidateObject(task, TaskProperties, "$.participants[].tasks[]", TaskProperties.ToArray());
            }
        }
    }

    private static void ValidateFixture(CorpusWritingUsabilityStudyFixture fixture)
    {
        if (!string.Equals(fixture.SchemaVersion, FixtureSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsupported usability study fixture schema '{fixture.SchemaVersion}'.");
        }

        ValidateIdentifier(fixture.StudyId, "study_id");
        ValidateIdentifier(fixture.StudyRevision, "study_revision");
        if (fixture.StudyKind is not ("contract" or "human"))
        {
            throw new InvalidDataException("study_kind must be 'contract' or 'human'.");
        }

        RequireNonEmpty(fixture.Participants, "participants");
        var participants = fixture.Participants!;
        if (fixture.StudyKind == "human" && participants.Count < 5)
        {
            throw new InvalidDataException("human participants must contain at least 5 items.");
        }

        var participantIds = participants.Select(item => item.ParticipantIdHash).ToArray();
        if (participantIds.Any(string.IsNullOrWhiteSpace) || participantIds.Distinct(StringComparer.Ordinal).Count() != participantIds.Length)
        {
            throw new InvalidDataException("participants must have unique hash identifiers.");
        }

        foreach (var participant in participants)
        {
            ValidateSha256(participant.ParticipantIdHash, "participants.participant_id_hash");
            RequireNonEmpty(participant.Tasks, "participants.tasks");
            if (participant.Tasks!.Count != CoreTaskIds.Count)
            {
                throw new InvalidDataException("Each participant must contain every core task exactly once.");
            }

            var taskIds = participant.Tasks.Select(task => task.TaskId).ToArray();
            if (taskIds.Any(string.IsNullOrWhiteSpace) || !taskIds.Order(StringComparer.Ordinal).SequenceEqual(CoreTaskIds.Order(StringComparer.Ordinal)))
            {
                throw new InvalidDataException("Each participant must contain the fixed core task set exactly once.");
            }

            foreach (var task in participant.Tasks)
            {
                ValidateTask(task);
            }
        }
    }

    private static void ValidateTask(CorpusWritingUsabilityTask task)
    {
        ValidateIdentifier(task.TaskId, "tasks.task_id");
        if (task.Outcome is not ("completed" or "abandoned"))
        {
            throw new InvalidDataException("tasks.outcome must be 'completed' or 'abandoned'.");
        }

        if (task.Outcome != "completed" && task.CompletedWithoutPrompt)
        {
            throw new InvalidDataException("An abandoned task cannot be marked completed_without_prompt.");
        }

        if (task.DurationSeconds < 0 || task.DurationSeconds > 7200 || task.BacktrackCount is < 0 or > 100)
        {
            throw new InvalidDataException("Task duration_seconds or backtrack_count is outside the allowed range.");
        }

        if (task.Difficulty is < 1 or > 5)
        {
            throw new InvalidDataException("Task difficulty must be between 1 and 5.");
        }

        ValidateOptionalCode(task.FirstFailureCode, FailureCodes, "tasks.first_failure_code");
        ValidateOptionalCode(task.RecoveryActionCode, RecoveryActionCodes, "tasks.recovery_action_code");
    }

    private static void ValidateObject(JsonElement element, ISet<string> allowed, string path, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{path} must be an object.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
            {
                throw new InvalidDataException($"Unexpected field '{property.Name}' at {path}; usability fixtures only permit redacted fields.");
            }

            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"Duplicate field '{property.Name}' at {path}.");
            }
        }

        foreach (var property in required)
        {
            if (!seen.Contains(property))
            {
                throw new InvalidDataException($"Missing required field '{property}' at {path}.");
            }
        }
    }

    private static IEnumerable<JsonElement> ValidateObjectArray(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{path} must be an array.");
        }

        return element.EnumerateArray().ToArray();
    }

    private static void RequireNonEmpty<T>(IReadOnlyList<T>? values, string name)
    {
        if (values is null || values.Count == 0)
        {
            throw new InvalidDataException($"{name} must not be empty.");
        }
    }

    private static void ValidateIdentifier(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !SafeIdentifierPattern.IsMatch(value))
        {
            throw new InvalidDataException($"{name} must be a redacted lowercase identifier.");
        }
    }

    private static void ValidateOptionalCode(string? value, ISet<string> allowedCodes, string name)
    {
        if (value is not null)
        {
            ValidateIdentifier(value, name);
            if (!allowedCodes.Contains(value))
            {
                throw new InvalidDataException($"{name} must use a code from the fixed usability codebook.");
            }
        }
    }

    private static void ValidateSha256(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !Sha256Pattern.IsMatch(value))
        {
            throw new InvalidDataException($"{name} must be a sha256 hash.");
        }
    }

    private static ISet<string> PropertySet(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string Format(double value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed record CorpusWritingUsabilityStudyFixture(
    [property: JsonPropertyName("schema_version")] string? SchemaVersion,
    [property: JsonPropertyName("study_id")] string? StudyId,
    [property: JsonPropertyName("study_revision")] string? StudyRevision,
    [property: JsonPropertyName("study_kind")] string? StudyKind,
    [property: JsonPropertyName("participants")] IReadOnlyList<CorpusWritingUsabilityParticipant>? Participants);

internal sealed record CorpusWritingUsabilityParticipant(
    [property: JsonPropertyName("participant_id_hash")] string? ParticipantIdHash,
    [property: JsonPropertyName("tasks")] IReadOnlyList<CorpusWritingUsabilityTask>? Tasks);

internal sealed record CorpusWritingUsabilityTask(
    [property: JsonPropertyName("task_id")] string? TaskId,
    [property: JsonPropertyName("outcome")] string? Outcome,
    [property: JsonPropertyName("completed_without_prompt")] bool CompletedWithoutPrompt,
    [property: JsonPropertyName("duration_seconds")] int DurationSeconds,
    [property: JsonPropertyName("backtrack_count")] int BacktrackCount,
    [property: JsonPropertyName("first_failure_code")] string? FirstFailureCode,
    [property: JsonPropertyName("recovery_action_code")] string? RecoveryActionCode,
    [property: JsonPropertyName("difficulty")] int Difficulty);

internal sealed record CorpusWritingUsabilityStudyReportResult(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("study_id")] string StudyId,
    [property: JsonPropertyName("study_revision")] string StudyRevision,
    [property: JsonPropertyName("study_kind")] string StudyKind,
    [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("participant_count")] int ParticipantCount,
    [property: JsonPropertyName("unprompted_full_path_completion_count")] int UnpromptedFullPathCompletionCount,
    [property: JsonPropertyName("unprompted_full_path_completion_rate")] double UnpromptedFullPathCompletionRate,
    [property: JsonPropertyName("acceptance_passed")] bool AcceptancePassed,
    [property: JsonPropertyName("tasks")] IReadOnlyList<CorpusWritingUsabilityTaskReport> Tasks);

internal sealed record CorpusWritingUsabilityTaskReport(
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("completion_count")] int CompletionCount,
    [property: JsonPropertyName("completion_rate")] double CompletionRate,
    [property: JsonPropertyName("unprompted_completion_count")] int UnpromptedCompletionCount,
    [property: JsonPropertyName("unprompted_completion_rate")] double UnpromptedCompletionRate,
    [property: JsonPropertyName("average_duration_seconds")] double AverageDurationSeconds,
    [property: JsonPropertyName("average_backtrack_count")] double AverageBacktrackCount,
    [property: JsonPropertyName("average_difficulty")] double AverageDifficulty,
    [property: JsonPropertyName("first_failure_counts")] IReadOnlyList<CorpusWritingUsabilityCodeCount> FirstFailureCounts,
    [property: JsonPropertyName("recovery_action_counts")] IReadOnlyList<CorpusWritingUsabilityCodeCount> RecoveryActionCounts);

internal sealed record CorpusWritingUsabilityCodeCount(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("count")] int Count);
