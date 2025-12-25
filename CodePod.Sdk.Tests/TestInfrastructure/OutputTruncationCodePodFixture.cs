using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public sealed class OutputTruncationCodePodFixture : CodePodFixtureBase
{
    protected override CodePodConfig CreateConfig(CodePodTestSettings settings)
    {
        CodePodConfig config = CodePodTestSupport.CreateDefaultConfig(settings);
        config.PrewarmCount = 2;
        config.SessionTimeoutSeconds = 300;
        config.LabelPrefix = "codepod-truncation-test";
        config.OutputOptions = new OutputOptions
        {
            MaxOutputBytes = 1024,
            Strategy = TruncationStrategy.HeadAndTail,
            TruncationMessage = "\n... [{0} bytes truncated] ...\n"
        };
        return config;
    }
}
