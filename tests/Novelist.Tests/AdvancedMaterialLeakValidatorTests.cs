using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class AdvancedMaterialLeakValidatorTests
{
    private static readonly string[] Source = ["他推门而入，屋里安静得能听见雨声。"];

    [Fact]
    public void DetectsVerbatimRunFromSource()
    {
        var violations = AdvancedMaterialLeakValidator.Validate(
            new Dictionary<string, string?> { ["transfer_template"] = "他推门而入，屋里安静得能听见雨声。" },
            Source);

        var violation = Assert.Single(violations);
        Assert.Equal("transfer_template", violation.Field);
        Assert.True(violation.Run.Length >= AdvancedMaterialLeakValidator.MaxVerbatimRunLength);
    }

    [Fact]
    public void ParaphraseWithPlaceholdersPasses()
    {
        var violations = AdvancedMaterialLeakValidator.Validate(
            new Dictionary<string, string?> { ["transfer_template"] = "主体[动作]后接[环境回响]，压低信息投放。" },
            Source);

        Assert.Empty(violations);
    }

    [Fact]
    public void IgnoresWhitespaceWhenComparing()
    {
        var violations = AdvancedMaterialLeakValidator.Validate(
            new Dictionary<string, string?> { ["abstract"] = "他推门而入 ， 屋里安静" },
            Source);

        Assert.NotEmpty(violations);
    }

    [Fact]
    public void SkipsShortFieldsAndEmptySources()
    {
        Assert.Empty(AdvancedMaterialLeakValidator.Validate(
            new Dictionary<string, string?> { ["value"] = "drip_fed" },
            Source));
        Assert.Empty(AdvancedMaterialLeakValidator.Validate(
            new Dictionary<string, string?> { ["abstract"] = "他推门而入，屋里安静得能听见雨声。" },
            []));
    }
}
