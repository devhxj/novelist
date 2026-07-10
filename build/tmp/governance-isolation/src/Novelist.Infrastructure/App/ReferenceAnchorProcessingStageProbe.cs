namespace Novelist.Infrastructure.App;

internal interface IReferenceAnchorProcessingStageProbe
{
    void BeforeStage(string stage, long anchorId, string sourceFileHash);
}

internal sealed class NoopReferenceAnchorProcessingStageProbe : IReferenceAnchorProcessingStageProbe
{
    public static NoopReferenceAnchorProcessingStageProbe Instance { get; } = new();

    private NoopReferenceAnchorProcessingStageProbe()
    {
    }

    public void BeforeStage(string stage, long anchorId, string sourceFileHash)
    {
    }
}
