using Novelist.Core.App;

namespace Novelist.Tests;

public sealed class ReferenceCorpusTechniqueSpecimenIdentityTests
{
 [Fact]
 public void CreateIsDeterministicAndSensitiveToEveryGenerationKeyPart()
 {
 var first = ReferenceCorpusTechniqueSpecimenIdentity.Create("run-1", "node-1", "action_as_emotion");
 var repeated = ReferenceCorpusTechniqueSpecimenIdentity.Create("run-1", "node-1", "action_as_emotion");

 Assert.Equal(first, repeated);
 Assert.StartsWith("spec_", first.SpecimenId, StringComparison.Ordinal);
 Assert.Equal(69, first.SpecimenId.Length);
 Assert.NotEqual(first.SpecimenId, ReferenceCorpusTechniqueSpecimenIdentity.Create("run-2", "node-1", "action_as_emotion").SpecimenId);
 Assert.NotEqual(first.SpecimenId, ReferenceCorpusTechniqueSpecimenIdentity.Create("run-1", "node-2", "action_as_emotion").SpecimenId);
 Assert.NotEqual(first.SpecimenId, ReferenceCorpusTechniqueSpecimenIdentity.Create("run-1", "node-1", "silence").SpecimenId);
 }

 [Theory]
 [InlineData("")]
 [InlineData(" action_as_emotion")]
 [InlineData("action_as_emotion ")]
 public void CreateRejectsNonCanonicalGenerationKeyParts(string family)
 {
 Assert.Throws<ArgumentException>(() =>
 ReferenceCorpusTechniqueSpecimenIdentity.Create("run-1", "node-1", family));
 }
}
