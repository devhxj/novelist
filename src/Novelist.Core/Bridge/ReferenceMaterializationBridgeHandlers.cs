using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceMaterializationBridgeHandlers
{
    public static BridgeDispatcher RegisterReferenceMaterializationHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceMaterializationService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("AnalyzeReferenceChapterSplit", async (context, cancellationToken) =>
            SanitizeProfile(await service.AnalyzeChapterSplitAsync(
                ReadObjectArg<AnalyzeReferenceChapterSplitPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("PreviewReferenceChapterSplit", async (context, cancellationToken) =>
            SanitizeProfile(await service.PreviewChapterSplitAsync(
                ReadObjectArg<PreviewReferenceChapterSplitPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("ConfirmReferenceChapterSplit", async (context, cancellationToken) =>
            SanitizeProfile(await service.ConfirmChapterSplitAsync(
                ReadObjectArg<ConfirmReferenceChapterSplitPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        return dispatcher;
    }

    private static ReferenceChapterSplitProfilePayload SanitizeProfile(
        ReferenceChapterSplitProfilePayload profile)
    {
        return profile with
        {
            SplitProfileId = ReferencePayloadSanitizer.RedactAndBoundText(profile.SplitProfileId, 128),
            SourceHash = ReferencePayloadSanitizer.RedactAndBoundText(profile.SourceHash, 128),
            SplitMode = ReferencePayloadSanitizer.RedactAndBoundText(profile.SplitMode, 32),
            PatternKind = ReferencePayloadSanitizer.RedactAndBoundText(profile.PatternKind, 64),
            DelimiterTemplate = ReferencePayloadSanitizer.RedactAndBoundText(profile.DelimiterTemplate, 160),
            ModelProvider = profile.ModelProvider is null ? null : ReferencePayloadSanitizer.RedactAndBoundText(profile.ModelProvider, 128),
            ModelId = profile.ModelId is null ? null : ReferencePayloadSanitizer.RedactAndBoundText(profile.ModelId, 256),
            Boundaries = (profile.Boundaries ?? Array.Empty<ReferenceChapterSplitBoundaryPayload>())
                .Select(boundary => boundary with
                {
                    Title = ReferencePayloadSanitizer.RedactAndBoundText(boundary.Title, 200),
                    TextHash = ReferencePayloadSanitizer.RedactAndBoundText(boundary.TextHash, 128)
                })
                .ToArray()
        };
    }

    private static T ReadObjectArg<T>(JsonElement? payload, int index, string argumentName)
    {
        if (payload is null ||
            payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("args", out var args) ||
            args.ValueKind != JsonValueKind.Array ||
            args.GetArrayLength() <= index ||
            args[index].ValueKind != JsonValueKind.Object)
        {
            throw Invalid(argumentName, "Value must be an object.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(args[index].GetRawText(), BridgeJson.SerializerOptions)
                ?? throw Invalid(argumentName, "Value must not be null.");
        }
        catch (JsonException)
        {
            throw Invalid(argumentName, "Value must match the expected object shape.");
        }
    }

    private static BridgeValidationException Invalid(string argumentName, string message)
    {
        return new BridgeValidationException(
            $"Invalid argument '{argumentName}'.",
            new Dictionary<string, string> { [argumentName] = message });
    }
}
