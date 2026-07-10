using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novelist.Infrastructure.App;

internal static class CorpusDrivenWritingEvaluationCommand
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
            var report = await CorpusDrivenWritingEvaluationReport.EvaluateFileAsync(
                options.FixturePath,
                options.OutputDirectory,
                generatedAt,
                cancellationToken);
            var summary = new CorpusWritingEvaluationCommandSummary(
                report.SchemaVersion,
                report.DatasetId,
                report.DatasetRevision,
                report.DatasetKind,
                report.QueryCaseCount,
                report.BlueprintCaseCount,
                report.InsertionCaseCount);

            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(summary, JsonOptions));
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            await standardError.WriteLineAsync("evaluation_invalid_arguments");
            return 2;
        }
        catch (Exception)
        {
            await standardError.WriteLineAsync("evaluation_failed");
            return 1;
        }
    }

    private static CorpusWritingEvaluationCommandOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 4)
        {
            throw new ArgumentException("Evaluation requires --fixture and --output.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var key = arguments[index];
            var value = arguments[index + 1];
            if (key is not ("--fixture" or "--output") || string.IsNullOrWhiteSpace(value) || !values.TryAdd(key, value))
            {
                throw new ArgumentException("Evaluation arguments are invalid.");
            }
        }

        if (!values.TryGetValue("--fixture", out var fixturePath) || !values.TryGetValue("--output", out var outputDirectory))
        {
            throw new ArgumentException("Evaluation requires --fixture and --output.");
        }

        return new CorpusWritingEvaluationCommandOptions(
            Path.GetFullPath(fixturePath),
            Path.GetFullPath(outputDirectory));
    }

    private sealed record CorpusWritingEvaluationCommandOptions(string FixturePath, string OutputDirectory);
}

internal sealed record CorpusWritingEvaluationCommandSummary(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("dataset_id")] string DatasetId,
    [property: JsonPropertyName("dataset_revision")] string DatasetRevision,
    [property: JsonPropertyName("dataset_kind")] string DatasetKind,
    [property: JsonPropertyName("query_case_count")] int QueryCaseCount,
    [property: JsonPropertyName("blueprint_case_count")] int BlueprintCaseCount,
    [property: JsonPropertyName("insertion_case_count")] int InsertionCaseCount);
