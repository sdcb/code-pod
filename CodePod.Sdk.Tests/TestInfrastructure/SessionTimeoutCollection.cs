using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SessionTimeoutCollection : ICollectionFixture<SessionTimeoutCodePodFixture>
{
    public const string Name = "CodePod.SessionTimeout";
}
