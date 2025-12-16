// 禁用 AOT/Trimming 警告，这是测试代码，不需要 AOT 支持
#pragma warning disable IL2026
#pragma warning disable IL3050

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

/// <summary>
/// 测试容器池达到最大限制时的行为
/// </summary>
public class MaxContainerTest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };
    
    private static readonly HttpClient Client = new() { BaseAddress = new Uri("http://localhost:5099") };
    private static readonly List<string> CreatedSessions = [];

    public static async Task Main(string[] args)
    {
        try
        {
            PrintHeader("=== Max Container Test ===");

            // 1. 获取当前状态
            PrintStep("Step 1: Get current status");
            await GetStatusAsync();

            // 2. 清理所有现有会话
            PrintStep("\nStep 2: Clean up existing sessions");
            await CleanupAllSessionsAsync();

            // 3. 创建 10 个会话（达到最大容器数）
            PrintStep("\nStep 3: Create 10 sessions (max containers)");
            for (int i = 1; i <= 10; i++)
            {
                var session = await CreateSessionAsync($"Session-{i}");
                var containerDisplay = session.ContainerId != null ? session.ContainerId[..12] : "null";
                PrintInfo($"  [{i}/10] Created {session.SessionId} - Status: {session.StatusText}, Container: {containerDisplay}");
                await Task.Delay(500); // 给容器时间创建
            }

            // 4. 查看当前状态
            PrintStep("\nStep 4: Check status after creating 10 sessions");
            await GetStatusAsync();

            // 5. 尝试创建第 11 个会话
            PrintStep("\nStep 5: Try to create 11th session (should be queued)");
            var queuedSession = await CreateSessionAsync("Session-11-Queued");
            PrintInfo($"  Session {queuedSession.SessionId}");
            PrintInfo($"    Status: {queuedSession.StatusText}");
            PrintInfo($"    Container: {queuedSession.ContainerId ?? "null"}");
            PrintInfo($"    Queue Position: {queuedSession.QueuePosition}");

            if (queuedSession.Status == "Queued")
            {
                PrintSuccess("\n  ✓ 正确：会话进入等待队列");

                // 6. 销毁第一个会话，释放容器
                PrintStep("\nStep 6: Destroy first session to release container");
                var firstSessionId = CreatedSessions[0];
                await DestroySessionAsync(firstSessionId);
                PrintInfo($"  Destroyed {firstSessionId}");

                // 7. 等待并多次检查队列中的会话是否获得了容器
                PrintStep("\nStep 7: Check if queued session got container");
                SessionResponse? updatedSession = null;
                
                for (int i = 0; i < 10; i++)
                {
                    await Task.Delay(1000); // 每秒检查一次
                    updatedSession = await GetSessionAsync(queuedSession.SessionId);
                    
                    if (updatedSession != null)
                    {
                        var containerDisplay = updatedSession.ContainerId != null ? updatedSession.ContainerId[..12] : "null";
                        PrintInfo($"  Check {i + 1}: Status={updatedSession.StatusText}, Container={containerDisplay}");
                        
                        if (updatedSession.Status == "Active" && !string.IsNullOrEmpty(updatedSession.ContainerId))
                        {
                            break;
                        }
                    }
                }
                
                if (updatedSession != null)
                {
                    var finalContainer = updatedSession.ContainerId != null ? updatedSession.ContainerId[..12] : "null";
                    PrintInfo($"\n  Final state:");
                    PrintInfo($"    Status: {updatedSession.StatusText}");
                    PrintInfo($"    Container: {finalContainer}");
                    PrintInfo($"    Queue Position: {updatedSession.QueuePosition}");

                    if (updatedSession.Status == "Active" && !string.IsNullOrEmpty(updatedSession.ContainerId))
                    {
                        PrintSuccess("\n  ✓ 正确：队列中的会话自动获得了释放的容器");
                    }
                    else
                    {
                        PrintError("\n  ✗ 错误：队列中的会话没有自动获得容器");
                    }
                }
            }
            else
            {
                PrintError("\n  ✗ 错误：第 11 个会话应该进入队列，但状态是：" + queuedSession.StatusText);
            }

            // 8. 最终状态
            PrintStep("\nStep 8: Final status");
            await GetStatusAsync();

            // 9. 清理
            PrintStep("\nStep 9: Cleanup");
            await CleanupAllSessionsAsync();

            PrintHeader("\n=== Test Completed ===");
        }
        catch (Exception ex)
        {
            PrintError($"\n❌ Error: {ex.Message}");
            if (ex.InnerException != null)
            {
                PrintError($"   Inner: {ex.InnerException.Message}");
            }
        }
    }

    #region Console Output Helpers
    
    private static void PrintHeader(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void PrintStep(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void PrintInfo(string message)
    {
        Console.WriteLine(message);
    }

    private static void PrintSuccess(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    private static void PrintError(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    #endregion

    #region API Methods

    private static async Task GetStatusAsync()
    {
        var response = await Client.GetFromJsonAsync<ApiResponse<SystemStatus>>("/api/admin/status", JsonOptions);
        if (response?.Data != null)
        {
            var data = response.Data;
            var totalContainers = data.Containers.Count;
            var idleContainers = data.AvailableContainers;
            var busyContainers = totalContainers - idleContainers - data.WarmingContainers - data.DestroyingContainers;
            
            PrintInfo($"  Pool Status:");
            PrintInfo($"    Total Containers: {totalContainers}");
            PrintInfo($"    Idle: {idleContainers}, Busy: {busyContainers}, Warming: {data.WarmingContainers}, Destroying: {data.DestroyingContainers}");
            PrintInfo($"    Active Sessions: {data.ActiveSessions}");
            PrintInfo($"    Max Containers: {data.MaxContainers}");
        }
    }

    private static async Task<SessionResponse> CreateSessionAsync(string name)
    {
        var response = await Client.PostAsJsonAsync("/api/sessions", new { name }, JsonOptions);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ApiResponse<SessionResponse>>(JsonOptions);
        
        if (result?.Data == null)
        {
            throw new Exception("Failed to create session");
        }

        CreatedSessions.Add(result.Data.SessionId);
        return result.Data;
    }

    private static async Task<SessionResponse?> GetSessionAsync(string sessionId)
    {
        var response = await Client.GetFromJsonAsync<ApiResponse<SessionResponse>>($"/api/sessions/{sessionId}", JsonOptions);
        return response?.Data;
    }

    private static async Task DestroySessionAsync(string sessionId)
    {
        await Client.DeleteAsync($"/api/sessions/{sessionId}");
    }

    private static async Task CleanupAllSessionsAsync()
    {
        var response = await Client.GetFromJsonAsync<ApiResponse<List<SessionResponse>>>("/api/sessions", JsonOptions);
        if (response?.Data != null && response.Data.Count > 0)
        {
            // 并行清理所有会话
            var tasks = response.Data.Select(async session =>
            {
                await DestroySessionAsync(session.SessionId);
                PrintInfo($"  Cleaned up session: {session.SessionId}");
            });
            await Task.WhenAll(tasks);
        }
        CreatedSessions.Clear();
    }

    #endregion

    #region Response Models

    public class ApiResponse<T>
    {
        public bool Success { get; set; }
        public T? Data { get; set; }
        public string? Error { get; set; }
    }

    public class SystemStatus
    {
        public int MaxContainers { get; set; }
        public int AvailableContainers { get; set; }
        public int ActiveSessions { get; set; }
        public int WarmingContainers { get; set; }
        public int DestroyingContainers { get; set; }
        public string Image { get; set; } = "";
        public List<ContainerInfo> Containers { get; set; } = new();
    }

    public class ContainerInfo
    {
        public string ContainerId { get; set; } = "";
        public string ShortId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Image { get; set; } = "";
        public string DockerStatus { get; set; } = "";
        public string Status { get; set; } = "";
        public string? SessionId { get; set; }
    }

    public class SessionResponse
    {
        public string SessionId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Status { get; set; } = "";
        public string? ContainerId { get; set; }
        public int? QueuePosition { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset LastActivityAt { get; set; }
        public int CommandCount { get; set; }
        
        public string StatusText => Status;
    }

    #endregion
}
