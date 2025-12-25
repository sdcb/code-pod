using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;

namespace CodePod.Sdk.Tests.TestInfrastructure;

public static class CodePodTestSupport
{
    public static CodePodConfig CreateDefaultConfig(CodePodTestSettings settings)
    {
        var isWindowsContainer = settings.IsWindowsContainer;

        return new CodePodConfig
        {
            IsWindowsContainer = isWindowsContainer,
            DockerEndpoint = settings.DockerEndpoint,
            Image = isWindowsContainer ? settings.DotnetSdkWindowsImage : settings.DotnetSdkLinuxImage,
            PrewarmCount = 1,
            MaxContainers = 10,
            SessionTimeoutSeconds = 1800,
            WorkDir = isWindowsContainer ? "C:\\app" : "/app",
            LabelPrefix = "codepod-test"
        };
    }

    public static async Task<Models.SessionInfo> WaitForSessionReadyAsync(CodePodClient client, int sessionId, int maxWaitSeconds = 30)
    {
        for (int i = 0; i < maxWaitSeconds * 2; i++)
        {
            SessionInfo session = await client.GetSessionAsync(sessionId);
            if (!string.IsNullOrEmpty(session.ContainerId))
            {
                return session;
            }
            await Task.Delay(500);
        }

        throw new TimeoutException($"Session {sessionId} did not become ready within {maxWaitSeconds} seconds");
    }

    public static string GetWorkPath(CodePodConfig config, string relativePath)
    {
        var rel = relativePath.TrimStart('/', '\\');

        if (config.IsWindowsContainer)
        {
            rel = rel.Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(rel))
            {
                return config.WorkDir;
            }

            return config.WorkDir.EndsWith("\\", StringComparison.Ordinal)
                ? config.WorkDir + rel
                : config.WorkDir + "\\" + rel;
        }

        rel = rel.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(rel))
        {
            return config.WorkDir;
        }

        return config.WorkDir.EndsWith("/", StringComparison.Ordinal)
            ? config.WorkDir + rel
            : config.WorkDir + "/" + rel;
    }

    public static string GetEchoCommand(bool isWindowsContainer, string message) =>
        isWindowsContainer ? $"Write-Output '{message}'" : $"echo '{message}'";

    public static string GetMultiLineEchoCommand(bool isWindowsContainer, int lineCount)
    {
        if (isWindowsContainer)
        {
            return $"1..{lineCount} | ForEach-Object {{ Write-Output \"Line $_\" }}";
        }

        return $"for i in $(seq 1 {lineCount}); do echo \"Line $i\"; done";
    }

    public static string GetStreamingOutputCommand(bool isWindowsContainer, int lineCount, double delaySeconds = 0.1)
    {
        if (isWindowsContainer)
        {
            var delayMs = (int)(delaySeconds * 1000);
            return $"1..{lineCount} | ForEach-Object {{ Write-Output \"stdout: Line $_\"; Write-Error \"stderr: Warning $_\"; Start-Sleep -Milliseconds {delayMs} }}";
        }

        return $"for i in $(seq 1 {lineCount}); do echo \"stdout: Line $i\"; echo \"stderr: Warning $i\" >&2; sleep {delaySeconds}; done";
    }
}
