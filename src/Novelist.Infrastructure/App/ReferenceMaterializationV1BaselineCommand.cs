using System.Text.Json;
using System.Text.Json.Serialization;

namespace Novelist.Infrastructure.App;

internal static class ReferenceMaterializationV1BaselineCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<int> RunAsync(
        IReadOnlyList<string> arguments,
        TextWriter standardOutput,
        TextWriter standardError,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = Parse(arguments);
            var report = await ReferenceMaterializationV1BaselineReport.EvaluateAsync(
                options.CalibrationFixturePath,
                options.HoldoutFixturePath,
                options.OutputDirectory,
                cancellationToken);
            await standardOutput.WriteLineAsync(JsonSerializer.Serialize(
                new ReferenceMaterializationV1BaselineCommandSummary(
                    report.SchemaVersion,
                    report.Calibration.CaseCount,
                    report.Holdout.CaseCount,
                    report.Holdout.AcceptedMaterialPrecision,
                    report.Holdout.ShortNoiseRejectionRecall),
                JsonOptions));
            return 0;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ArgumentException)
        {
            await standardError.WriteLineAsync("materialization_v1_baseline_invalid_arguments");
            return 2;
        }
        catch (Exception)
        {
            await standardError.WriteLineAsync("materialization_v1_baseline_failed");
            return 1;
        }
    }

    private static ReferenceMaterializationV1BaselineCommandOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count != 6)
        {
            throw new ArgumentException("Baseline requires calibration, holdout, and output paths.");
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Count; index += 2)
        {
            var key = arguments[index];
            var value = arguments[index + 1];
            if (key is not ("--calibration" or "--holdout" or "--output") ||
                string.IsNullOrWhiteSpace(value) ||
                !values.TryAdd(key, value))
            {
                throw new ArgumentException("Baseline arguments are invalid.");
            }
        }

        return new ReferenceMaterializationV1BaselineCommandOptions(
            Path.GetFullPath(values["--calibration"]),
            Path.GetFullPath(values["--holdout"]),
            Path.GetFullPath(values["--output"]));
    }

    private sealed record ReferenceMaterializationV1BaselineCommandOptions(
        string CalibrationFixturePath,
        string HoldoutFixturePath,
        string OutputDirectory);
}

internal sealed record ReferenceMaterializationV1BaselineCommandSummary(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("calibration_case_count")] int CalibrationCaseCount,
    [property: JsonPropertyName("holdout_case_count")] int HoldoutCaseCount,
    [property: JsonPropertyName("holdout_accepted_material_precision")] double? HoldoutAcceptedMaterialPrecision,
    [property: JsonPropertyName("holdout_short_noise_rejection_recall")] double? HoldoutShortNoiseRejectionRecall);
