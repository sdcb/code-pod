using CodePod.Sdk.Configuration;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class CodePodFixture : CodePodFixtureBase
{
    protected override CodePodConfig CreateConfig(CodePodTestSettings settings) =>
        CodePodTestSupport.CreateDefaultConfig(settings);
}
