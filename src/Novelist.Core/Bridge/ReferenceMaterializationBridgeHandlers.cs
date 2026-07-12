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

        dispatcher.Register("RetryReferenceMaterialization", async (context, cancellationToken) =>
            await ExecuteStatusAsync(() => service.RetryMaterializationAsync(
                ReadObjectArg<RetryReferenceMaterializationPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("ListReferenceMaterializationChapterProgress", async (context, cancellationToken) =>
            await ExecuteProgressAsync(() => service.ListMaterializationChapterProgressAsync(
                ReadObjectArg<ListReferenceMaterializationChapterProgressPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("ListReferenceMaterializationCandidates", async (context, cancellationToken) =>
            await ExecuteCandidatesAsync(() => service.ListMaterializationCandidatesAsync(
                ReadObjectArg<ListReferenceMaterializationCandidatesPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("ReviewReferenceMaterializationCandidate", async (context, cancellationToken) =>
            await ExecuteCandidateReviewAsync(() => service.ReviewMaterializationCandidateAsync(
                ReadObjectArg<ReviewReferenceMaterializationCandidatePayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("ListActiveReferenceMaterializationMaterials", async (context, cancellationToken) =>
            await ExecuteMaterialsAsync(() => service.ListActiveMaterialsAsync(
                ReadObjectArg<ListActiveReferenceMaterializationMaterialsPayload>(context.Payload, 0, "input"),
                cancellationToken)));

        dispatcher.Register("SearchActiveReferenceMaterializationMaterials", async (context, cancellationToken) =>
            await ExecuteSemanticMaterialsAsync(() => service.SearchActiveMaterialsAsync(
                ReadObjectArg<SearchActiveReferenceMaterializationMaterialsPayload>(context.Payload, 0, "input"),
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

    private static async ValueTask<PageResultPayload<ReferenceMaterializationMaterialPayload>> ExecuteMaterialsAsync(
        Func<ValueTask<PageResultPayload<ReferenceMaterializationMaterialPayload>>> operation)
    {
        try
        {
            var result = await operation();
            return new PageResultPayload<ReferenceMaterializationMaterialPayload>(
                result.Items.Select(SanitizeMaterial).ToArray(),
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

    private static async ValueTask<PageResultPayload<ReferenceMaterializationCandidatePayload>> ExecuteCandidatesAsync(
        Func<ValueTask<PageResultPayload<ReferenceMaterializationCandidatePayload>>> operation)
    {
        try
        {
            var result = await operation();
            return new PageResultPayload<ReferenceMaterializationCandidatePayload>(
                result.Items.Select(SanitizeCandidate).ToArray(),
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

    private static async ValueTask<ReferenceMaterializationCandidateReviewResultPayload> ExecuteCandidateReviewAsync(
        Func<ValueTask<ReferenceMaterializationCandidateReviewResultPayload>> operation)
    {
        try
        {
            var result = await operation();
            return result with
            {
                CandidateId = ReferencePayloadSanitizer.RedactAndBoundText(result.CandidateId, 128),
                Decision = ReferencePayloadSanitizer.RedactAndBoundText(result.Decision, 32),
                Status = SanitizeStatus(result.Status)
            };
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

    private static async ValueTask<IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload>> ExecuteSemanticMaterialsAsync(
        Func<ValueTask<IReadOnlyList<ReferenceMaterializationSemanticSearchHitPayload>>> operation)
    {
        try
        {
            return (await operation())
                .Select(hit => hit with { Material = SanitizeMaterial(hit.Material) })
                .ToArray();
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

    private static ReferenceMaterializationMaterialPayload SanitizeMaterial(
        ReferenceMaterializationMaterialPayload material)
    {
        return material with
        {
            MaterialId = ReferencePayloadSanitizer.RedactAndBoundText(material.MaterialId, 128),
            GenerationId = ReferencePayloadSanitizer.RedactAndBoundText(material.GenerationId, 128),
            MaterialType = ReferencePayloadSanitizer.RedactAndBoundText(material.MaterialType, 64),
            Text = ReferencePayloadSanitizer.RedactAndBoundText(material.Text, 1_400),
            Tags = new ReferenceMaterializationMaterialTagsPayload(
                SanitizeTags(material.Tags.NarrativeFunctions),
                SanitizeTags(material.Tags.EmotionMechanics),
                SanitizeTags(material.Tags.Pov),
                SanitizeTags(material.Tags.Techniques))
            {
                SceneBeatRoles = SanitizeTags(material.Tags.SceneBeatRoles),
                CharacterRelations = SanitizeTags(material.Tags.CharacterRelations),
                CausalInformationRoles = SanitizeTags(material.Tags.CausalInformationRoles)
            },
            ReasonCodes = SanitizeTags(material.ReasonCodes)
        };
    }

    private static ReferenceMaterializationCandidatePayload SanitizeCandidate(
        ReferenceMaterializationCandidatePayload candidate)
    {
        return candidate with
        {
            CandidateId = ReferencePayloadSanitizer.RedactAndBoundText(candidate.CandidateId, 128),
            RunId = ReferencePayloadSanitizer.RedactAndBoundText(candidate.RunId, 128),
            CandidateType = ReferencePayloadSanitizer.RedactAndBoundText(candidate.CandidateType, 64),
            Decision = ReferencePayloadSanitizer.RedactAndBoundText(candidate.Decision, 32),
            DecisionOrigin = ReferencePayloadSanitizer.RedactAndBoundText(candidate.DecisionOrigin, 64),
            TextPreview = ReferencePayloadSanitizer.RedactAndBoundText(candidate.TextPreview, 512),
            SourceSpans = (candidate.SourceSpans ?? Array.Empty<ReferenceMaterializationCandidateSourceSpanPayload>())
                .Take(12)
                .Select(span => new ReferenceMaterializationCandidateSourceSpanPayload(
                    ReferencePayloadSanitizer.RedactAndBoundText(span.NodeId, 128),
                    Math.Max(0, span.Start),
                    Math.Max(0, span.End)))
                .ToArray(),
            Tags = new ReferenceMaterializationMaterialTagsPayload(
                SanitizeTags(candidate.Tags.NarrativeFunctions),
                SanitizeTags(candidate.Tags.EmotionMechanics),
                SanitizeTags(candidate.Tags.Pov),
                SanitizeTags(candidate.Tags.Techniques))
            {
                SceneBeatRoles = SanitizeTags(candidate.Tags.SceneBeatRoles),
                CharacterRelations = SanitizeTags(candidate.Tags.CharacterRelations),
                CausalInformationRoles = SanitizeTags(candidate.Tags.CausalInformationRoles)
            },
            ReasonCodes = SanitizeTags(candidate.ReasonCodes)
        };
    }

    private static IReadOnlyList<string> SanitizeTags(IReadOnlyList<string>? values)
    {
        return (values ?? Array.Empty<string>())
            .Take(12)
            .Select(value => ReferencePayloadSanitizer.RedactAndBoundText(value, 96))
            .ToArray();
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
