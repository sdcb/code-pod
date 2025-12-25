using CodePod.Sdk.Configuration;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class MaxContainerCodePodFixture : CodePodFixtureBase
{
    protected override CodePodConfig CreateConfig(CodePodTestSettings settings)
    {
        CodePodConfig config = CodePodTestSupport.CreateDefaultConfig(settings);
        config.PrewarmCount = 0;
        config.MaxContainers = 3;
        config.SessionTimeoutSeconds = 300;
        config.LabelPrefix = "codepod-maxtest";
        return config;
    }
}
