using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CodePodCollection : ICollectionFixture<CodePodFixture>
{
    public const string Name = "CodePod";
}
