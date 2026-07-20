using System.Text.Json;
using Novelist.Contracts.App;
using Novelist.Contracts.Bridge;

namespace Novelist.Tests;

public sealed class ReferenceSourceContractTests
{
    [Fact]
    public void SourcePayloadsUseStableSnakeCaseNames()
    {
        var input = new CreateReferenceAnchorPayload(
            42,
            "Reference",
            "Author",
            @"D:\books\reference.md",
            "markdown",
            "user_provided");
        var json = JsonSerializer.Serialize(input, BridgeJson.SerializerOptions);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(42, document.RootElement.GetProperty("novel_id").GetInt64());
        Assert.Equal(@"D:\books\reference.md", document.RootElement.GetProperty("source_path").GetString());
        Assert.Equal("markdown", document.RootElement.GetProperty("source_kind").GetString());
        Assert.Equal("user_provided", document.RootElement.GetProperty("license_status").GetString());
    }

    [Fact]
    public void AnchorPayloadKeepsOwnershipMetadataExplicit()
    {
        var anchor = new ReferenceAnchorPayload(
            7,
            42,
            "Reference",
            "Author",
            string.Empty,
            "markdown",
            "user_provided",
            "source-hash",
            "whole-chapter-v1",
            ReferenceAnchorBuildStates.PendingSplit,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            ReferenceCorpusVisibilities.Private,
            ReferenceSourceTrustLevels.UserVerified,
            ["dialogue"]);
        var json = JsonSerializer.Serialize(anchor, BridgeJson.SerializerOptions);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(ReferenceAnchorOwnerScopes.Novel, document.RootElement.GetProperty("owner_scope").GetString());
        Assert.Equal(42, document.RootElement.GetProperty("owner_novel_id").GetInt64());
        Assert.Equal(ReferenceAnchorBuildStates.PendingSplit, document.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public void SourceBuildStatesContainOnlyTheCurrentWorkflow()
    {
        Assert.Equal(
            [
                ReferenceAnchorBuildStates.PendingSplit,
                ReferenceAnchorBuildStates.PendingMaterialization,
                ReferenceAnchorBuildStates.Ready
            ],
            ReferenceAnchorBuildStates.All);
    }
}
