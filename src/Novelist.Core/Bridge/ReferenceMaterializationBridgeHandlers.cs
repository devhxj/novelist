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

        dispatcher.Register("EnqueueReferenceMaterialization", async (context, cancellationToken) =>
            await ExecuteStatusAsync(() => service.EnqueueMaterializationAsync(
                ReadObjectArg<EnqueueReferenceMaterializationPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("GetReferenceMaterializationStatus", async (context, cancellationToken) =>
            await ExecuteOptionalStatusAsync(() => service.GetMaterializationStatusAsync(
                ReadObjectArg<GetReferenceMaterializationStatusPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("ListReferenceMaterializationChapterProgress", async (context, cancellationToken) =>
            await ExecuteProgressAsync(() => service.ListMaterializationChapterProgressAsync(
                ReadObjectArg<ListReferenceMaterializationChapterProgressPayload>(context.Payload, 0, "input"),
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

    private static async ValueTask<ReferenceMaterializationStatusPayload> ExecuteStatusAsync(
        Func<ValueTask<ReferenceMaterializationStatusPayload>> operation)
    {
        try
        {
            return SanitizeStatus(await operation());
        }
        catch (ReferenceMaterializationException exception)
        {
            throw new BridgeRequestException(
                exception.ErrorCode,
                exception.Message,
                new { error_code = exception.ErrorCode },
                retryable: true);
        }
    }

    private static async ValueTask<ReferenceMaterializationStatusPayload?> ExecuteOptionalStatusAsync(
        Func<ValueTask<ReferenceMaterializationStatusPayload?>> operation)
    {
        try
        {
            var result = await operation();
            return result is null ? null : SanitizeStatus(result);
        }
        catch (ReferenceMaterializationException exception)
        {
            throw new BridgeRequestException(
                exception.ErrorCode,
                exception.Message,
                new { error_code = exception.ErrorCode },
                retryable: true);
        }
    }

    private static async ValueTask<PageResultPayload<ReferenceMaterializationChapterProgressPayload>> ExecuteProgressAsync(
        Func<ValueTask<PageResultPayload<ReferenceMaterializationChapterProgressPayload>>> operation)
    {
        try
        {
            var result = await operation();
            return new PageResultPayload<ReferenceMaterializationChapterProgressPayload>(
                result.Items.Select(SanitizeProgress).ToArray(),
                result.Total,
                result.Page,
                result.Size,
                result.TotalPages);
        }
        catch (ReferenceMaterializationException exception)
        {
            throw new BridgeRequestException(
                exception.ErrorCode,
                exception.Message,
                new { error_code = exception.ErrorCode },
                retryable: true);
        }
    }

    private static ReferenceMaterializationStatusPayload SanitizeStatus(ReferenceMaterializationStatusPayload status)
    {
        return status with
        {
            RunId = ReferencePayloadSanitizer.RedactAndBoundText(status.RunId, 128),
            SplitProfileId = ReferencePayloadSanitizer.RedactAndBoundText(status.SplitProfileId, 128),
            GenerationId = ReferencePayloadSanitizer.RedactAndBoundText(status.GenerationId, 128),
            Status = ReferencePayloadSanitizer.RedactAndBoundText(status.Status, 32),
            Llm = SanitizeModel(status.Llm),
            Embedding = SanitizeModel(status.Embedding),
            LastErrorCode = status.LastErrorCode is null ? null : ReferencePayloadSanitizer.RedactAndBoundText(status.LastErrorCode, 128),
            LastErrorMessage = status.LastErrorMessage is null ? null : ReferencePayloadSanitizer.RedactAndBoundText(status.LastErrorMessage, 512),
            NextAction = ReferencePayloadSanitizer.RedactAndBoundText(status.NextAction, 64)
        };
    }

    private static ReferenceMaterializationChapterProgressPayload SanitizeProgress(
        ReferenceMaterializationChapterProgressPayload progress)
    {
        return progress with
        {
            Status = ReferencePayloadSanitizer.RedactAndBoundText(progress.Status, 32),
            CurrentStage = ReferencePayloadSanitizer.RedactAndBoundText(progress.CurrentStage, 64),
            LastErrorCode = progress.LastErrorCode is null ? null : ReferencePayloadSanitizer.RedactAndBoundText(progress.LastErrorCode, 128),
            LastErrorMessage = progress.LastErrorMessage is null ? null : ReferencePayloadSanitizer.RedactAndBoundText(progress.LastErrorMessage, 512)
        };
    }

    private static ReferenceMaterializationModelIdentityPayload SanitizeModel(
        ReferenceMaterializationModelIdentityPayload model)
    {
        return model with
        {
            Provider = ReferencePayloadSanitizer.RedactAndBoundText(model.Provider, 128),
            ModelId = ReferencePayloadSanitizer.RedactAndBoundText(model.ModelId, 256)
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
