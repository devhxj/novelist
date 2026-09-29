using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Novelist.Infrastructure.App;

// M0 埋点：材料化章节级阶段耗时诊断。
//
// 只做观测，不参与任何业务判定，也不进入 contracts / bridge / UI。
// 输出落在语料库同目录的 materialization-diagnostics.jsonl，每行一章，
// 供提速方案回答三个问题：plan 占比、平均每章趟数与每趟产出、db+其他 占比。
internal sealed record ReferenceMaterializationChapterDiagnostics(
    string RunId,
    int ChapterIndex,
    double PlanMs,
    double RoundMs,
    int RoundCount,
    double QualifyMs,
    double EmbedMs,
    double IndexMs,
    double TotalMs,
    int ModelCallCount)
{
    // 未单独计时的部分（DB 往返、状态读写、租约心跳、候选构建等）用差值近似。
    // 目的只是判断"db + 其他"是否值得单独优化，不追求精确归因。
    public double OtherMs => Math.Max(0d, TotalMs - PlanMs - RoundMs - QualifyMs - EmbedMs - IndexMs);
}

/// <summary>
/// 单章计时的收集器。以参数形式而不是返回值传递，是因为
/// <c>ReferenceMaterializationWorker.ProcessChapterAsync</c> 存在多个提前返回分支，
/// 用 sink 可以让所有分支共享同一份计时而无需逐处返回结果。
/// 每章一个实例，因此未来章级并发（M1）下天然互不干扰。
/// </summary>
internal sealed class ReferenceMaterializationChapterDiagnosticsSink
{
    private readonly Stopwatch _total = Stopwatch.StartNew();

    public double PlanMs { get; private set; }
    public double RoundMs { get; private set; }
    public double QualifyMs { get; private set; }
    public double EmbedMs { get; private set; }
    public double IndexMs { get; private set; }
    public int RoundCount { get; private set; }
    public int ModelCallCount { get; private set; }

    public void AddPlan(double milliseconds) => PlanMs += milliseconds;

    public void AddRound(double milliseconds)
    {
        RoundMs += milliseconds;
        RoundCount++;
    }

    public void AddQualify(double milliseconds) => QualifyMs += milliseconds;

    public void AddEmbed(double milliseconds) => EmbedMs += milliseconds;

    public void AddIndex(double milliseconds) => IndexMs += milliseconds;

    public void AddModelCalls(int count) => ModelCallCount += count;

    public ReferenceMaterializationChapterDiagnostics Complete(string runId, int chapterIndex)
    {
        _total.Stop();
        return new ReferenceMaterializationChapterDiagnostics(
            runId,
            chapterIndex,
            Math.Round(PlanMs, 2),
            Math.Round(RoundMs, 2),
            RoundCount,
            Math.Round(QualifyMs, 2),
            Math.Round(EmbedMs, 2),
            Math.Round(IndexMs, 2),
            Math.Round(_total.Elapsed.TotalMilliseconds, 2),
            ModelCallCount);
    }
}

internal static class ReferenceMaterializationDiagnosticsWriter
{
    private const string FileName = "materialization-diagnostics.jsonl";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    private static readonly object Gate = new();

    public static void Append(string databasePath, ReferenceMaterializationChapterDiagnostics diagnostics)
    {
        try
        {
            var directory = Path.GetDirectoryName(databasePath);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var payload = new Dictionary<string, object?>
            {
                ["run_id"] = diagnostics.RunId,
                ["chapter_index"] = diagnostics.ChapterIndex,
                ["plan_ms"] = diagnostics.PlanMs,
                ["round_ms"] = diagnostics.RoundMs,
                ["round_count"] = diagnostics.RoundCount,
                ["qualify_ms"] = diagnostics.QualifyMs,
                ["embed_ms"] = diagnostics.EmbedMs,
                ["index_ms"] = diagnostics.IndexMs,
                ["other_ms"] = Math.Round(diagnostics.OtherMs, 2),
                ["total_ms"] = diagnostics.TotalMs,
                ["model_call_count"] = diagnostics.ModelCallCount,
                ["recorded_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            };

            var line = JsonSerializer.Serialize(payload, Options);
            lock (Gate)
            {
                File.AppendAllText(Path.Combine(directory, FileName), line + Environment.NewLine);
            }
        }
        catch
        {
            // 诊断写入失败绝不能影响材料化主流程——观测永远不该成为故障源。
        }
    }
}
