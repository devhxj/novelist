using Novelist.Contracts.App;
using Novelist.Core.App;
using Novelist.Infrastructure.App;

namespace Novelist.IntegrationTests;

public sealed class ReferenceMaterializationBlueprintPreviewServiceTests : IDisposable
{
    private const string MultiParagraphText = "First line.\n\nSecond line.";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "novelist-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateUsesActiveMaterialSearchAndDoesNotPersistPreviewState()
    {
        var search = new FakeSearch(new Dictionary<long, IReadOnlyList<ReferenceMaterialSearchHit>>
        {
            [11] =
            [
                Hit("material-1", "generation-a", 11, "dialogue", MultiParagraphText, 0.1),
                Hit("material-2", "generation-a", 11, "hook", "A final knock.", 0.2)
            ]
        });
        var service = new ReferenceMaterializationBlueprintPreviewService(search);

        var preview = await service.GenerateAsync(
            new GenerateReferenceMaterializationBlueprintPreviewPayload(
                7,
                [11],
                "Escalate the conflict.",
                2),
            CancellationToken.None);

        var request = Assert.Single(search.Requests);
        Assert.Equal([11], request.AnchorIds);
        Assert.Equal("Escalate the conflict.", request.Query);
        Assert.Equal("generation-a", Assert.Single(preview.Sources).GenerationId);
        Assert.Equal(2, preview.Candidates.Count);
        var links = preview.Candidates
            .SelectMany(candidate => candidate.Beats)
            .SelectMany(beat => beat.Materials)
            .ToArray();
        Assert.Contains(links, link => link.Text == MultiParagraphText);
        Assert.All(links, link => Assert.Equal("generation-a", link.GenerationId));
    }

    [Fact]
    public async Task GeneratePropagatesMissingActiveGenerationFailure()
    {
        var search = new ThrowingSearch(new ReferenceMaterializationException(
            ReferenceMaterializationErrorCodes.GenerationIncomplete,
            "No active generation."));
        var service = new ReferenceMaterializationBlueprintPreviewService(search);

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.GenerateAsync(
                new GenerateReferenceMaterializationBlueprintPreviewPayload(
                    7,
                    [11],
                    "Escalate the conflict."),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.GenerationIncomplete, exception.ErrorCode);
    }

    [Fact]
    public async Task GenerateRejectsAnEmptyVectorResultWithoutFallback()
    {
        var service = new ReferenceMaterializationBlueprintPreviewService(
            new FakeSearch(new Dictionary<long, IReadOnlyList<ReferenceMaterialSearchHit>> { [11] = [] }));

        var exception = await Assert.ThrowsAsync<ReferenceMaterializationException>(async () =>
            await service.GenerateAsync(
                new GenerateReferenceMaterializationBlueprintPreviewPayload(
                    7,
                    [11],
                    "Escalate the conflict."),
                CancellationToken.None));

        Assert.Equal(ReferenceMaterializationErrorCodes.BlueprintNoRelevantMaterial, exception.ErrorCode);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private static ReferenceMaterialSearchHit Hit(
        string materialId,
        string generationId,
        long anchorId,
        string materialType,
        string text,
        double distance) => new(
            materialId,
            generationId,
            anchorId,
            1,
            0,
            materialType,
            text,
            "Useful for the requested beat.",
            [materialType],
            "text-hash",
            distance);

    private sealed class FakeSearch(
        IReadOnlyDictionary<long, IReadOnlyList<ReferenceMaterialSearchHit>> hitsByAnchor)
        : IReferenceMaterialSearch
    {
        public List<ReferenceMaterialSearchRequest> Requests { get; } = [];

        public ValueTask<ReferenceMaterialListPage> ListAsync(
            ReferenceMaterialListRequest input,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("This test search only supports vector queries.");

        public ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchAsync(
            ReferenceMaterialSearchRequest input,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(input);
            var anchorId = Assert.Single(input.AnchorIds ?? []);
            return ValueTask.FromResult(hitsByAnchor.TryGetValue(anchorId, out var hits) ? hits : []);
        }
    }

    private sealed class ThrowingSearch(Exception exception) : IReferenceMaterialSearch
    {
        public ValueTask<ReferenceMaterialListPage> ListAsync(
            ReferenceMaterialListRequest input,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<ReferenceMaterialListPage>(exception);

        public ValueTask<IReadOnlyList<ReferenceMaterialSearchHit>> SearchAsync(
            ReferenceMaterialSearchRequest input,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IReadOnlyList<ReferenceMaterialSearchHit>>(exception);
    }
}
