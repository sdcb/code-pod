using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class ResourceLimitsCodePodFixture : CodePodFixtureBase
{
    protected override CodePodConfig CreateConfig(CodePodTestSettings settings)
    {
        CodePodConfig config = CodePodTestSupport.CreateDefaultConfig(settings);
        config.PrewarmCount = 0;
        config.SessionTimeoutSeconds = 300;
        config.LabelPrefix = "codepod-reslimit-test";
        config.MaxResourceLimits = new ResourceLimits
        {
            MemoryBytes = 1024 * 1024 * 1024,
            CpuCores = 2.0,
            MaxProcesses = 200
        };
        config.DefaultResourceLimits = ResourceLimits.Standard;
        return config;
    }
}
