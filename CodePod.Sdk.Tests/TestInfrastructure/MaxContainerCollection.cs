using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MaxContainerCollection : ICollectionFixture<MaxContainerCodePodFixture>
{
    public const string Name = "CodePod.MaxContainers";
}
