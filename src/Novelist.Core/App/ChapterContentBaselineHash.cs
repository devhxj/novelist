using System.Globalization;

namespace Novelist.Core.App;

/// <summary>
/// 章节保存的基线令牌（U1）。前端在 frontend/src/lib/contentBaseline.ts 维护逐字节相同的算法：
/// FNV-1a 32 位，按 UTF-16 码元（char）迭代，输出 "fnv1a:{8位小写hex}:{长度}"。
/// 两端必须同时改，BridgeFrontendContractTests 有逐字节一致性守卫。
/// </summary>
public static class ChapterContentBaselineHash
{
    public static string Compute(string content)
    {
        ArgumentNullException.ThrowIfNull(content);
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in content)
            {
                hash ^= ch;
                hash *= 16777619u;
            }

            return string.Create(
                CultureInfo.InvariantCulture,
                $"fnv1a:{hash.ToString("x8", CultureInfo.InvariantCulture)}:{content.Length}");
        }
    }
}
