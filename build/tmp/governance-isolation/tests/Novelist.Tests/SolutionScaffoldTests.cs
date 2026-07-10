using Novelist.Agent;
using Novelist.Contracts;
using Novelist.Core;

namespace Novelist.Tests;

public sealed class SolutionScaffoldTests
{
    [Fact]
    public void CoreProjectsUseNovelistAssemblyNames()
    {
        Assert.Equal("Novelist.Contracts", typeof(ContractsAssembly).Assembly.GetName().Name);
        Assert.Equal("Novelist.Core", typeof(CoreAssembly).Assembly.GetName().Name);
        Assert.Equal("Novelist.Agent", typeof(AgentAssembly).Assembly.GetName().Name);
    }
}
