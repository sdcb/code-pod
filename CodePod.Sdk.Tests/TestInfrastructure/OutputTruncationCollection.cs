using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class OutputTruncationCollection : ICollectionFixture<OutputTruncationCodePodFixture>
{
    public const string Name = "CodePod.OutputTruncation";
}
