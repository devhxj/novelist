using System.Text.Json;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

// M0 埋点的回归保护：诊断只做观测，因此这里的断言只覆盖"累加与落盘是否正确"，
// 不对真实耗时做任何假设——那部分由跑真实书时的 jsonl 输出负责。
public sealed class ReferenceMaterializationChapterDiagnosticsTests
{
    [Fact]
    public void SinkAccumulatesStageDurationsAndCounters()
    {
        var sink = new ReferenceMaterializationChapterDiagnosticsSink();

        sink.AddPlan(12.5);
        sink.AddRound(100d);
        sink.AddRound(50d);
        sink.AddQualify(30d);
        sink.AddEmbed(8d);
        sink.AddIndex(5d);
        sink.AddModelCalls(3);

        var diagnostics = sink.Complete("run-1", 7);

        Assert.Equal("run-1", diagnostics.RunId);
        Assert.Equal(7, diagnostics.ChapterIndex);
        Assert.Equal(12.5, diagnostics.PlanMs);
        Assert.Equal(150d, diagnostics.RoundMs);
        Assert.Equal(2, diagnostics.RoundCount);
        Assert.Equal(30d, diagnostics.QualifyMs);
        Assert.Equal(8d, diagnostics.EmbedMs);
        Assert.Equal(5d, diagnostics.IndexMs);
        Assert.Equal(3, diagnostics.ModelCallCount);
        Assert.True(diagnostics.TotalMs >= 0d);
    }

    [Fact]
    public void SinkClampsOtherMsToZeroWhenStagesExceedTotal()
    {
        var sink = new ReferenceMaterializationChapterDiagnosticsSink();

        // 注入一个远超真实墙钟的阶段耗时：other_ms 由差值算出，
        // 必须被夹到 0，不能出现负数污染后续占比统计。
        sink.AddPlan(1_000_000d);

        Assert.Equal(0d, sink.Complete("run-2", 1).OtherMs);
    }

    [Fact]
    public async Task SinkDerivesOtherMsFromRemainingTime()
    {
        var sink = new ReferenceMaterializationChapterDiagnosticsSink();

        // 先制造一段真实墙钟，让差值落在未夹紧区间；
        // 注入值必须小于它，否则 other_ms 会被夹到 0（那是另一个用例的职责）。
        await Task.Delay(60);
        sink.AddPlan(10d);
        sink.AddRound(20d);

        var diagnostics = sink.Complete("run-3", 2);

        Assert.True(diagnostics.TotalMs >= 60d);
        Assert.InRange(diagnostics.OtherMs, diagnostics.TotalMs - 31d, diagnostics.TotalMs - 29d);
    }

    [Fact]
    public void WriterAppendsOneJsonLinePerChapter()
    {
        var directory = Path.Combine(Path.GetTempPath(), "novelist-diagnostics-" + Guid.NewGuid().ToString("N"));
        try
        {
            var databasePath = Path.Combine(directory, "index.sqlite");
            var diagnostics = new ReferenceMaterializationChapterDiagnostics("run-4", 3, 1d, 2d, 1, 3d, 4d, 5d, 100d, 6);

            ReferenceMaterializationDiagnosticsWriter.Append(databasePath, diagnostics);
            ReferenceMaterializationDiagnosticsWriter.Append(databasePath, diagnostics);

            var lines = File.ReadAllLines(Path.Combine(directory, "materialization-diagnostics.jsonl"));
            Assert.Equal(2, lines.Length);

            using var document = JsonDocument.Parse(lines[0]);
            Assert.Equal("run-4", document.RootElement.GetProperty("run_id").GetString());
            Assert.Equal(3, document.RootElement.GetProperty("chapter_index").GetInt32());
            Assert.Equal(1, document.RootElement.GetProperty("round_count").GetInt32());
            Assert.Equal(6, document.RootElement.GetProperty("model_call_count").GetInt32());
            Assert.Equal(85d, document.RootElement.GetProperty("other_ms").GetDouble());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void WriterNeverThrowsIntoThePipeline()
    {
        var diagnostics = new ReferenceMaterializationChapterDiagnostics("run-5", 1, 0d, 0d, 0, 0d, 0d, 0d, 1d, 0);

        // 观测设施的任何失败都必须被吞掉：诊断绝不能成为材料化失败的原因。
        var exception = Record.Exception(() => ReferenceMaterializationDiagnosticsWriter.Append(string.Empty, diagnostics));

        Assert.Null(exception);
    }
}
