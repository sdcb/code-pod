using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class MaxContainerCollection : ICollectionFixture<MaxContainerCodePodFixture>
{
    public const string Name = "CodePod.MaxContainers";
}
