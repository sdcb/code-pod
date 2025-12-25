using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ResourceLimitsCollection : ICollectionFixture<ResourceLimitsCodePodFixture>
{
    public const string Name = "CodePod.ResourceLimits";
}
