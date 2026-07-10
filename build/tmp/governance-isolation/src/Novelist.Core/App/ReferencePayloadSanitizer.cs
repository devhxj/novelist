using System.Text.RegularExpressions;
using Novelist.Contracts.App;

namespace Novelist.Core.App;

public static class ReferencePayloadSanitizer
{
    private const int MetadataMaxChars = 256;
    private const int PreviewMaxChars = 600;
    private const int DiagnosticMaxChars = 1_200;
    private const int AdaptedTextPreviewMaxChars = 800;
    private const int DraftCandidateTextMaxChars = 4_000;

    private static readonly Regex SensitiveFieldAssignmentPattern = new(
        @"(?<![\w-])[""']?(source_path|source_text|candidate_text|prompt)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\r\n;,}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SecretAssignmentPattern = new(
        @"(?<![\w-])[""']?(api[_-]?key|token|secret|authorization|password|credential)[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s;,}\]]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SensitiveIdentifierPattern = new(
        @"(?<![\w-])(source_path|source_text|candidate_text|prompt)(?=\b|_)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BearerTokenPattern = new(
        @"\bBearer\s+[A-Za-z0-9._~+/=-]{12,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ApiKeyPattern = new(
        @"\bsk-(?:proj-)?[A-Za-z0-9_-]{16,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WindowsPathPattern = new(
        @"\b[A-Z]:[\\/][^\s;,""'<>|]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UncPathPattern = new(
        @"\\\\[^\\/\s;,""'<>|]+[\\/][^\s;,""'<>|]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FileUriPattern = new(
        @"\bfile://[^\s;""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex UnixPathPattern = new(
        @"(?<!\w)/(?:Users|home|private|mnt|Volumes|var/folders|tmp)/[^\s;""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex FullTextSentinelPattern = new(
        @"__[A-Z0-9_]*(?:FULL|SOURCE|MATERIAL|CHAPTER|CANDIDATE|PROMPT)[A-Z0-9_]*__",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string RedactSensitiveText(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = SensitiveFieldAssignmentPattern.Replace(value, "[redacted_field]");
        redacted = SecretAssignmentPattern.Replace(redacted, "[redacted_secret]");
        redacted = BearerTokenPattern.Replace(redacted, "Bearer [redacted_secret]");
        redacted = ApiKeyPattern.Replace(redacted, "[redacted_secret]");
        redacted = WindowsPathPattern.Replace(redacted, "[redacted_path]");
        redacted = UncPathPattern.Replace(redacted, "[redacted_path]");
        redacted = FileUriPattern.Replace(redacted, "[redacted_path]");
        redacted = UnixPathPattern.Replace(redacted, "[redacted_path]");
        redacted = FullTextSentinelPattern.Replace(redacted, "[redacted_text]");
        return redacted;
    }

    public static string RedactSensitiveIdentifier(string? value)
    {
        return SensitiveIdentifierPattern.Replace(RedactSensitiveText(value), "redacted_field");
    }

    public static string RedactAndBoundText(string? value, int maxChars)
    {
        var redacted = RedactSensitiveText(value);
        if (redacted.Length <= maxChars)
        {
            return redacted;
        }

        return redacted[..maxChars].TrimEnd() + "...";
    }

    public static ReferenceAnchorPayload SanitizeAnchor(ReferenceAnchorPayload anchor)
    {
        return anchor with
        {
            Title = RedactSensitiveText(anchor.Title),
            Author = RedactSensitiveText(anchor.Author),
            SourcePath = string.Empty,
            SourceKind = RedactSensitiveText(anchor.SourceKind),
            LicenseStatus = RedactSensitiveText(anchor.LicenseStatus),
            SourceFileHash = RedactSensitiveText(anchor.SourceFileHash),
            BuildVersion = RedactSensitiveText(anchor.BuildVersion),
            Status = RedactSensitiveText(anchor.Status),
            Visibility = RedactSensitiveText(anchor.Visibility),
            SourceTrust = RedactSensitiveText(anchor.SourceTrust),
            UserTags = (anchor.UserTags ?? Array.Empty<string>()).Select(tag => RedactAndBoundText(tag, MetadataMaxChars)).ToArray(),
            OwnerScope = RedactSensitiveText(anchor.OwnerScope)
        };
    }

    public static CreateReferenceAnchorsResultPayload SanitizeCreateAnchorsResult(
        CreateReferenceAnchorsResultPayload result)
    {
        return result with
        {
            Succeeded = (result.Succeeded ?? Array.Empty<ReferenceAnchorPayload>())
                .Select(SanitizeAnchor)
                .ToArray(),
            Failed = (result.Failed ?? Array.Empty<CreateReferenceAnchorFailurePayload>())
                .Select(SanitizeCreateAnchorFailure)
                .ToArray()
        };
    }

    private static CreateReferenceAnchorFailurePayload SanitizeCreateAnchorFailure(
        CreateReferenceAnchorFailurePayload failure)
    {
        return failure with
        {
            Title = RedactAndBoundText(failure.Title, MetadataMaxChars),
            SourceKind = RedactAndBoundText(failure.SourceKind, MetadataMaxChars),
            SourceIdentity = RedactAndBoundText(failure.SourceIdentity, MetadataMaxChars),
            Diagnostic = RedactAndBoundText(failure.Diagnostic, DiagnosticMaxChars)
        };
    }

    public static ReferenceAnchorBuildStatusPayload? SanitizeBuildStatus(ReferenceAnchorBuildStatusPayload? status)
    {
        if (status is null)
        {
            return null;
        }

        return status with
        {
            Status = RedactSensitiveText(status.Status),
            Stage = RedactSensitiveText(status.Stage),
            LastError = RedactAndBoundText(status.LastError, DiagnosticMaxChars)
        };
    }

    public static AdaptReferenceMaterialResultPayload? SanitizeAdaptMaterialResult(
        AdaptReferenceMaterialResultPayload? result)
    {
        if (result is null)
        {
            return null;
        }

        return result with
        {
            CandidateId = RedactAndBoundText(result.CandidateId, MetadataMaxChars),
            MaterialId = RedactAndBoundText(result.MaterialId, MetadataMaxChars),
            RewriteLevel = RedactAndBoundText(result.RewriteLevel, MetadataMaxChars),
            Text = RedactAndBoundText(result.Text, AdaptedTextPreviewMaxChars),
            ChangedSlots = (result.ChangedSlots ?? Array.Empty<ReferenceSlotValuePayload>())
                .Select(SanitizeSlotValue)
                .ToArray(),
            NonSlotEdits = (result.NonSlotEdits ?? Array.Empty<string>())
                .Select(edit => RedactAndBoundText(edit, DiagnosticMaxChars))
                .ToArray(),
            Audit = SanitizeReuseAudit(result.Audit)
        };
    }

    public static IReadOnlyList<ReferenceDraftParagraphCandidatePayload> SanitizeDraftCandidates(
        IReadOnlyList<ReferenceDraftParagraphCandidatePayload>? candidates)
    {
        return (candidates ?? Array.Empty<ReferenceDraftParagraphCandidatePayload>())
            .Select(SanitizeDraftCandidate)
            .ToArray();
    }

    public static ReferenceDraftParagraphCandidatePayload SanitizeDraftCandidate(
        ReferenceDraftParagraphCandidatePayload candidate)
    {
        return candidate with
        {
            CandidateId = RedactAndBoundText(candidate.CandidateId, MetadataMaxChars),
            BeatId = RedactAndBoundText(candidate.BeatId, MetadataMaxChars),
            MaterialId = RedactAndBoundText(candidate.MaterialId, MetadataMaxChars),
            RewriteLevel = RedactAndBoundText(candidate.RewriteLevel, MetadataMaxChars),
            Text = RedactAndBoundText(candidate.Text, DraftCandidateTextMaxChars),
            ChangedSlots = (candidate.ChangedSlots ?? Array.Empty<ReferenceSlotValuePayload>())
                .Select(SanitizeSlotValue)
                .ToArray(),
            NonSlotEdits = (candidate.NonSlotEdits ?? Array.Empty<string>())
                .Select(edit => RedactAndBoundText(edit, DiagnosticMaxChars))
                .ToArray(),
            AuditStatus = RedactAndBoundText(candidate.AuditStatus, MetadataMaxChars),
            StyleAttempts = candidate.StyleAttempts is null
                ? null
                : candidate.StyleAttempts.Select(SanitizeDraftStyleAttempt).ToArray()
        };
    }

    public static ReferenceReuseAuditPayload SanitizeReuseAudit(ReferenceReuseAuditPayload? audit)
    {
        if (audit is null)
        {
            return new ReferenceReuseAuditPayload(
                string.Empty,
                "unknown",
                string.Empty,
                [],
                [],
                [],
                [],
                [],
                DateTimeOffset.UnixEpoch);
        }

        return audit with
        {
            AuditId = RedactAndBoundText(audit.AuditId, MetadataMaxChars),
            Status = RedactAndBoundText(audit.Status, MetadataMaxChars),
            RewriteLevel = RedactAndBoundText(audit.RewriteLevel, MetadataMaxChars),
            ProvenanceErrors = (audit.ProvenanceErrors ?? Array.Empty<string>())
                .Select(error => RedactAndBoundText(error, DiagnosticMaxChars))
                .ToArray(),
            UnsupportedFactErrors = (audit.UnsupportedFactErrors ?? Array.Empty<string>())
                .Select(error => RedactAndBoundText(error, DiagnosticMaxChars))
                .ToArray(),
            AiProseRisks = (audit.AiProseRisks ?? Array.Empty<string>())
                .Select(risk => RedactAndBoundText(risk, DiagnosticMaxChars))
                .ToArray(),
            NonSlotEdits = (audit.NonSlotEdits ?? Array.Empty<string>())
                .Select(edit => RedactAndBoundText(edit, DiagnosticMaxChars))
                .ToArray(),
            RequiredFixes = (audit.RequiredFixes ?? Array.Empty<string>())
                .Select(fix => RedactAndBoundText(fix, DiagnosticMaxChars))
                .ToArray()
        };
    }

    public static ReferenceMaterialDetailPayload? SanitizeMaterialDetail(ReferenceMaterialDetailPayload? detail)
    {
        if (detail is null)
        {
            return null;
        }

        return detail with
        {
            Material = SanitizeMaterialSummary(detail.Material),
            Source = SanitizeSourceSummary(detail.Source),
            Segments = (detail.Segments ?? Array.Empty<ReferenceMaterialSegmentPreviewPayload>())
                .Select(SanitizeSegmentPreview)
                .ToArray(),
            Slots = (detail.Slots ?? Array.Empty<ReferenceMaterialSlotPreviewPayload>())
                .Select(SanitizeSlotPreview)
                .ToArray(),
            ProcessingNotes = (detail.ProcessingNotes ?? Array.Empty<ReferenceMaterialProcessingNotePayload>())
                .Select(SanitizeProcessingNote)
                .ToArray()
        };
    }

    public static ReferenceSourceSegmentDetailPayload? SanitizeSourceSegmentDetail(
        ReferenceSourceSegmentDetailPayload? detail)
    {
        if (detail is null)
        {
            return null;
        }

        return detail with
        {
            Source = SanitizeSourceSummary(detail.Source),
            Segment = SanitizeSourceSegmentPreview(detail.Segment),
            ProcessingNotes = (detail.ProcessingNotes ?? Array.Empty<ReferenceMaterialProcessingNotePayload>())
                .Select(SanitizeProcessingNote)
                .ToArray()
        };
    }

    public static ReferenceSourceProcessingDetailPayload? SanitizeSourceProcessingDetail(
        ReferenceSourceProcessingDetailPayload? detail)
    {
        if (detail is null)
        {
            return null;
        }

        return detail with
        {
            Source = SanitizeSourceSummary(detail.Source),
            CurrentStatus = detail.CurrentStatus is null ? null : SanitizeProcessingStatus(detail.CurrentStatus),
            Events = (detail.Events ?? Array.Empty<ReferenceSourceProcessingEventPayload>())
                .Select(SanitizeProcessingEvent)
                .ToArray(),
            AttemptCount = Math.Max(0, detail.AttemptCount),
            CurrentAttempt = detail.CurrentAttempt is null ? null : SanitizeProcessingAttempt(detail.CurrentAttempt),
            PriorAttempts = (detail.PriorAttempts ?? Array.Empty<ReferenceSourceProcessingAttemptPayload>())
                .Select(SanitizeProcessingAttempt)
                .ToArray(),
            RecoveredFromAttemptId = RedactAndBoundText(detail.RecoveredFromAttemptId, MetadataMaxChars),
            RecoveredFromBuildId = RedactAndBoundText(detail.RecoveredFromBuildId, MetadataMaxChars),
            BlockedReason = RedactAndBoundText(detail.BlockedReason, DiagnosticMaxChars)
        };
    }

    public static ReferenceMaterialSummaryPayload SanitizeMaterialSummary(ReferenceMaterialSummaryPayload material)
    {
        var preview = BoundPreview(material.TextPreview, PreviewMaxChars);
        return material with
        {
            MaterialId = RedactAndBoundText(material.MaterialId, MetadataMaxChars),
            SourceSegmentId = RedactAndBoundText(material.SourceSegmentId, MetadataMaxChars),
            MaterialType = RedactAndBoundText(material.MaterialType, MetadataMaxChars),
            FunctionTag = RedactAndBoundText(material.FunctionTag, MetadataMaxChars),
            EmotionTag = RedactAndBoundText(material.EmotionTag, MetadataMaxChars),
            SceneTag = RedactAndBoundText(material.SceneTag, MetadataMaxChars),
            PovTag = RedactAndBoundText(material.PovTag, MetadataMaxChars),
            TechniqueTag = RedactAndBoundText(material.TechniqueTag, MetadataMaxChars),
            TextPreview = preview.Text,
            TextTruncated = material.TextTruncated || preview.Truncated,
            SourceHash = RedactAndBoundText(material.SourceHash, MetadataMaxChars),
            ExtractorVersion = RedactAndBoundText(material.ExtractorVersion, MetadataMaxChars),
            ArchiveState = RedactSensitiveText(material.ArchiveState),
            ScoreComponents = SanitizeScoreComponents(material.ScoreComponents)
        };
    }

    public static ReferenceMaterialSourceSummaryPayload SanitizeSourceSummary(ReferenceMaterialSourceSummaryPayload source)
    {
        return source with
        {
            Title = RedactSensitiveText(source.Title),
            Author = RedactSensitiveText(source.Author),
            SourceKind = RedactSensitiveText(source.SourceKind),
            LicenseStatus = RedactSensitiveText(source.LicenseStatus),
            SourceFileHash = RedactSensitiveText(source.SourceFileHash),
            BuildVersion = RedactSensitiveText(source.BuildVersion),
            Status = RedactSensitiveText(source.Status),
            Visibility = RedactSensitiveText(source.Visibility),
            SourceTrust = RedactSensitiveText(source.SourceTrust),
            UserTags = (source.UserTags ?? Array.Empty<string>()).Select(tag => RedactAndBoundText(tag, MetadataMaxChars)).ToArray(),
            OwnerScope = RedactSensitiveText(source.OwnerScope)
        };
    }

    private static ReferenceMaterialSegmentPreviewPayload SanitizeSegmentPreview(
        ReferenceMaterialSegmentPreviewPayload segment)
    {
        var preview = BoundPreview(segment.TextPreview, PreviewMaxChars);
        return segment with
        {
            SegmentId = RedactSensitiveText(segment.SegmentId),
            SegmentType = RedactSensitiveText(segment.SegmentType),
            ChapterTitle = RedactSensitiveText(segment.ChapterTitle),
            TextPreview = preview.Text,
            TextTruncated = segment.TextTruncated || preview.Truncated,
            TextHash = RedactSensitiveText(segment.TextHash)
        };
    }

    private static ReferenceSourceSegmentPreviewPayload SanitizeSourceSegmentPreview(
        ReferenceSourceSegmentPreviewPayload segment)
    {
        var preview = BoundPreview(segment.TextPreview, PreviewMaxChars);
        return segment with
        {
            SegmentId = RedactAndBoundText(segment.SegmentId, MetadataMaxChars),
            SegmentType = RedactAndBoundText(segment.SegmentType, MetadataMaxChars),
            ChapterTitle = RedactAndBoundText(segment.ChapterTitle, MetadataMaxChars),
            ParentSegmentId = RedactAndBoundText(segment.ParentSegmentId, MetadataMaxChars),
            TextPreview = preview.Text,
            TextTruncated = segment.TextTruncated || preview.Truncated,
            TextHash = RedactAndBoundText(segment.TextHash, MetadataMaxChars)
        };
    }

    private static ReferenceMaterialSlotPreviewPayload SanitizeSlotPreview(ReferenceMaterialSlotPreviewPayload slot)
    {
        return slot with
        {
            SlotName = RedactAndBoundText(slot.SlotName, MetadataMaxChars),
            Placeholder = RedactAndBoundText(slot.Placeholder, MetadataMaxChars)
        };
    }

    private static ReferenceMaterialProcessingNotePayload SanitizeProcessingNote(
        ReferenceMaterialProcessingNotePayload note)
    {
        return note with
        {
            Stage = RedactSensitiveText(note.Stage),
            Status = RedactSensitiveText(note.Status),
            Message = RedactAndBoundText(note.Message, DiagnosticMaxChars),
            AffectedSourceId = RedactAndBoundText(note.AffectedSourceId, MetadataMaxChars),
            AffectedMaterialId = RedactAndBoundText(note.AffectedMaterialId, MetadataMaxChars),
            AffectedSegmentId = RedactAndBoundText(note.AffectedSegmentId, MetadataMaxChars),
            AffectedSlotId = RedactAndBoundText(note.AffectedSlotId, MetadataMaxChars)
        };
    }

    private static ReferenceSourceProcessingStatusPayload SanitizeProcessingStatus(
        ReferenceSourceProcessingStatusPayload status)
    {
        return status with
        {
            Stage = RedactSensitiveText(status.Stage),
            Status = RedactSensitiveText(status.Status),
            Diagnostic = RedactAndBoundText(status.Diagnostic, DiagnosticMaxChars)
        };
    }

    private static ReferenceSourceProcessingEventPayload SanitizeProcessingEvent(
        ReferenceSourceProcessingEventPayload processingEvent)
    {
        return processingEvent with
        {
            EventId = RedactSensitiveText(processingEvent.EventId),
            Stage = RedactSensitiveText(processingEvent.Stage),
            Status = RedactSensitiveText(processingEvent.Status),
            Message = RedactAndBoundText(processingEvent.Message, DiagnosticMaxChars),
            AffectedSourceId = RedactAndBoundText(processingEvent.AffectedSourceId, MetadataMaxChars),
            AffectedMaterialId = RedactAndBoundText(processingEvent.AffectedMaterialId, MetadataMaxChars),
            AffectedSegmentId = RedactAndBoundText(processingEvent.AffectedSegmentId, MetadataMaxChars),
            AffectedSlotId = RedactAndBoundText(processingEvent.AffectedSlotId, MetadataMaxChars)
        };
    }

    private static ReferenceSourceProcessingAttemptPayload SanitizeProcessingAttempt(
        ReferenceSourceProcessingAttemptPayload attempt)
    {
        return attempt with
        {
            AttemptId = RedactAndBoundText(attempt.AttemptId, MetadataMaxChars),
            BuildId = RedactAndBoundText(attempt.BuildId, MetadataMaxChars),
            BuildVersion = RedactAndBoundText(attempt.BuildVersion, MetadataMaxChars),
            Stage = RedactSensitiveText(attempt.Stage),
            Status = RedactSensitiveText(attempt.Status),
            RecoveredFromAttemptId = RedactAndBoundText(attempt.RecoveredFromAttemptId, MetadataMaxChars),
            RecoveredFromBuildId = RedactAndBoundText(attempt.RecoveredFromBuildId, MetadataMaxChars),
            BlockedReason = RedactAndBoundText(attempt.BlockedReason, DiagnosticMaxChars)
        };
    }

    private static ReferenceSlotValuePayload SanitizeSlotValue(ReferenceSlotValuePayload slotValue)
    {
        return slotValue with
        {
            SlotName = RedactAndBoundText(slotValue.SlotName, MetadataMaxChars),
            Value = RedactAndBoundText(slotValue.Value, MetadataMaxChars)
        };
    }

    private static ReferenceDraftStyleAttemptPayload SanitizeDraftStyleAttempt(
        ReferenceDraftStyleAttemptPayload attempt)
    {
        return attempt with
        {
            StyleDimensions = (attempt.StyleDimensions ?? Array.Empty<string>())
                .Select(value => RedactAndBoundText(value, MetadataMaxChars))
                .ToArray(),
            ImitationIntensity = RedactAndBoundText(attempt.ImitationIntensity, MetadataMaxChars),
            AllowedCloseness = RedactAndBoundText(attempt.AllowedCloseness, MetadataMaxChars),
            RequiredEvidenceTypes = (attempt.RequiredEvidenceTypes ?? Array.Empty<string>())
                .Select(value => RedactAndBoundText(value, MetadataMaxChars))
                .ToArray(),
            ForbiddenStyleRisks = (attempt.ForbiddenStyleRisks ?? Array.Empty<string>())
                .Select(value => RedactAndBoundText(value, MetadataMaxChars))
                .ToArray(),
            Status = RedactAndBoundText(attempt.Status, MetadataMaxChars)
        };
    }

    private static TextPreview BoundPreview(string? value, int maxChars)
    {
        var redacted = RedactSensitiveText(value);
        if (redacted.Length <= maxChars)
        {
            return new TextPreview(redacted, false);
        }

        return new TextPreview(redacted[..maxChars].TrimEnd() + "...", true);
    }

    private static IReadOnlyDictionary<string, double>? SanitizeScoreComponents(
        IReadOnlyDictionary<string, double>? scoreComponents)
    {
        if (scoreComponents is null)
        {
            return null;
        }

        var sanitized = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (key, value) in scoreComponents)
        {
            sanitized[RedactSensitiveIdentifier(key)] = value;
        }

        return sanitized;
    }

    private readonly record struct TextPreview(string Text, bool Truncated);
}
