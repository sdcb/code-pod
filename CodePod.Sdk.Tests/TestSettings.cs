using Microsoft.Extensions.Configuration;

namespace CodePod.Sdk.Tests;

public sealed class CodePodTestSettings
{
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";

    public bool IsWindowsContainer { get; set; } = false;

    public string DotnetSdkLinuxImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0";

    public string DotnetSdkWindowsImage { get; set; } = "mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022";

    public string PythonLinuxImage { get; set; } = "python:3.12-slim";

    public string PythonWindowsImage { get; set; } = "python:3.15.0a3-windowsservercore-ltsc2022";
}

public static class TestSettings
{
    private static readonly object LockObj = new();
    private static CodePodTestSettings? Cached;

    public static CodePodTestSettings Load()
    {
        lock (LockObj)
        {
            if (Cached != null)
            {
                return Cached;
            }

            IConfigurationRoot configuration = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .AddEnvironmentVariables(prefix: "CODEPOD_")
                .Build();

            CodePodTestSettings settings = new();
            configuration.GetSection("CodePodTest").Bind(settings);

            Cached = settings;
            return settings;
        }
    }
}
