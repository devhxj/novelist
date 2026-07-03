namespace Novelist.Agent;

public sealed record NovelistMafToolContext(
    long NovelId,
    string SessionId = "",
    int TurnId = 0,
    string ToolId = "",
    string ProviderName = "",
    string ModelId = "",
    string ReasoningEffort = "",
    int CurrentSequence = 0,
    string RawArgumentsJson = "");
