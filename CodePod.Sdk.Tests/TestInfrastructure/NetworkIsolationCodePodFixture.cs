using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class NetworkIsolationCodePodFixture : CodePodFixtureBase
{
    protected override CodePodConfig CreateConfig(CodePodTestSettings settings)
    {
        CodePodConfig config = CodePodTestSupport.CreateDefaultConfig(settings);
        config.PrewarmCount = 0;
        config.SessionTimeoutSeconds = 300;
        config.LabelPrefix = "codepod-network-test";
        config.DefaultNetworkMode = NetworkMode.None;
        return config;
    }
}
