using System.Text;
using System.Text.Json;
using Novelist.Core.App;

namespace Novelist.Infrastructure.App;

public sealed class ReferenceChapterSplitChatCompletionAnalyzer : IReferenceChapterSplitAnalyzer
{
    private const int MaxOutputChars = 16 * 1024;
    private const int MaxOutputTokens = 1_024;
    private readonly IAppSettingsService _settings;
    private readonly IChatCompletionClient _completion;

    public ReferenceChapterSplitChatCompletionAnalyzer(
        IAppSettingsService settings,
        IChatCompletionClient completion)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
    }

    public async ValueTask<ReferenceChapterSplitModelResult> AnalyzeAsync(
        ReferenceChapterSplitModelRequest input,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (string.IsNullOrWhiteSpace(input.NormalizedSample))
        {
            throw new ArgumentException("Chapter split analysis requires a non-empty normalized sample.", nameof(input));
        }

        var model = await ResolveSelectedModelAsync(cancellationToken)
            ?? throw new InvalidOperationException("Chapter split analysis requires a selected model.");
        var request = new ChatCompletionRequest(
            model.ProviderName,
            model.ModelId,
            model.ReasoningEffort,
            [
                new ChatCompletionMessage("system", """
                    Identify the chapter-heading convention in the supplied fiction source sample.
                    Return strict JSON only with this exact shape:
                    {"pattern_kind":"markdown_heading|chapter_template","delimiter_template":"...","confidence":0.0,"evidence_offsets":[0]}

                    Rules:
                    - pattern_kind is markdown_heading only for Markdown heading lines, otherwise chapter_template.
                    - markdown_heading must use delimiter_template "# {title}".
                    - chapter_template may use only literal characters plus {number} and {title}; it must match a complete heading line.
                    - evidence_offsets are zero-based offsets inside normalized_source_sample.
                    - Do not return source text, rewritten text, Markdown, explanations, paths, URLs, or any additional fields.
                    """),
                new ChatCompletionMessage("user", JsonSerializer.Serialize(new
                {
                    normalized_source_sample = input.NormalizedSample
                }))
            ],
            MaxOutputTokens: MaxOutputTokens);

        var response = new StringBuilder();
        await foreach (var item in _completion.StreamChatAsync(request, cancellationToken))
        {
            if (item.Kind != ChatCompletionStreamEventKind.Content || string.IsNullOrEmpty(item.Data))
            {
                continue;
            }

            if (response.Length + item.Data.Length > MaxOutputChars)
            {
                throw new InvalidOperationException("Chapter split analysis response is too large.");
            }

            response.Append(item.Data);
        }

        return ParseResponse(response.ToString(), input.NormalizedSample.Length, model);
    }

    private static ReferenceChapterSplitModelResult ParseResponse(
        string response,
        int sampleLength,
        SelectedModel model)
    {
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var allowedProperties = new HashSet<string>(StringComparer.Ordinal)
            {
                "pattern_kind",
                "delimiter_template",
                "confidence",
                "evidence_offsets"
            };
            if (root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Any(property => !allowedProperties.Contains(property.Name)))
            {
                throw new InvalidOperationException("Chapter split analysis returned invalid structured output.");
            }
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("pattern_kind", out var kindElement) ||
                kindElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("delimiter_template", out var templateElement) ||
                templateElement.ValueKind != JsonValueKind.String ||
                !root.TryGetProperty("confidence", out var confidenceElement) ||
                confidenceElement.ValueKind != JsonValueKind.Number ||
                !confidenceElement.TryGetDouble(out var confidence) ||
                double.IsNaN(confidence) ||
                double.IsInfinity(confidence) ||
                confidence < 0 || confidence > 1 ||
                !root.TryGetProperty("evidence_offsets", out var evidenceElement) ||
                evidenceElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException("Chapter split analysis returned invalid structured output.");
            }

            var kind = kindElement.GetString()?.Trim() ?? string.Empty;
            if (kind is not ("markdown_heading" or "chapter_template"))
            {
                throw new InvalidOperationException("Chapter split analysis returned an unsupported pattern kind.");
            }

            var template = templateElement.GetString()?.Trim() ?? string.Empty;
            if (template.Length == 0 || template.Length > 160)
            {
                throw new InvalidOperationException("Chapter split analysis returned an invalid delimiter template.");
            }

            if (kind == "markdown_heading" && !string.Equals(template, "# {title}", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Chapter split analysis returned an invalid Markdown heading template.");
            }

            var offsets = new List<int>();
            foreach (var item in evidenceElement.EnumerateArray())
            {
                if (!item.TryGetInt32(out var offset) || offset < 0 || offset >= sampleLength)
                {
                    throw new InvalidOperationException("Chapter split analysis returned invalid evidence offsets.");
                }

                offsets.Add(offset);
            }

            if (offsets.Count == 0)
            {
                throw new InvalidOperationException("Chapter split analysis returned no evidence offsets.");
            }

            return new ReferenceChapterSplitModelResult(
                kind,
                template,
                confidence,
                offsets.Distinct().OrderBy(offset => offset).ToArray(),
                model.ProviderName,
                model.ModelId);
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Chapter split analysis returned invalid structured output.", exception);
        }
    }

    private async ValueTask<SelectedModel?> ResolveSelectedModelAsync(CancellationToken cancellationToken)
    {
        var settings = await _settings.GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.SelectedModelKey))
        {
            return null;
        }

        var parts = settings.SelectedModelKey.Split('/', 2, StringSplitOptions.None);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            return null;
        }

        return new SelectedModel(parts[0].Trim().ToLowerInvariant(), parts[1].Trim(), settings.ReasoningEffort ?? string.Empty);
    }

    private sealed record SelectedModel(string ProviderName, string ModelId, string ReasoningEffort);
}
