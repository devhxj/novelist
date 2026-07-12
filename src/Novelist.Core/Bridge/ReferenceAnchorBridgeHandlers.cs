using System.Text.Json;
using System.Text.RegularExpressions;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;
using Novelist.Core.App;

namespace Novelist.Core.Bridge;

public static class ReferenceAnchorBridgeHandlers
{
    private const int MaterialListPreviewMaxChars = 160;

    public static BridgeDispatcher RegisterReferenceAnchorHandlers(
        this BridgeDispatcher dispatcher,
        IReferenceAnchorService service)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(service);

        dispatcher.Register("CreateReferenceAnchor", async (context, cancellationToken) =>
            SanitizeAnchor(
                await service.CreateAnchorAsync(
                    ReadObjectArg<CreateReferenceAnchorPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("RegisterReferenceMaterializationSource", async (context, cancellationToken) =>
            SanitizeAnchor(
                await service.RegisterMaterializationSourceAsync(
                    ReadObjectArg<CreateReferenceAnchorPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("CreateReferenceAnchors", async (context, cancellationToken) =>
            SanitizeAnchors(
                await service.CreateAnchorsAsync(
                    ReadObjectArg<CreateReferenceAnchorsPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("CreateReferenceAnchorsWithResult", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeCreateAnchorsResult(
                await service.CreateAnchorsWithResultAsync(
                    ReadObjectArg<CreateReferenceAnchorsPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetReferenceAnchors", async (context, cancellationToken) =>
            SanitizeAnchors(
                await service.GetAnchorsAsync(
                    ReadLongArg(context.Payload, 0, "novelId"),
                    cancellationToken)));

        dispatcher.Register("DeleteReferenceAnchor", async (context, cancellationToken) =>
        {
            await service.DeleteAnchorAsync(
                ReadLongArg(context.Payload, 0, "novelId"),
                ReadLongArg(context.Payload, 1, "anchorId"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteReferenceAnchors", async (context, cancellationToken) =>
        {
            await service.DeleteAnchorsAsync(
                ReadObjectArg<DeleteReferenceAnchorsPayload>(context.Payload, 0, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("DeleteReferenceMaterials", async (context, cancellationToken) =>
        {
            await service.DeleteMaterialsAsync(
                ReadObjectArg<DeleteReferenceMaterialsPayload>(context.Payload, 0, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("RestoreReferenceMaterials", async (context, cancellationToken) =>
        {
            await service.RestoreMaterialsAsync(
                ReadObjectArg<RestoreReferenceMaterialsPayload>(context.Payload, 0, "input"),
                cancellationToken);
            return null;
        });

        dispatcher.Register("PromoteReferenceAnchorToWorkspaceCorpus", async (context, cancellationToken) =>
            SanitizeAnchor(
                await service.PromoteAnchorToWorkspaceCorpusAsync(
                    ReadObjectArg<PromoteReferenceAnchorToWorkspaceCorpusPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("PromoteReferenceAnchorsToWorkspaceCorpus", async (context, cancellationToken) =>
            SanitizeAnchors(
                await service.PromoteAnchorsToWorkspaceCorpusAsync(
                    ReadObjectArg<PromoteReferenceAnchorsToWorkspaceCorpusPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("UpdateReferenceAnchorMetadata", async (context, cancellationToken) =>
            SanitizeAnchor(
                await service.UpdateAnchorMetadataAsync(
                    ReadObjectArg<UpdateReferenceAnchorMetadataPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("RebuildReferenceAnchor", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeBuildStatus(
                await service.RebuildAnchorAsync(
                    ReadLongArg(context.Payload, 0, "novelId"),
                    ReadLongArg(context.Payload, 1, "anchorId"),
                    cancellationToken)));

        dispatcher.Register("GetReferenceAnchorBuildStatus", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeBuildStatus(
                await service.GetBuildStatusAsync(
                    ReadLongArg(context.Payload, 0, "novelId"),
                    ReadLongArg(context.Payload, 1, "anchorId"),
                    cancellationToken)));

        dispatcher.Register("SearchReferenceMaterials", async (context, cancellationToken) =>
            SanitizeMaterialSearchResults(
                await service.SearchMaterialsAsync(
                    ReadObjectArg<SearchReferenceMaterialsPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetReferenceMaterialCoverage", async (context, cancellationToken) =>
            SanitizeMaterialCoverage(
                await service.GetMaterialCoverageAsync(
                    ReadObjectArg<GetReferenceMaterialCoveragePayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetReferenceMaterialTagReviewQueue", async (context, cancellationToken) =>
            SanitizeMaterialTagReviewQueueResults(
                await service.GetMaterialTagReviewQueueAsync(
                    ReadObjectArg<GetReferenceMaterialTagReviewQueuePayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetReferenceMaterialDetail", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeMaterialDetail(
                await service.GetMaterialDetailAsync(
                    ReadObjectArg<GetReferenceMaterialDetailPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetReferenceSourceSegmentDetail", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeSourceSegmentDetail(
                await service.GetSourceSegmentDetailAsync(
                    ReadObjectArg<GetReferenceSourceSegmentDetailPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("GetReferenceSourceProcessingDetail", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeSourceProcessingDetail(
                await service.GetSourceProcessingDetailAsync(
                    ReadObjectArg<GetReferenceSourceProcessingDetailPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("UpdateReferenceMaterialTags", async (context, cancellationToken) =>
            ToMaterialSummary(
                await service.UpdateMaterialTagsAsync(
                    ReadObjectArg<UpdateReferenceMaterialTagsPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("UpdateReferenceMaterialsTags", async (context, cancellationToken) =>
            (await service.UpdateMaterialsTagsAsync(
                ReadObjectArg<UpdateReferenceMaterialsTagsPayload>(context.Payload, 0, "input"),
                cancellationToken))
            .Select(ToMaterialSummary)
            .ToArray());

        dispatcher.Register("AdaptReferenceMaterial", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeAdaptMaterialResult(
                await service.AdaptMaterialAsync(
                    ReadObjectArg<AdaptReferenceMaterialPayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("AuditReferenceReuse", async (context, cancellationToken) =>
            ReferencePayloadSanitizer.SanitizeReuseAudit(
                await service.AuditCandidateAsync(
                    ReadObjectArg<AuditReferenceReusePayload>(context.Payload, 0, "input"),
                    cancellationToken)));

        dispatcher.Register("RecordReferenceUserFeedback", async (context, cancellationToken) =>
            await service.RecordUserFeedbackAsync(
                ReadObjectArg<RecordReferenceUserFeedbackPayload>(context.Payload, 0, "input"),
                cancellationToken));

        dispatcher.Register("GetReferenceUserFeedback", async (context, cancellationToken) =>
            await service.GetUserFeedbackAsync(
                ReadObjectArg<GetReferenceUserFeedbackPayload>(context.Payload, 0, "input"),
                cancellationToken));

        return dispatcher;
    }

    private static ReferenceAnchorPayload SanitizeAnchor(ReferenceAnchorPayload anchor)
    {
        return ReferencePayloadSanitizer.SanitizeAnchor(anchor);
    }

    private static IReadOnlyList<ReferenceAnchorPayload> SanitizeAnchors(IReadOnlyList<ReferenceAnchorPayload> anchors)
    {
        return anchors
            .Select(SanitizeAnchor)
            .ToArray();
    }

    private static PageResultPayload<ReferenceMaterialSummaryPayload> SanitizeMaterialSearchResults(
        PageResultPayload<ReferenceMaterialPayload> result)
    {
        return new PageResultPayload<ReferenceMaterialSummaryPayload>(
            result.Items.Select(ToMaterialSummary).ToArray(),
            result.Total,
            result.Page,
            result.Size,
            result.TotalPages);
    }

    private static ReferenceMaterialCoveragePayload SanitizeMaterialCoverage(
        ReferenceMaterialCoveragePayload coverage)
    {
        return coverage with
        {
            Facets = (coverage.Facets ?? Array.Empty<ReferenceMaterialFacetPayload>())
                .Select(facet => facet with
                {
                    Key = ReferencePayloadSanitizer.RedactAndBoundText(facet.Key, 128),
                    Values = (facet.Values ?? Array.Empty<ReferenceMaterialFacetValuePayload>())
                        .Select(value => value with
                        {
                            Value = ReferencePayloadSanitizer.RedactAndBoundText(value.Value, 128)
                        })
                        .ToArray()
                })
                .ToArray()
        };
    }

    private static PageResultPayload<ReferenceMaterialTagReviewItemPayload> SanitizeMaterialTagReviewQueueResults(
        PageResultPayload<ReferenceMaterialTagReviewItemPayload> result)
    {
        return new PageResultPayload<ReferenceMaterialTagReviewItemPayload>(
            result.Items.Select(SanitizeMaterialTagReviewItem).ToArray(),
            result.Total,
            result.Page,
            result.Size,
            result.TotalPages);
    }

    private static ReferenceMaterialTagReviewItemPayload SanitizeMaterialTagReviewItem(
        ReferenceMaterialTagReviewItemPayload item)
    {
        return item with
        {
            Material = ReferencePayloadSanitizer.SanitizeMaterialSummary(item.Material),
            Issues = (item.Issues ?? Array.Empty<ReferenceMaterialTagReviewIssuePayload>())
                .Select(issue => issue with
                {
                    Code = ReferencePayloadSanitizer.RedactAndBoundText(issue.Code, 128),
                    Label = ReferencePayloadSanitizer.RedactAndBoundText(issue.Label, 256),
                    Severity = ReferencePayloadSanitizer.RedactAndBoundText(issue.Severity, 128)
                })
                .ToArray()
        };
    }

    private static ReferenceMaterialSummaryPayload ToMaterialSummary(ReferenceMaterialPayload material)
    {
        var preview = BuildPreview(material.Text, MaterialListPreviewMaxChars);
        return ReferencePayloadSanitizer.SanitizeMaterialSummary(new ReferenceMaterialSummaryPayload(
            material.MaterialId,
            material.AnchorId,
            material.SourceSegmentId,
            material.MaterialType,
            material.FunctionTag,
            material.EmotionTag,
            material.SceneTag,
            material.PovTag,
            material.TechniqueTag,
            material.FunctionConfidence,
            material.EmotionConfidence,
            material.PovConfidence,
            preview.Text,
            preview.Truncated,
            material.SourceHash,
            material.ExtractorVersion,
            material.UserVerified,
            material.CreatedAt,
            ScoreComponents: material.ScoreComponents));
    }

    private static TextPreview BuildPreview(string? text, int maxLength)
    {
        var normalized = ReferencePayloadSanitizer.RedactSensitiveText(
            Regex.Replace((text ?? string.Empty).Trim(), @"\s+", " "));
        if (normalized.Length <= maxLength)
        {
            return new TextPreview(normalized, false);
        }

        return new TextPreview(normalized[..maxLength].TrimEnd() + "...", true);
    }

    private static T ReadObjectArg<T>(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Object)
        {
            throw Invalid(argumentName, "Value must be an object.");
        }

        try
        {
            return JsonSerializer.Deserialize<T>(value.GetRawText(), BridgeJson.SerializerOptions)
                ?? throw Invalid(argumentName, "Value must not be null.");
        }
        catch (JsonException)
        {
            throw Invalid(argumentName, "Value must match the expected object shape.");
        }
    }

    private static long ReadLongArg(JsonElement? payload, int index, string argumentName)
    {
        var value = ReadArg(payload, index, argumentName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var number))
        {
            throw Invalid(argumentName, "Value must be an integer.");
        }

        return number;
    }

    private static JsonElement ReadArg(JsonElement? payload, int index, string argumentName)
    {
        if (payload is null ||
            payload.Value.ValueKind != JsonValueKind.Object ||
            !payload.Value.TryGetProperty("args", out var args) ||
            args.ValueKind != JsonValueKind.Array ||
            args.GetArrayLength() <= index)
        {
            throw Invalid(argumentName, $"Argument at index {index} is required.");
        }

        return args[index];
    }

    private static BridgeValidationException Invalid(string argumentName, string message)
    {
        return new BridgeValidationException(
            $"Invalid argument '{argumentName}'.",
            new Dictionary<string, string> { [argumentName] = message });
    }

    private readonly record struct TextPreview(string Text, bool Truncated);
}
