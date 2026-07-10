using Novelist.Contracts.App;
using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class PageRequestNormalizerTests
{
    [Fact]
    public void NormalizeAddsStableTieBreakersAfterRequestedSort()
    {
        var normalized = PageRequestNormalizer.Normalize(
            new PageRequestPayload(
                Cursor: "cursor-1",
                PageSize: 50,
                SortBy: "score",
                SortDir: "DESC",
                Filters: new Dictionary<string, string> { ["feature_family"] = "sensory" }),
            new PageRequestPolicy(
                AllowedSortFields: ["score", "created_at", "candidate_id"],
                DefaultSortBy: "score",
                StableTieBreakers: ["created_at", "candidate_id"]));

        Assert.Equal("cursor-1", normalized.Cursor);
        Assert.Equal(50, normalized.PageSize);
        Assert.Equal("score", normalized.SortBy);
        Assert.Equal("desc", normalized.SortDir);
        Assert.Equal(["score", "created_at", "candidate_id"], normalized.StableSortFields);
        Assert.Equal("sensory", normalized.Filters["feature_family"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void NormalizeRejectsPageSizesOutsidePolicyLimit(int pageSize)
    {
        var exception = Assert.Throws<PageRequestValidationException>(() =>
            PageRequestNormalizer.Normalize(
                new PageRequestPayload(null, pageSize, "score", "desc"),
                DefaultPolicy()));

        Assert.Equal(PageRequestErrorCodes.PageSizeOutOfRange, exception.Code);
    }

    [Fact]
    public void NormalizeRejectsUnknownSortFieldsWithExplicitErrorCode()
    {
        var exception = Assert.Throws<PageRequestValidationException>(() =>
            PageRequestNormalizer.Normalize(
                new PageRequestPayload(null, 20, "raw_sql", "desc"),
                DefaultPolicy()));

        Assert.Equal(PageRequestErrorCodes.InvalidSortField, exception.Code);
    }

    [Fact]
    public void NormalizeRejectsInvalidSortDirectionWithExplicitErrorCode()
    {
        var exception = Assert.Throws<PageRequestValidationException>(() =>
            PageRequestNormalizer.Normalize(
                new PageRequestPayload(null, 20, "score", "sideways"),
                DefaultPolicy()));

        Assert.Equal(PageRequestErrorCodes.InvalidSortDirection, exception.Code);
    }

    [Fact]
    public void NormalizeRejectsInvalidFilterKeysBeforeQueryConstruction()
    {
        var exception = Assert.Throws<PageRequestValidationException>(() =>
            PageRequestNormalizer.Normalize(
                new PageRequestPayload(
                    Cursor: null,
                    PageSize: 20,
                    SortBy: "score",
                    SortDir: "desc",
                    Filters: new Dictionary<string, string> { ["bad filter"] = "x" }),
                DefaultPolicy()));

        Assert.Equal(PageRequestErrorCodes.InvalidFilterKey, exception.Code);
    }

    [Theory]
    [InlineData("cursor with spaces")]
    [InlineData("cursor\nnext")]
    public void NormalizeRejectsInvalidCursorSyntaxBeforeServiceDispatch(string cursor)
    {
        var exception = Assert.Throws<PageRequestValidationException>(() =>
            PageRequestNormalizer.Normalize(
                new PageRequestPayload(cursor, 20, "score", "desc"),
                DefaultPolicy()));

        Assert.Equal(PageRequestErrorCodes.InvalidCursor, exception.Code);
    }

    private static PageRequestPolicy DefaultPolicy()
    {
        return new PageRequestPolicy(
            AllowedSortFields: ["score", "created_at", "candidate_id"],
            DefaultSortBy: "score",
            StableTieBreakers: ["created_at", "candidate_id"]);
    }
}
