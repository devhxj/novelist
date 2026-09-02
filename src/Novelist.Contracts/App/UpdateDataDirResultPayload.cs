using System.Text.Json.Serialization;

namespace Novelist.Contracts.App;

/// <summary>
/// UpdateDataDir 的出站结果：copy-first 迁移的复制统计与清单位置，
/// 供前端向作者如实呈现"复制了什么"（U13）。
/// </summary>
public sealed record UpdateDataDirResultPayload(
    [property: JsonPropertyName("copied_files")] int CopiedFiles,
    [property: JsonPropertyName("skipped_files")] int SkippedFiles,
    [property: JsonPropertyName("warnings")] int Warnings,
    [property: JsonPropertyName("manifest_path")] string ManifestPath);
