namespace Novelist.Infrastructure.App;

internal static class ReferenceDraftProvenanceIds
{
    public const string NoReusePrefix = "no-reuse:";

    public static string BuildNoReuseMaterialId(string beatId)
    {
        return NoReusePrefix + beatId;
    }

    public static bool IsNoReuseMaterialId(string materialId)
    {
        return materialId.StartsWith(NoReusePrefix, StringComparison.Ordinal);
    }
}
