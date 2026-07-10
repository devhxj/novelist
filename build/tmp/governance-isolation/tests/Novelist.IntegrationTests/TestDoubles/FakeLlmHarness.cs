using System.Runtime.CompilerServices;
using System.Text.Json;
using Novelist.Core.App;

namespace Novelist.IntegrationTests.TestDoubles;

public sealed class FakeLlmHarness : IChatCompletionClient
{
    public const string DefaultScenario = "default";

    private readonly object _gate = new();
    private readonly Dictionary<ResponseKey, FakeLlmResponse> _responses = [];
    private readonly List<FakeLlmCall> _calls = [];

    public Func<ChatCompletionRequest, string> PromptKeySelector { get; set; } = InferPromptKey;

    public Func<ChatCompletionRequest, string> ScenarioSelector { get; set; } = InferScenario;

    public int CallCount
    {
        get
        {
            lock (_gate)
            {
                return _calls.Count;
            }
        }
    }

    public IReadOnlyList<FakeLlmCall> Calls
    {
        get
        {
            lock (_gate)
            {
                return _calls.ToArray();
            }
        }
    }

    public FakeLlmHarness RespondWithText(
        string promptKey,
        string response,
        string scenario = DefaultScenario)
    {
        SetResponse(promptKey, scenario, FakeLlmResponseKind.Text, response);
        return this;
    }

    public FakeLlmHarness RespondWithJson(
        string promptKey,
        string responseJson,
        string scenario = DefaultScenario)
    {
        using var _ = JsonDocument.Parse(responseJson);
        SetResponse(promptKey, scenario, FakeLlmResponseKind.Json, responseJson);
        return this;
    }

    public FakeLlmHarness RespondWithJson(
        string promptKey,
        JsonElement responseJson,
        string scenario = DefaultScenario)
    {
        SetResponse(promptKey, scenario, FakeLlmResponseKind.Json, responseJson.GetRawText());
        return this;
    }

    public int GetCallCount(string promptKey, string scenario = DefaultScenario)
    {
        var normalizedPromptKey = NormalizeRequired(promptKey, nameof(promptKey));
        var normalizedScenario = NormalizeScenario(scenario);

        lock (_gate)
        {
            return _calls.Count(call =>
                string.Equals(call.PromptKey, normalizedPromptKey, StringComparison.Ordinal) &&
                string.Equals(call.Scenario, normalizedScenario, StringComparison.Ordinal));
        }
    }

    public ValueTask<string> GenerateTextAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var promptKey = NormalizeRequired(PromptKeySelector(request), "prompt key");
        var scenario = NormalizeScenario(ScenarioSelector(request));
        var response = GetResponse(promptKey, scenario);
        RecordCall(promptKey, scenario, request, response);
        if (response is null)
        {
            throw new InvalidOperationException(BuildMissingResponseMessage(promptKey, scenario));
        }

        return ValueTask.FromResult(response.Content);
    }

    public async IAsyncEnumerable<ChatCompletionStreamEvent> StreamChatAsync(
        ChatCompletionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await GenerateTextAsync(request, cancellationToken);
        yield return new ChatCompletionStreamEvent(ChatCompletionStreamEventKind.Content, response);
    }

    private void SetResponse(
        string promptKey,
        string scenario,
        FakeLlmResponseKind kind,
        string content)
    {
        var normalizedPromptKey = NormalizeRequired(promptKey, nameof(promptKey));
        var normalizedScenario = NormalizeScenario(scenario);
        ArgumentNullException.ThrowIfNull(content);

        lock (_gate)
        {
            _responses[new ResponseKey(normalizedPromptKey, normalizedScenario)] = new FakeLlmResponse(
                normalizedPromptKey,
                normalizedScenario,
                kind,
                content);
        }
    }

    private FakeLlmResponse? GetResponse(string promptKey, string scenario)
    {
        lock (_gate)
        {
            _responses.TryGetValue(new ResponseKey(promptKey, scenario), out var response);
            return response;
        }
    }

    private void RecordCall(
        string promptKey,
        string scenario,
        ChatCompletionRequest request,
        FakeLlmResponse? response)
    {
        lock (_gate)
        {
            _calls.Add(new FakeLlmCall(
                _calls.Count + 1,
                promptKey,
                scenario,
                request,
                response?.Kind,
                response?.Content ?? string.Empty,
                response is not null));
        }
    }

    private string BuildMissingResponseMessage(string promptKey, string scenario)
    {
        FakeLlmResponse[] configured;
        lock (_gate)
        {
            configured = _responses.Values
                .OrderBy(response => response.PromptKey, StringComparer.Ordinal)
                .ThenBy(response => response.Scenario, StringComparer.Ordinal)
                .ToArray();
        }

        var available = configured.Length == 0
            ? "<none>"
            : string.Join(", ", configured.Select(response => $"{response.PromptKey}/{response.Scenario}"));
        return $"No fake LLM response is configured for prompt key '{promptKey}' and scenario '{scenario}'. Available: {available}.";
    }

    private static string InferPromptKey(ChatCompletionRequest request)
    {
        var promptKey = TryReadMetadata(request, "prompt_key", "promptKey");
        if (!string.IsNullOrWhiteSpace(promptKey))
        {
            return promptKey;
        }

        throw new InvalidOperationException(
            "FakeLlmHarness could not infer a prompt key. Include prompt_key in a JSON message or set PromptKeySelector.");
    }

    private static string InferScenario(ChatCompletionRequest request)
    {
        return TryReadMetadata(request, "scenario", "scenario_key", "scenarioKey") ?? DefaultScenario;
    }

    private static string? TryReadMetadata(ChatCompletionRequest request, params string[] propertyNames)
    {
        for (var index = request.Messages.Count - 1; index >= 0; index--)
        {
            var content = request.Messages[index].Content;
            var fromJson = TryReadJsonMetadata(content, propertyNames);
            if (!string.IsNullOrWhiteSpace(fromJson))
            {
                return fromJson;
            }

            var fromLines = TryReadLineMetadata(content, propertyNames);
            if (!string.IsNullOrWhiteSpace(fromLines))
            {
                return fromLines;
            }
        }

        return null;
    }

    private static string? TryReadJsonMetadata(string content, IReadOnlyList<string> propertyNames)
    {
        var trimmed = UnwrapCodeFence(content.Trim());
        if (!trimmed.StartsWith('{') && !trimmed.StartsWith('['))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            return TryReadJsonMetadata(document.RootElement, propertyNames);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryReadJsonMetadata(JsonElement element, IReadOnlyList<string> propertyNames)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        if (element.TryGetProperty("metadata", out var metadata))
        {
            return TryReadJsonMetadata(metadata, propertyNames);
        }

        return null;
    }

    private static string? TryReadLineMetadata(string content, IReadOnlyList<string> propertyNames)
    {
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.Trim();
            foreach (var propertyName in propertyNames)
            {
                if (!trimmed.StartsWith(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = trimmed[propertyName.Length..].TrimStart();
                if (value.Length == 0 || (value[0] != ':' && value[0] != '='))
                {
                    continue;
                }

                return value[1..].Trim().Trim('"', '\'');
            }
        }

        return null;
    }

    private static string UnwrapCodeFence(string content)
    {
        if (!content.StartsWith("```", StringComparison.Ordinal))
        {
            return content;
        }

        var firstLineBreak = content.IndexOf('\n', StringComparison.Ordinal);
        var lastFence = content.LastIndexOf("```", StringComparison.Ordinal);
        if (firstLineBreak < 0 || lastFence <= firstLineBreak)
        {
            return content;
        }

        return content[(firstLineBreak + 1)..lastFence].Trim();
    }

    private static string NormalizeRequired(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", name);
        }

        return value.Trim();
    }

    private static string NormalizeScenario(string? scenario)
    {
        return string.IsNullOrWhiteSpace(scenario) ? DefaultScenario : scenario.Trim();
    }

    private readonly record struct ResponseKey(string PromptKey, string Scenario);
}

public enum FakeLlmResponseKind
{
    Text,
    Json
}

public sealed record FakeLlmResponse(
    string PromptKey,
    string Scenario,
    FakeLlmResponseKind Kind,
    string Content);

public sealed record FakeLlmCall(
    int Index,
    string PromptKey,
    string Scenario,
    ChatCompletionRequest Request,
    FakeLlmResponseKind? ResponseKind,
    string Response,
    bool Matched);
