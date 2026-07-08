using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class StyleSkillDocumentTests
{
    [Fact]
    public void ParseStrictAcceptsRequiredFrontmatterAndBody()
    {
        var skill = StyleSkillDocument.ParseStrict(
            """
            ---
            name: 雨夜克制
            description: 适合雨夜悬疑场景。
            category: 风格仿写
            mode: auto
            author: ai
            version: 1
            ---
            # 雨夜克制

            ## 仿写要点
            - 短句推进。
            """);

        Assert.Equal("雨夜克制", skill.Name);
        Assert.Equal("适合雨夜悬疑场景。", skill.Description);
        Assert.Equal("风格仿写", skill.Category);
        Assert.Equal("auto", skill.Mode);
        Assert.Equal("ai", skill.Author);
        Assert.Equal(1, skill.Version);
        Assert.Contains("短句推进", skill.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseStrictRejectsMissingRequiredFields()
    {
        var error = Assert.Throws<StyleSkillValidationException>(() =>
            StyleSkillDocument.ParseStrict(
                """
                ---
                name: 缺字段
                description: 少字段。
                mode: auto
                ---
                # 缺字段
                """));

        Assert.Contains("category", error.Message, StringComparison.Ordinal);
        Assert.Contains("author", error.Message, StringComparison.Ordinal);
        Assert.Contains("version", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hidden", 1, "mode")]
    [InlineData("auto", 0, "version")]
    public void ParseStrictRejectsInvalidModeAndVersion(string mode, int version, string expectedMessage)
    {
        var error = Assert.Throws<StyleSkillValidationException>(() =>
            StyleSkillDocument.ParseStrict(
                $$"""
                ---
                name: bad
                description: bad
                category: 风格仿写
                mode: {{mode}}
                author: ai
                version: {{version}}
                ---
                # bad
                """));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("bad:name")]
    [InlineData("bad|name")]
    public void NormalizeSkillNameRejectsUnsafeFilenames(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => StyleSkillDocument.NormalizeSkillName(name));
    }
}
