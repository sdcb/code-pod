using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class SessionTimeoutCollection : ICollectionFixture<SessionTimeoutCodePodFixture>
{
    public const string Name = "CodePod.SessionTimeout";
}
