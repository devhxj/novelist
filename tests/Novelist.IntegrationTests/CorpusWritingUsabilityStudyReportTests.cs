using System.Text.Json;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class CorpusWritingUsabilityStudyReportTests
{
    [Fact]
    public async Task CommandWritesSanitizedSummaryForValidFixture()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-usability-contract.json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N"));

        try
        {
            using var standardOutput = new StringWriter();
            using var standardError = new StringWriter();

            var exitCode = await CorpusWritingUsabilityStudyCommand.RunAsync(
                ["--fixture", fixturePath, "--output", outputDirectory],
                standardOutput,
                standardError,
                new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero),
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            Assert.Empty(standardError.ToString());
            using var summary = JsonDocument.Parse(standardOutput.ToString());
            Assert.Equal("corpus-writing-usability-report-v1", summary.RootElement.GetProperty("schema_version").GetString());
            Assert.Equal("contract", summary.RootElement.GetProperty("study_kind").GetString());
            Assert.DoesNotContain(fixturePath, standardOutput.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(outputDirectory, standardOutput.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ContractFixtureProducesStableRedactedStudyReport()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-usability-contract.json");
        var outputRoot = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N"));

        try
        {
            var generatedAt = new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero);
            var first = await CorpusWritingUsabilityStudyReport.EvaluateFileAsync(
                fixturePath,
                Path.Combine(outputRoot, "first"),
                generatedAt,
                CancellationToken.None);
            var second = await CorpusWritingUsabilityStudyReport.EvaluateFileAsync(
                fixturePath,
                Path.Combine(outputRoot, "second"),
                generatedAt,
                CancellationToken.None);

            Assert.Equal("contract", first.StudyKind);
            Assert.Equal(2, first.ParticipantCount);
            Assert.Equal(1, first.UnpromptedFullPathCompletionCount);
            Assert.Equal(0.5d, first.UnpromptedFullPathCompletionRate, 6);
            Assert.False(first.AcceptancePassed);
            Assert.Equal(5, first.Tasks.Count);
            Assert.Equal(1, first.Tasks.Single(item => item.TaskId == "import_start_analysis").CompletionCount);
            Assert.Equal(2, first.Tasks.Single(item => item.TaskId == "blocked_recover_insert").CompletionCount);

            var firstJsonPath = Path.Combine(outputRoot, "first", "corpus-writing-usability-report.json");
            var secondJsonPath = Path.Combine(outputRoot, "second", "corpus-writing-usability-report.json");
            var firstMarkdownPath = Path.Combine(outputRoot, "first", "corpus-writing-usability-report.md");
            var secondMarkdownPath = Path.Combine(outputRoot, "second", "corpus-writing-usability-report.md");

            Assert.Equal(await File.ReadAllTextAsync(firstJsonPath), await File.ReadAllTextAsync(secondJsonPath));
            Assert.Equal(await File.ReadAllTextAsync(firstMarkdownPath), await File.ReadAllTextAsync(secondMarkdownPath));

            using var report = JsonDocument.Parse(await File.ReadAllTextAsync(firstJsonPath));
            Assert.Equal("corpus-writing-usability-report-v1", report.RootElement.GetProperty("schema_version").GetString());
            Assert.False(report.RootElement.GetProperty("acceptance_passed").GetBoolean());
            var blockedTask = report.RootElement
                .GetProperty("tasks")
                .EnumerateArray()
                .Single(item => item.GetProperty("task_id").GetString() == "blocked_recover_insert");
            var recoveryActions = blockedTask.GetProperty("recovery_action_counts").EnumerateArray().ToArray();
            Assert.Collection(recoveryActions,
                item =>
                {
                    Assert.Equal("return_to_blueprint", item.GetProperty("code").GetString());
                    Assert.Equal(2, item.GetProperty("count").GetInt32());
                });

            Assert.Contains(
                "recovery actions `return_to_blueprint` (2).",
                await File.ReadAllTextAsync(firstMarkdownPath),
                StringComparison.Ordinal);

            var reportText = await File.ReadAllTextAsync(firstJsonPath) + await File.ReadAllTextAsync(firstMarkdownPath);
            foreach (var forbiddenValue in new[]
            {
                "雨声贴着窗缝",
                "C:\\private\\novel.txt",
                outputRoot
            })
            {
                Assert.DoesNotContain(forbiddenValue, reportText, StringComparison.Ordinal);
            }
        }
        finally
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task StudyReportRejectsSourceAndPathFields()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-usability-contract.json");
        var malformedPath = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N") + ".json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = await File.ReadAllTextAsync(fixturePath);
            fixture = fixture.Replace(
                "\"study_kind\": \"contract\",",
                "\"study_kind\": \"contract\",\n  \"source_text\": \"雨声贴着窗缝\",\n  \"source_path\": \"C:\\\\private\\\\novel.txt\",");
            Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
            await File.WriteAllTextAsync(malformedPath, fixture);

            await Assert.ThrowsAsync<InvalidDataException>(() => CorpusWritingUsabilityStudyReport.EvaluateFileAsync(
                malformedPath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
        }
        finally
        {
            if (File.Exists(malformedPath))
            {
                File.Delete(malformedPath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Theory]
    [InlineData("\"transition_blocked\"", "\"unregistered_failure_code\"")]
    [InlineData("\"return_to_blueprint\"", "\"unregistered_recovery_action\"")]
    public async Task StudyReportRejectsCodesOutsideTheFixedCodebook(string existingCode, string replacementCode)
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-usability-contract.json");
        var malformedPath = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N") + ".json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = await File.ReadAllTextAsync(fixturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(malformedPath)!);
            await File.WriteAllTextAsync(malformedPath, fixture.Replace(existingCode, replacementCode, StringComparison.Ordinal));

            await Assert.ThrowsAsync<InvalidDataException>(() => CorpusWritingUsabilityStudyReport.EvaluateFileAsync(
                malformedPath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
        }
        finally
        {
            if (File.Exists(malformedPath))
            {
                File.Delete(malformedPath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HumanStudyRejectsFewerThanFiveParticipants()
    {
        var fixturePath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "corpus-driven-writing",
            "corpus-writing-usability-contract.json");
        var humanFixturePath = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N") + ".json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N"));

        try
        {
            var fixture = await File.ReadAllTextAsync(fixturePath);
            Directory.CreateDirectory(Path.GetDirectoryName(humanFixturePath)!);
            await File.WriteAllTextAsync(humanFixturePath, fixture.Replace("\"study_kind\": \"contract\"", "\"study_kind\": \"human\""));

            await Assert.ThrowsAsync<InvalidDataException>(() => CorpusWritingUsabilityStudyReport.EvaluateFileAsync(
                humanFixturePath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None));
        }
        finally
        {
            if (File.Exists(humanFixturePath))
            {
                File.Delete(humanFixturePath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HumanStudyPassesWhenFourOfFiveParticipantsCompleteEveryTaskWithoutPrompt()
    {
        var fixturePath = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N") + ".json");
        var outputDirectory = Path.Combine(Path.GetTempPath(), "novelist-corpus-usability", Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixturePath)!);
            var taskIds = new[]
            {
                "import_start_analysis",
                "leave_resume_analysis",
                "target_to_blueprint",
                "feedback_select_prose",
                "blocked_recover_insert"
            };
            var participants = Enumerable.Range(1, 5)
                .Select(participantIndex => new
                {
                    participant_id_hash = "sha256:" + new string((char)('0' + participantIndex), 64),
                    tasks = taskIds.Select((taskId, taskIndex) => new
                    {
                        task_id = taskId,
                        outcome = participantIndex == 5 && taskIndex == 0 ? "abandoned" : "completed",
                        completed_without_prompt = participantIndex != 5 || taskIndex != 0,
                        duration_seconds = 60,
                        backtrack_count = 0,
                        first_failure_code = participantIndex == 5 && taskIndex == 0 ? "start_action_not_found" : null,
                        recovery_action_code = (string?)null,
                        difficulty = 2
                    }).ToArray()
                })
                .ToArray();
            var fixture = JsonSerializer.Serialize(new
            {
                schema_version = "corpus-writing-usability-fixtures-v1",
                study_id = "human-study-boundary",
                study_revision = "v1",
                study_kind = "human",
                participants
            });
            await File.WriteAllTextAsync(fixturePath, fixture);

            var report = await CorpusWritingUsabilityStudyReport.EvaluateFileAsync(
                fixturePath,
                outputDirectory,
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            Assert.Equal(5, report.ParticipantCount);
            Assert.Equal(4, report.UnpromptedFullPathCompletionCount);
            Assert.True(report.AcceptancePassed);
        }
        finally
        {
            if (File.Exists(fixturePath))
            {
                File.Delete(fixturePath);
            }

            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }
}
