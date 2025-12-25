using CodePod.Sdk.Configuration;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class SessionTimeoutCodePodFixture : CodePodFixtureBase
{
    protected override CodePodConfig CreateConfig(CodePodTestSettings settings)
    {
        CodePodConfig config = CodePodTestSupport.CreateDefaultConfig(settings);
        config.SessionTimeoutSeconds = 60;
        config.LabelPrefix = "codepod-timeout-test";
        return config;
    }
}
