namespace Novelist.Agent;

public sealed record NovelistMafToolContext(
    long NovelId,
    string SessionId = "",
    int TurnId = 0,
    string ToolId = "");
