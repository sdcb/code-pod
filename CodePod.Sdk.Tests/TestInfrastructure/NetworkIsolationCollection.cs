using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class NetworkIsolationCollection : ICollectionFixture<NetworkIsolationCodePodFixture>
{
    public const string Name = "CodePod.NetworkIsolation";
}
