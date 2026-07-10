using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novelist.Infrastructure.App;

internal static class CorpusWritingUsabilityStudyCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);

        try
        {
            var options = Parse(arguments);
            var report = await CorpusWritingUsabilityStudyReport.EvaluateFileAsync(
                options.FixturePath,
                options.OutputDirectory,
                generatedAt,
                cancellationToken);
            var summary = new CorpusWritingUsabilityStudyCommandSummary(
                report.SchemaVersion,
                report.StudyId,
                report.StudyRevision,
                report.StudyKind,
                report.ParticipantCount,
                report.UnpromptedFullPathCompletionCount,
                report.AcceptancePassed);
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOptions));
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            await standardError.WriteLineAsync("usability_study_invalid_arguments");
            return 2;
        }
        catch (Exception)
        {
            await standardError.WriteLineAsync("usability_study_failed");
            return 1;
        }
    }

    private static CorpusWritingUsabilityStudyCommandOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 4)
        {
            throw new ArgumentException("Usability study requires --fixture and --output.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var key = arguments[index];
            var value = arguments[index + 1];
            if (key is not ("--fixture" or "--output") || string.IsNullOrWhiteSpace(value) || !values.TryAdd(key, value))
            {
                throw new ArgumentException("Usability study arguments are invalid.");
            }
        }

        if (!values.TryGetValue("--fixture", out var fixturePath) || !values.TryGetValue("--output", out var outputDirectory))
        {
            throw new ArgumentException("Usability study requires --fixture and --output.");
        }

        return new CorpusWritingUsabilityStudyCommandOptions(
            Path.GetFullPath(fixturePath),
            Path.GetFullPath(outputDirectory));
    }

    private sealed record CorpusWritingUsabilityStudyCommandOptions(string FixturePath, string OutputDirectory);
}

internal sealed record CorpusWritingUsabilityStudyCommandSummary(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("study_id")] string StudyId,
    [property: JsonPropertyName("study_revision")] string StudyRevision,
    [property: JsonPropertyName("study_kind")] string StudyKind,
    [property: JsonPropertyName("participant_count")] int ParticipantCount,
    [property: JsonPropertyName("unprompted_full_path_completion_count")] int UnpromptedFullPathCompletionCount,
    [property: JsonPropertyName("acceptance_passed")] bool AcceptancePassed);
