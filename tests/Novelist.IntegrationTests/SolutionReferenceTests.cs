using Novelist.App;
using Novelist.Infrastructure;

namespace Novelist.IntegrationTests;

public sealed class SolutionReferenceTests
{
    [Fact]
    public void IntegrationTestProjectCanReferenceAppAndInfrastructure()
    {
        Assert.Equal("Novelist.App", typeof(AppAssembly).Assembly.GetName().Name);
        Assert.Equal("Novelist.Infrastructure", typeof(InfrastructureAssembly).Assembly.GetName().Name);
    }
}
