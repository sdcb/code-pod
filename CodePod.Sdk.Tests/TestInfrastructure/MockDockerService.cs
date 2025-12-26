using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Services;
using SdkContainerStatus = CodePod.Sdk.Models.ContainerStatus;

namespace CodePod.Sdk.Tests.TestInfrastructure;

/// <summary>
/// Mock DockerService implementation for unit testing.
/// Maintains in-memory state to simulate container lifecycle, command execution,
/// file operations and stats without connecting to a real Docker daemon.
/// </summary>
public class MockDockerService : IDockerService
{
    private readonly CodePodConfig _config;
    private readonly object _lock = new();

    // In-memory state stores
    private readonly Dictionary<string, MockContainer> _containers = new();
    private readonly Dictionary<string, Dictionary<string, byte[]>> _files = new(); // containerId -> (path -> content)
    private readonly Dictionary<string, MockStats> _stats = new();
    private readonly HashSet<string> _availableImages = new();
    private readonly List<CommandMatcher> _commandMatchers = new();

    public MockDockerService(CodePodConfig config)
    {
        _config = config;
        // Default images are always available
        _availableImages.Add(config.Image);
        SetupDefaultCommandMatchers();
    }

    #region Command Matching Setup

    /// <summary>
    /// Register a command matcher for simulating specific command outputs.
    /// </summary>
    public void RegisterCommandMatcher(string pattern, string stdout, string stderr = "", long exitCode = 0, bool isRegex = true)
    {
        lock (_lock)
        {
            _commandMatchers.Add(new CommandMatcher(pattern, stdout, stderr, exitCode, isRegex));
        }
    }

    /// <summary>
    /// Clear all custom command matchers and restore defaults.
    /// </summary>
    public void ClearCommandMatchers()
    {
        lock (_lock)
        {
            _commandMatchers.Clear();
            SetupDefaultCommandMatchers();
        }
    }

    private void SetupDefaultCommandMatchers()
    {
        // Windows echo commands (PowerShell)
            _commandMatchers.Add(new CommandMatcher(@"^\s*Write-Output\s+'([^']*)'\s*$", m => (m.Groups[1].Value + "\n", "", 0), isRegex: true));
            _commandMatchers.Add(new CommandMatcher(@"^\s*Write-Output\s+""([^""]*)""\s*$", m => (m.Groups[1].Value + "\n", "", 0), isRegex: true));

        // Linux echo commands
            _commandMatchers.Add(new CommandMatcher(@"^\s*echo\s+'([^']*)'\s*$", m => (m.Groups[1].Value + "\n", "", 0), isRegex: true));
            _commandMatchers.Add(new CommandMatcher(@"^\s*echo\s+""([^""]*)""\s*$", m => (m.Groups[1].Value + "\n", "", 0), isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"^echo\s+(\S+)$", m => (m.Groups[1].Value + "\n", "", 0), isRegex: true));

        // Multi-line echo (Windows PowerShell)
        _commandMatchers.Add(new CommandMatcher(@"1\.\.(\d+)\s*\|\s*ForEach-Object\s*\{\s*Write-Output\s*""Line \$_""\s*\}", m =>
        {
            int count = int.Parse(m.Groups[1].Value);
            StringBuilder sb = new();
            for (int i = 1; i <= count; i++)
            {
                sb.AppendLine($"Line {i}");
            }
            return (sb.ToString(), "", 0);
        }, isRegex: true));

        // Multi-line echo (Linux)
        _commandMatchers.Add(new CommandMatcher(@"for i in \$\(seq 1 (\d+)\); do echo ""Line \$i""; done", m =>
        {
            int count = int.Parse(m.Groups[1].Value);
            StringBuilder sb = new();
            for (int i = 1; i <= count; i++)
            {
                sb.AppendLine($"Line {i}");
            }
            return (sb.ToString(), "", 0);
        }, isRegex: true));

        // Streaming output (Windows) - simulate stdout and stderr interleaved
        _commandMatchers.Add(new CommandMatcher(@"1\.\.(\d+)\s*\|\s*ForEach-Object\s*\{\s*Write-Output\s*""stdout: Line \$_"";\s*Write-Error\s*""stderr: Warning \$_""", m =>
        {
            int count = int.Parse(m.Groups[1].Value);
            StringBuilder stdout = new();
            StringBuilder stderr = new();
            for (int i = 1; i <= count; i++)
            {
                stdout.AppendLine($"stdout: Line {i}");
                stderr.AppendLine($"stderr: Warning {i}");
            }
            return (stdout.ToString(), stderr.ToString(), 0);
        }, isRegex: true));

        // Streaming output (Linux)
        _commandMatchers.Add(new CommandMatcher(@"for i in \$\(seq 1 (\d+)\); do echo ""stdout: Line \$i""; echo ""stderr: Warning \$i"" >&2", m =>
        {
            int count = int.Parse(m.Groups[1].Value);
            StringBuilder stdout = new();
            StringBuilder stderr = new();
            for (int i = 1; i <= count; i++)
            {
                stdout.AppendLine($"stdout: Line {i}");
                stderr.AppendLine($"stderr: Warning {i}");
            }
            return (stdout.ToString(), stderr.ToString(), 0);
        }, isRegex: true));

        // Large output for truncation tests (Windows)
        _commandMatchers.Add(new CommandMatcher(@"1\.\.(\d+)\s*\|\s*ForEach-Object\s*\{\s*Write-Output\s*""X{100}""\s*\}", m =>
        {
            int count = int.Parse(m.Groups[1].Value);
            StringBuilder sb = new();
            string line = new('X', 100);
            for (int i = 0; i < count; i++)
            {
                sb.AppendLine(line);
            }
            return (sb.ToString(), "", 0);
        }, isRegex: true));

        // Large output for truncation tests (Linux)
        _commandMatchers.Add(new CommandMatcher(@"for i in \$\(seq 1 (\d+)\); do printf 'X%.0s' \{1\.\.100\}; echo; done", m =>
        {
            int count = int.Parse(m.Groups[1].Value);
            StringBuilder sb = new();
            string line = new('X', 100);
            for (int i = 0; i < count; i++)
            {
                sb.AppendLine(line);
            }
            return (sb.ToString(), "", 0);
        }, isRegex: true));

        // Python version
        _commandMatchers.Add(new CommandMatcher("python --version", "Python 3.12.0\n", "", 0, isRegex: false));
        _commandMatchers.Add(new CommandMatcher("python3 --version", "Python 3.12.0\n", "", 0, isRegex: false));

        // .NET version
        _commandMatchers.Add(new CommandMatcher("dotnet --version", "10.0.100\n", "", 0, isRegex: false));

        // Python execution
        _commandMatchers.Add(new CommandMatcher(@"python\s+-c\s+""print\('([^']*)'\)""", m => (m.Groups[1].Value + "\n", "", 0), isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"python\s+-c\s+""print\(([^)]+)\)""", m =>
        {
            string expr = m.Groups[1].Value.Trim('\'', '"');
            return (expr + "\n", "", 0);
        }, isRegex: true));

        // Python math operations
        _commandMatchers.Add(new CommandMatcher(@"python\s+-c\s+""print\((\d+)\s*\*\s*(\d+)\)""", m =>
        {
            long a = long.Parse(m.Groups[1].Value);
            long b = long.Parse(m.Groups[2].Value);
            return ((a * b).ToString() + "\n", "", 0);
        }, isRegex: true));

        // Network isolation tests - curl/wget blocked
        _commandMatchers.Add(new CommandMatcher(@"timeout \d+ curl", "Network access blocked\n", "curl: (7) Couldn't connect to server\n", 7, isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"curl\s+-s\s+https?://", "Network access blocked\n", "curl: (7) Couldn't connect to server\n", 7, isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"wget\s+", "", "wget: unable to resolve host address\n", 4, isRegex: true));

        // Network interface check (Linux)
        _commandMatchers.Add(new CommandMatcher(@"cat /proc/net/dev", "no_external_interfaces\n", "", 0, isRegex: false));

        // DNS resolution tests
        _commandMatchers.Add(new CommandMatcher(@"nslookup|host\s+\S+|dig\s+", "", "connection timed out; no servers could be reached\n", 1, isRegex: true));

        // mkdir commands (Windows)
        _commandMatchers.Add(new CommandMatcher(@"New-Item\s+-ItemType\s+Directory\s+-Force\s+-Path", "", "", 0, isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"mkdir\s+-p", "", "", 0, isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"md\s+", "", "", 0, isRegex: true));

        // pwd/cd commands
        _commandMatchers.Add(new CommandMatcher("pwd", "/app\n", "", 0, isRegex: false));
        _commandMatchers.Add(new CommandMatcher("Get-Location", "C:\\app\n", "", 0, isRegex: false));

        // ls/dir commands
        _commandMatchers.Add(new CommandMatcher(@"^ls\s*$", "", "", 0, isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"^dir\s*$", "", "", 0, isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"Get-ChildItem", "", "", 0, isRegex: true));

        // File existence check
        _commandMatchers.Add(new CommandMatcher(@"test -f", "", "", 0, isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"Test-Path", "True\n", "", 0, isRegex: true));

        // Cat/type file (will be handled specially in ExecuteCommand)
        _commandMatchers.Add(new CommandMatcher(@"cat\s+(\S+)", m => ("", "", 0), isRegex: true)); // Placeholder
        _commandMatchers.Add(new CommandMatcher(@"type\s+(\S+)", m => ("", "", 0), isRegex: true)); // Placeholder

        // Sleep commands
        _commandMatchers.Add(new CommandMatcher(@"sleep\s+\d+", "", "", 0, isRegex: true));
        _commandMatchers.Add(new CommandMatcher(@"Start-Sleep", "", "", 0, isRegex: true));

        // Exit/return commands
        _commandMatchers.Add(new CommandMatcher(@"exit\s+(\d+)", m => ("", "", long.Parse(m.Groups[1].Value)), isRegex: true));

        // Fallback: unknown commands succeed with empty output
        // (This ensures tests don't fail unexpectedly)
    }

    #endregion

    #region IDockerService Implementation

    public Task EnsureImageAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _availableImages.Add(_config.Image);
        }
        return Task.CompletedTask;
    }

    public Task<ContainerInfo> CreateContainerAsync(ResourceLimits? resourceLimits = null, NetworkMode? networkMode = null, CancellationToken cancellationToken = default)
    {
        ResourceLimits limits = resourceLimits ?? _config.DefaultResourceLimits;
        limits.Validate(_config.MaxResourceLimits);

        NetworkMode network = networkMode ?? _config.DefaultNetworkMode;
        string containerId = Guid.NewGuid().ToString("N");
        string containerName = $"{_config.LabelPrefix}-{Guid.NewGuid():N}";
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Dictionary<string, string> labels = new()
        {
            [$"{_config.LabelPrefix}.managed"] = "true",
            [$"{_config.LabelPrefix}.created"] = now.ToString("o"),
            [$"{_config.LabelPrefix}.memory"] = limits.MemoryBytes.ToString(),
            [$"{_config.LabelPrefix}.cpu"] = limits.CpuCores.ToString("F2"),
            [$"{_config.LabelPrefix}.pids"] = limits.MaxProcesses.ToString(),
            [$"{_config.LabelPrefix}.network"] = network.ToString().ToLower()
        };

        MockContainer container = new()
        {
            ContainerId = containerId,
            Name = containerName,
            Image = _config.Image,
            DockerStatus = "running",
            Status = SdkContainerStatus.Warming,
            CreatedAt = now,
            StartedAt = now,
            Labels = labels,
            NetworkMode = network,
            ResourceLimits = limits
        };

        lock (_lock)
        {
            _containers[containerId] = container;
            _files[containerId] = new Dictionary<string, byte[]>();
            _stats[containerId] = new MockStats();
        }

        ContainerInfo info = container.ToContainerInfo();
        return Task.FromResult(info);
    }

    public Task<List<ContainerInfo>> GetManagedContainersAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            List<ContainerInfo> result = _containers.Values
                .Where(c => c.Labels.TryGetValue($"{_config.LabelPrefix}.managed", out string? v) && v == "true")
                .Select(c => c.ToContainerInfo())
                .ToList();
            return Task.FromResult(result);
        }
    }

    public Task<ContainerInfo?> GetContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (_containers.TryGetValue(containerId, out MockContainer? container))
            {
                return Task.FromResult<ContainerInfo?>(container.ToContainerInfo());
            }
            return Task.FromResult<ContainerInfo?>(null);
        }
    }

    public Task DeleteContainerAsync(string containerId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _containers.Remove(containerId);
            _files.Remove(containerId);
            _stats.Remove(containerId);
        }
        return Task.CompletedTask;
    }

    public async Task DeleteAllManagedContainersAsync(CancellationToken cancellationToken = default)
    {
        List<ContainerInfo> containers = await GetManagedContainersAsync(cancellationToken);
        foreach (ContainerInfo container in containers)
        {
            await DeleteContainerAsync(container.ContainerId, cancellationToken);
        }
    }

    public Task<CommandResult> ExecuteCommandAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        // Keep behavior aligned with DockerService: string commands are executed via shell wrapper.
        return ExecuteCommandAsync(containerId, _config.GetShellCommand(command), workingDirectory, timeoutSeconds, cancellationToken);
    }

    public Task<CommandResult> ExecuteCommandAsync(string containerId, string[] command, string workingDirectory, int timeoutSeconds, CancellationToken cancellationToken = default)
    {
        return ExecuteCommandCoreAsync(containerId, command, workingDirectory);
    }

    private Task<CommandResult> ExecuteCommandCoreAsync(string containerId, string[] command, string workingDirectory)
    {
        lock (_lock)
        {
            if (!_containers.ContainsKey(containerId))
            {
                throw new Exceptions.ContainerNotFoundException(containerId);
            }

            // Update stats (simulate CPU usage increase)
            if (_stats.TryGetValue(containerId, out MockStats? stats))
            {
                stats.CpuUsageNanos += 1_000_000_000; // 1 second of CPU
                stats.CommandCount++;
            }
        }

        // Try to match command against registered patterns
        (string stdout, string stderr, long exitCode) = MatchCommand(containerId, command, workingDirectory);

        // Apply truncation if needed
        (string truncatedStdout, bool stdoutTruncated) = TruncateOutput(stdout);
        (string truncatedStderr, bool stderrTruncated) = TruncateOutput(stderr);

        CommandResult result = new()
        {
            Stdout = truncatedStdout,
            Stderr = truncatedStderr,
            ExitCode = exitCode,
            ExecutionTimeMs = 10, // Mock execution time
            IsTruncated = stdoutTruncated || stderrTruncated
        };

        return Task.FromResult(result);
    }

    public async IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(string containerId, string command, string workingDirectory, int timeoutSeconds, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Keep behavior aligned with DockerService: string commands are executed via shell wrapper.
        await foreach (CommandOutputEvent evt in ExecuteCommandStreamAsync(containerId, _config.GetShellCommand(command), workingDirectory, timeoutSeconds, cancellationToken))
        {
            yield return evt;
        }
    }

    public async IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamAsync(string containerId, string[] command, string workingDirectory, int timeoutSeconds, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (CommandOutputEvent evt in ExecuteCommandStreamCoreAsync(containerId, command, workingDirectory, cancellationToken))
        {
            yield return evt;
        }
    }

    private async IAsyncEnumerable<CommandOutputEvent> ExecuteCommandStreamCoreAsync(string containerId, string[] command, string workingDirectory, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_containers.ContainsKey(containerId))
            {
                throw new Exceptions.ContainerNotFoundException(containerId);
            }

            if (_stats.TryGetValue(containerId, out MockStats? stats))
            {
                stats.CpuUsageNanos += 1_000_000_000;
                stats.CommandCount++;
            }
        }

        (string stdout, string stderr, long exitCode) = MatchCommand(containerId, command, workingDirectory);

        // Simulate streaming by yielding line by line
        string[] stdoutLines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        string[] stderrLines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        int maxLines = Math.Max(stdoutLines.Length, stderrLines.Length);
        for (int i = 0; i < maxLines; i++)
        {
            if (cancellationToken.IsCancellationRequested) yield break;

            if (i < stdoutLines.Length)
            {
                yield return CommandOutputEvent.FromStdout(stdoutLines[i] + "\n");
            }
            if (i < stderrLines.Length)
            {
                yield return CommandOutputEvent.FromStderr(stderrLines[i] + "\n");
            }

            // Small delay to simulate streaming
            await Task.Yield();
        }

        yield return CommandOutputEvent.FromExit(exitCode, 10);
    }

    public Task UploadFileAsync(string containerId, string containerPath, byte[] content, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_containers.ContainsKey(containerId))
            {
                throw new Exceptions.ContainerNotFoundException(containerId);
            }

            if (!_files.TryGetValue(containerId, out Dictionary<string, byte[]>? containerFiles))
            {
                containerFiles = new Dictionary<string, byte[]>();
                _files[containerId] = containerFiles;
            }

            // Normalize path
            string normalizedPath = NormalizePath(containerPath);
            containerFiles[normalizedPath] = content;

            // Update stats
            if (_stats.TryGetValue(containerId, out MockStats? stats))
            {
                stats.NetworkRxBytes += content.Length;
            }
        }
        return Task.CompletedTask;
    }

    public Task<List<FileEntry>> ListDirectoryAsync(string containerId, string path, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_containers.ContainsKey(containerId))
            {
                throw new Exceptions.ContainerNotFoundException(containerId);
            }

            List<FileEntry> entries = new();
            if (!_files.TryGetValue(containerId, out Dictionary<string, byte[]>? containerFiles))
            {
                return Task.FromResult(entries);
            }

            string normalizedPath = NormalizePath(path).TrimEnd('/');
            HashSet<string> seenDirs = new();

            foreach (KeyValuePair<string, byte[]> kvp in containerFiles)
            {
                string filePath = kvp.Key;
                if (!filePath.StartsWith(normalizedPath, StringComparison.OrdinalIgnoreCase))
                    continue;

                string relativePath = filePath[normalizedPath.Length..].TrimStart('/');
                if (string.IsNullOrEmpty(relativePath))
                    continue;

                string[] parts = relativePath.Split('/');
                if (parts.Length == 1)
                {
                    // Direct file
                    entries.Add(new FileEntry
                    {
                        Path = filePath,
                        Name = parts[0],
                        IsDirectory = false,
                        Size = kvp.Value.Length,
                        LastModified = DateTimeOffset.UtcNow
                    });
                }
                else if (!seenDirs.Contains(parts[0]))
                {
                    // Subdirectory
                    seenDirs.Add(parts[0]);
                    entries.Add(new FileEntry
                    {
                        Path = normalizedPath + "/" + parts[0],
                        Name = parts[0],
                        IsDirectory = true,
                        Size = 0,
                        LastModified = DateTimeOffset.UtcNow
                    });
                }
            }

            return Task.FromResult(entries);
        }
    }

    public Task<byte[]> DownloadFileAsync(string containerId, string filePath, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_containers.ContainsKey(containerId))
            {
                throw new Exceptions.ContainerNotFoundException(containerId);
            }

            if (!_files.TryGetValue(containerId, out Dictionary<string, byte[]>? containerFiles))
            {
                throw new FileNotFoundException($"File {filePath} not found in container");
            }

            string normalizedPath = NormalizePath(filePath);
            if (!containerFiles.TryGetValue(normalizedPath, out byte[]? content))
            {
                throw new FileNotFoundException($"File {filePath} not found in container");
            }

            // Update stats
            if (_stats.TryGetValue(containerId, out MockStats? stats))
            {
                stats.NetworkTxBytes += content.Length;
            }

            return Task.FromResult(content);
        }
    }

    public Task<SessionUsage?> GetContainerStatsAsync(string containerId, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_containers.ContainsKey(containerId))
            {
                return Task.FromResult<SessionUsage?>(null);
            }

            if (!_stats.TryGetValue(containerId, out MockStats? stats))
            {
                return Task.FromResult<SessionUsage?>(null);
            }

            // Simulate increasing memory usage based on command count
            long memoryUsage = 50 * 1024 * 1024 + (stats.CommandCount * 5 * 1024 * 1024); // Base 50MB + 5MB per command

            SessionUsage usage = new()
            {
                ContainerId = containerId,
                CpuUsageNanos = stats.CpuUsageNanos,
                MemoryUsageBytes = memoryUsage,
                PeakMemoryBytes = memoryUsage + 10 * 1024 * 1024,
                NetworkRxBytes = stats.NetworkRxBytes,
                NetworkTxBytes = stats.NetworkTxBytes
            };

            return Task.FromResult<SessionUsage?>(usage);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _containers.Clear();
            _files.Clear();
            _stats.Clear();
        }
        GC.SuppressFinalize(this);
    }

    #endregion

    #region Helper Methods

    private (string stdout, string stderr, long exitCode) MatchCommand(string containerId, string[] command, string workingDirectory)
    {
        // If this is a shell wrapper, extract the script so our matchers can stay simple.
        if (TryExtractShellScript(command, out string script))
        {
            return MatchCommandText(containerId, script, workingDirectory);
        }

        // Native command array (e.g. python -c ...)
        if (TryHandlePythonCommand(command, workingDirectory, out string pythonOut, out string pythonErr, out long pythonExit))
        {
            return (pythonOut, pythonErr, pythonExit);
        }

        string fallback = string.Join(" ", command);
        return MatchCommandText(containerId, fallback, workingDirectory);
    }

    private (string stdout, string stderr, long exitCode) MatchCommandText(string containerId, string command, string workingDirectory)
    {
        // Special-case: delete file command should mutate in-memory file store.
        if (TryHandleDeleteFile(containerId, command))
        {
            return ("", "", 0);
        }

        // Check for file read commands first
        Match catMatch = Regex.Match(command, @"\bcat\s+(\S+)", RegexOptions.IgnoreCase);
        if (catMatch.Success)
        {
            string filePath = catMatch.Groups[1].Value;
            lock (_lock)
            {
                if (_files.TryGetValue(containerId, out Dictionary<string, byte[]>? containerFiles))
                {
                    string normalizedPath = NormalizePath(filePath);
                    if (containerFiles.TryGetValue(normalizedPath, out byte[]? content))
                    {
                        return (Encoding.UTF8.GetString(content), "", 0);
                    }
                }
            }
            return ("", $"cat: {filePath}: No such file or directory\n", 1);
        }

        Match typeMatch = Regex.Match(command, @"\btype\s+(\S+)", RegexOptions.IgnoreCase);
        if (typeMatch.Success)
        {
            string filePath = typeMatch.Groups[1].Value;
            lock (_lock)
            {
                if (_files.TryGetValue(containerId, out Dictionary<string, byte[]>? containerFiles))
                {
                    string normalizedPath = NormalizePath(filePath);
                    if (containerFiles.TryGetValue(normalizedPath, out byte[]? content))
                    {
                        return (Encoding.UTF8.GetString(content), "", 0);
                    }
                }
            }
            return ("", "The system cannot find the file specified.\n", 1);
        }

        // Specific error test: unknown command should fail.
        if (command.Contains("nonexistent_command_12345", StringComparison.OrdinalIgnoreCase))
        {
            return ("", "command not found\n", 127);
        }

        // Python cwd when executed via shell wrapper (rare but safe)
        if (command.Contains("print(os.getcwd())", StringComparison.OrdinalIgnoreCase))
        {
            return (workingDirectory + "\n", "", 0);
        }

        // Check network mode for network-related commands
        lock (_lock)
        {
            if (_containers.TryGetValue(containerId, out MockContainer? container))
            {
                // For bridge mode, allow some network commands to succeed
                if (container.NetworkMode == NetworkMode.Bridge)
                {
                    if (command.Contains("curl", StringComparison.OrdinalIgnoreCase) && !command.Contains("blocked", StringComparison.OrdinalIgnoreCase))
                    {
                        return ("<!DOCTYPE html><html>Mock response</html>\n", "", 0);
                    }
                    if (command.Contains("nslookup", StringComparison.OrdinalIgnoreCase) || command.Contains("host ", StringComparison.OrdinalIgnoreCase) || command.Contains("dig ", StringComparison.OrdinalIgnoreCase))
                    {
                        return ("Address: 142.250.80.46\n", "", 0);
                    }
                }
            }
        }

        // Built-in generated large output patterns used by truncation tests.
        (bool generated, string genOut, string genErr, long genExit) = TryGenerateLargeOutputForTests(command);
        if (generated)
        {
            return (genOut, genErr, genExit);
        }

        // Built-in patterns for multi-line and streaming output tests.
        (bool loopGenerated, string loopOut, string loopErr, long loopExit) = TryGenerateLoopOutputForTests(command);
        if (loopGenerated)
        {
            return (loopOut, loopErr, loopExit);
        }

        // Try registered matchers
        lock (_lock)
        {
            foreach (CommandMatcher matcher in _commandMatchers)
            {
                (bool matched, string stdout, string stderr, long exitCode) = matcher.TryMatch(command);
                if (matched)
                {
                    return (stdout, stderr, exitCode);
                }
            }
        }

        // Default: succeed with empty output (keep tests resilient).
        return ("", "", 0);
    }

    private static (bool generated, string stdout, string stderr, long exitCode) TryGenerateLoopOutputForTests(string command)
    {
        // Windows multi-line: 1..N | ForEach-Object { Write-Output "Line $_" }
        Match winMulti = Regex.Match(command, @"1\.\.(\d+)\s*\|\s*ForEach-Object\s*\{\s*Write-Output\s*""Line\s+\$_""\s*\}", RegexOptions.IgnoreCase);
        if (winMulti.Success)
        {
            int count = int.Parse(winMulti.Groups[1].Value);
            StringBuilder sb = new();
            for (int i = 1; i <= count; i++)
            {
                sb.AppendLine($"Line {i}");
            }
            return (true, sb.ToString(), "", 0);
        }

        // Linux multi-line: for i in $(seq 1 N); do echo "Line $i"; done
        Match linuxMulti = Regex.Match(command, @"for\s+i\s+in\s+\$\(seq\s+1\s+(\d+)\);\s+do\s+echo\s+""Line\s+\$i"";\s+done", RegexOptions.IgnoreCase);
        if (linuxMulti.Success)
        {
            int count = int.Parse(linuxMulti.Groups[1].Value);
            StringBuilder sb = new();
            for (int i = 1; i <= count; i++)
            {
                sb.AppendLine($"Line {i}");
            }
            return (true, sb.ToString(), "", 0);
        }

        // Windows streaming: 1..N | ForEach-Object { Write-Output "stdout: Line $_"; Write-Error "stderr: Warning $_"; Start-Sleep ... }
        Match winStream = Regex.Match(command, @"1\.\.(\d+)\s*\|\s*ForEach-Object\s*\{\s*Write-Output\s*""stdout:\s*Line\s+\$_"";\s*Write-Error\s*""stderr:\s*Warning\s+\$_""", RegexOptions.IgnoreCase);
        if (winStream.Success)
        {
            int count = int.Parse(winStream.Groups[1].Value);
            StringBuilder stdout = new();
            StringBuilder stderr = new();
            for (int i = 1; i <= count; i++)
            {
                stdout.AppendLine($"stdout: Line {i}");
                stderr.AppendLine($"stderr: Warning {i}");
            }
            return (true, stdout.ToString(), stderr.ToString(), 0);
        }

        // Linux streaming: for i in $(seq 1 N); do echo "stdout: Line $i"; echo "stderr: Warning $i" >&2; sleep ...; done
        Match linuxStream = Regex.Match(command, @"for\s+i\s+in\s+\$\(seq\s+1\s+(\d+)\);\s+do\s+echo\s+""stdout:\s*Line\s+\$i"";\s+echo\s+""stderr:\s*Warning\s+\$i""\s+>&2", RegexOptions.IgnoreCase);
        if (linuxStream.Success)
        {
            int count = int.Parse(linuxStream.Groups[1].Value);
            StringBuilder stdout = new();
            StringBuilder stderr = new();
            for (int i = 1; i <= count; i++)
            {
                stdout.AppendLine($"stdout: Line {i}");
                stderr.AppendLine($"stderr: Warning {i}");
            }
            return (true, stdout.ToString(), stderr.ToString(), 0);
        }

        return (false, "", "", 0);
    }

    private static bool TryExtractShellScript(string[] command, out string script)
    {
        script = string.Empty;
        if (command.Length < 3)
        {
            return false;
        }

        // Windows: powershell -NoProfile -NonInteractive -Command <script>
        if (string.Equals(command[0], "powershell", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < command.Length - 1; i++)
            {
                if (string.Equals(command[i], "-Command", StringComparison.OrdinalIgnoreCase))
                {
                    script = command[i + 1];
                    return true;
                }
            }
        }

        // Linux: /bin/bash -lc <script>
        if (string.Equals(command[0], "/bin/bash", StringComparison.OrdinalIgnoreCase))
        {
            for (int i = 0; i < command.Length - 1; i++)
            {
                if (string.Equals(command[i], "-lc", StringComparison.OrdinalIgnoreCase))
                {
                    script = command[i + 1];
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryHandleDeleteFile(string containerId, string command)
    {
        // Windows pwsh delete: Remove-Item -Force -ErrorAction SilentlyContinue 'path'
        Match win = Regex.Match(command, @"Remove-Item\s+-Force\s+-ErrorAction\s+SilentlyContinue\s+'([^']+)'", RegexOptions.IgnoreCase);
        if (win.Success)
        {
            string target = win.Groups[1].Value;
            return DeleteFileFromStore(containerId, target);
        }

        // Linux delete: rm -f "path"
        Match linux = Regex.Match(command, @"\brm\s+-f\s+""([^""]+)""", RegexOptions.IgnoreCase);
        if (linux.Success)
        {
            string target = linux.Groups[1].Value;
            return DeleteFileFromStore(containerId, target);
        }

        return false;
    }

    private bool DeleteFileFromStore(string containerId, string filePath)
    {
        lock (_lock)
        {
            if (_files.TryGetValue(containerId, out Dictionary<string, byte[]>? containerFiles))
            {
                string normalizedPath = NormalizePath(filePath);
                return containerFiles.Remove(normalizedPath);
            }
        }

        return false;
    }

    private static bool TryHandlePythonCommand(string[] command, string workingDirectory, out string stdout, out string stderr, out long exitCode)
    {
        stdout = "";
        stderr = "";
        exitCode = 0;

        if (command.Length < 3)
        {
            return false;
        }

        if (!string.Equals(command[0], "python", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(command[0], "python3", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.Equals(command[1], "-c", StringComparison.OrdinalIgnoreCase))
        {
            // e.g. python --version is handled by generic matchers
            return false;
        }

        string code = command[2];

        // os.getcwd
        if (code.Contains("print(os.getcwd())", StringComparison.OrdinalIgnoreCase))
        {
            stdout = workingDirectory + "\n";
            return true;
        }

        // os.makedirs(...) – treat as success
        if (code.Contains("os.makedirs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // common streaming loop
        if (code.Contains("for i in range(1,4):", StringComparison.OrdinalIgnoreCase) && code.Contains("Line", StringComparison.OrdinalIgnoreCase))
        {
            stdout = "Line1\nLine2\nLine3\n";
            return true;
        }

        // json.dumps(data, indent=2)
        if (code.Contains("json.dumps", StringComparison.OrdinalIgnoreCase) && code.Contains("resource_limits", StringComparison.OrdinalIgnoreCase))
        {
            stdout = "{\n  \"name\": \"CodePod\",\n  \"version\": \"1.0\",\n  \"features\": [\n    \"resource_limits\",\n    \"network_isolation\"\n  ]\n}\n";
            return true;
        }

        // print('...') / print("...")
        Match printMatch = Regex.Match(code, @"\bprint\((['""])(?<s>(?:\\.|(?!\1).)*)\1\)\s*$", RegexOptions.Singleline);
        if (printMatch.Success)
        {
            string raw = printMatch.Groups["s"].Value;
            stdout = UnescapeLikePython(raw) + "\n";
            return true;
        }

        // print(<expr>) fallback: return expression text
        Match printExpr = Regex.Match(code, @"\bprint\((?<expr>[^)]+)\)\s*$", RegexOptions.Singleline);
        if (printExpr.Success)
        {
            string expr = printExpr.Groups["expr"].Value.Trim();
            stdout = expr.Trim('\\', '\'', '"') + "\n";
            return true;
        }

        return false;
    }

    private static string UnescapeLikePython(string value)
    {
        // Minimal escape handling sufficient for tests.
        return value
            .Replace("\\\\", "\\")
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\\"", "\"")
            .Replace("\\'", "'");
    }

    private static (bool generated, string stdout, string stderr, long exitCode) TryGenerateLargeOutputForTests(string command)
    {
        // Windows large stdout: 1..500 | ForEach-Object { Write-Output ("Line {0}: ..." -f $_) }
        Match winStdout = Regex.Match(command, @"1\.\.(\d+)\s*\|\s*ForEach-Object\s*\{\s*Write-Output\s*\(\s*""Line\s*\{0\}:\s*(?<msg>[^""]+)""\s*-f\s*\$_\s*\)\s*\}", RegexOptions.IgnoreCase);
        if (winStdout.Success)
        {
            int count = int.Parse(winStdout.Groups[1].Value);
            string msg = winStdout.Groups["msg"].Value;
            StringBuilder sb = new();
            for (int i = 1; i <= count; i++)
            {
                sb.AppendLine($"Line {i}: {msg}");
            }
            return (true, sb.ToString(), "", 0);
        }

        // Linux large stdout: for i in $(seq 1 500); do echo "Line $i: ..."; done
        Match linuxStdout = Regex.Match(command, @"for\s+i\s+in\s+\$\(seq\s+1\s+(\d+)\);\s+do\s+echo\s+""Line\s+\$i:\s*(?<msg>[^""]+)"";\s+done", RegexOptions.IgnoreCase);
        if (linuxStdout.Success)
        {
            int count = int.Parse(linuxStdout.Groups[1].Value);
            string msg = linuxStdout.Groups["msg"].Value;
            StringBuilder sb = new();
            for (int i = 1; i <= count; i++)
            {
                sb.AppendLine($"Line {i}: {msg}");
            }
            return (true, sb.ToString(), "", 0);
        }

        // START/MIDDLE/END pattern (both Windows and Linux tests)
        if (command.Contains("=== START ===", StringComparison.OrdinalIgnoreCase) && command.Contains("=== END ===", StringComparison.OrdinalIgnoreCase))
        {
            Match countMatch = Regex.Match(command, @"1\.\.(\d+)");
            int count = countMatch.Success ? int.Parse(countMatch.Groups[1].Value) : 500;
            StringBuilder sb = new();
            sb.AppendLine("=== START ===");
            for (int i = 1; i <= count; i++)
            {
                sb.AppendLine($"Middle line {i}");
            }
            sb.AppendLine("=== END ===");
            return (true, sb.ToString(), "", 0);
        }

        // Large stderr (Windows): [Console]::Error.WriteLine(("Error line {0}" -f $_))
        Match winErr = Regex.Match(command, @"1\.\.(\d+)\s*\|\s*ForEach-Object\s*\{\s*\[Console\]::Error\.WriteLine\(\(""Error line\s*\{0\}""\s*-f\s*\$_\)\)\s*\}", RegexOptions.IgnoreCase);
        if (winErr.Success)
        {
            int count = int.Parse(winErr.Groups[1].Value);
            StringBuilder err = new();
            for (int i = 1; i <= count; i++)
            {
                err.AppendLine($"Error line {i}");
            }
            return (true, "", err.ToString(), 0);
        }

        // Large stderr (Linux): for i in $(seq 1 500); do echo "Error line $i" >&2; done
        Match linuxErr = Regex.Match(command, @"for\s+i\s+in\s+\$\(seq\s+1\s+(\d+)\);\s+do\s+echo\s+""Error line\s+\$i""\s+>&2;\s+done", RegexOptions.IgnoreCase);
        if (linuxErr.Success)
        {
            int count = int.Parse(linuxErr.Groups[1].Value);
            StringBuilder err = new();
            for (int i = 1; i <= count; i++)
            {
                err.AppendLine($"Error line {i}");
            }
            return (true, "", err.ToString(), 0);
        }

        return (false, "", "", 0);
    }

    private (string output, bool truncated) TruncateOutput(string output)
    {
        OutputOptions options = _config.OutputOptions;
        byte[] bytes = Encoding.UTF8.GetBytes(output);

        if (bytes.Length <= options.MaxOutputBytes)
        {
            return (output, false);
        }

        int halfSize = options.MaxOutputBytes / 2;
        int omittedBytes = bytes.Length - options.MaxOutputBytes;

        return options.Strategy switch
        {
            TruncationStrategy.Head => (
                Encoding.UTF8.GetString(bytes, 0, options.MaxOutputBytes) +
                string.Format(options.TruncationMessage, omittedBytes),
                true),

            TruncationStrategy.Tail => (
                string.Format(options.TruncationMessage, omittedBytes) +
                Encoding.UTF8.GetString(bytes, bytes.Length - options.MaxOutputBytes, options.MaxOutputBytes),
                true),

            TruncationStrategy.HeadAndTail => (
                Encoding.UTF8.GetString(bytes, 0, halfSize) +
                string.Format(options.TruncationMessage, omittedBytes) +
                Encoding.UTF8.GetString(bytes, bytes.Length - halfSize, halfSize),
                true),

            _ => (output, false)
        };
    }

    private static string NormalizePath(string path)
    {
        // Normalize to forward slashes and remove leading slash for storage
        return path.Replace('\\', '/').TrimStart('/');
    }

    #endregion

    #region Internal Classes

    private class MockContainer
    {
        public required string ContainerId { get; init; }
        public required string Name { get; init; }
        public required string Image { get; init; }
        public required string DockerStatus { get; set; }
        public SdkContainerStatus Status { get; set; }
        public required DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset? StartedAt { get; init; }
        public required Dictionary<string, string> Labels { get; init; }
        public NetworkMode NetworkMode { get; init; }
        public required ResourceLimits ResourceLimits { get; init; }

        public ContainerInfo ToContainerInfo() => new()
        {
            ContainerId = ContainerId,
            Name = Name,
            Image = Image,
            DockerStatus = DockerStatus,
            Status = Status,
            CreatedAt = CreatedAt,
            StartedAt = StartedAt,
            Labels = new Dictionary<string, string>(Labels)
        };
    }

    private class MockStats
    {
        public long CpuUsageNanos { get; set; }
        public long MemoryUsageBytes { get; set; }
        public long NetworkRxBytes { get; set; }
        public long NetworkTxBytes { get; set; }
        public int CommandCount { get; set; }
    }

    private class CommandMatcher
    {
        private readonly string _pattern;
        private readonly bool _isRegex;
        private readonly Func<Match, (string stdout, string stderr, long exitCode)>? _dynamicHandler;
        private readonly string? _staticStdout;
        private readonly string? _staticStderr;
        private readonly long _staticExitCode;

        public CommandMatcher(string pattern, string stdout, string stderr, long exitCode, bool isRegex)
        {
            _pattern = pattern;
            _isRegex = isRegex;
            _staticStdout = stdout;
            _staticStderr = stderr;
            _staticExitCode = exitCode;
        }

        public CommandMatcher(string pattern, Func<Match, (string stdout, string stderr, long exitCode)> handler, bool isRegex)
        {
            _pattern = pattern;
            _isRegex = isRegex;
            _dynamicHandler = handler;
        }

        public (bool matched, string stdout, string stderr, long exitCode) TryMatch(string command)
        {
            if (_isRegex)
            {
                Match match = Regex.Match(command, _pattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    if (_dynamicHandler != null)
                    {
                        (string stdout, string stderr, long exitCode) = _dynamicHandler(match);
                        return (true, stdout, stderr, exitCode);
                    }
                    return (true, _staticStdout ?? "", _staticStderr ?? "", _staticExitCode);
                }
            }
            else
            {
                if (command.Contains(_pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return (true, _staticStdout ?? "", _staticStderr ?? "", _staticExitCode);
                }
            }

            return (false, "", "", 0);
        }
    }

    #endregion
}
