using System.Globalization;
using System.Text.RegularExpressions;
using Novelist.Contracts.App;

namespace Novelist.Infrastructure.App;

internal static class ReferenceMaterialSlotDetector
{
    private static readonly Regex SlotPattern = new(
        @"\{\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}\}|\{(?<name>[A-Za-z_][A-Za-z0-9_]*)\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<ReferenceMaterialSlot> Detect(ReferenceMaterialPayload material)
    {
        ArgumentNullException.ThrowIfNull(material);

        var slots = new List<ReferenceMaterialSlot>();
        var ordinalByName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match match in SlotPattern.Matches(material.Text))
        {
            var slotName = match.Groups["name"].Value.Trim();
            if (slotName.Length == 0)
            {
                continue;
            }

            ordinalByName.TryGetValue(slotName, out var ordinal);
            ordinal++;
            ordinalByName[slotName] = ordinal;
            slots.Add(new ReferenceMaterialSlot(
                BuildSlotId(material.MaterialId, slotName, ordinal),
                material.MaterialId,
                slotName,
                match.Value,
                match.Index,
                match.Index + match.Length));
        }

        return slots;
    }

    private static string BuildSlotId(string materialId, string slotName, int ordinal)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{materialId}:slot:{slotName}:{ordinal}");
    }
}

internal sealed record ReferenceMaterialSlot(
    string SlotId,
    string MaterialId,
    string SlotName,
    string Placeholder,
    int StartOffset,
    int EndOffset);
