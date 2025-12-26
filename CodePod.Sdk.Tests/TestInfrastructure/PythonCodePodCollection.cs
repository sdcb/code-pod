using Xunit;

namespace CodePod.Sdk.Tests.TestInfrastructure;

[CollectionDefinition(Name, DisableParallelization = false)]
public sealed class PythonCodePodCollection : ICollectionFixture<PythonCodePodFixture>
{
    public const string Name = "CodePod.Python";
}
