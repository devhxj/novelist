using System.Text.Json.Serialization;

namespace Novelist.Core.App;

public interface ISubagentRunner
{
    ValueTask<SubagentRunResult> RunAsync(
        SubagentRunRequest request,
        CancellationToken cancellationToken);
}

public sealed record SubagentRunRequest(
    long NovelId,
    string SessionId,
    int TurnId,
    string ToolId,
    string AgentType,
    string Instruction,
    string ProviderName,
    string ModelId,
    string ReasoningEffort,
    int StartSequence);

public sealed record SubagentRunResult(
    [property: JsonPropertyName("agent_type")] string AgentType,
    [property: JsonPropertyName("report")] string Report,
    [property: JsonIgnore] int LastSequence = 0) : ISequencedChatToolResult;
