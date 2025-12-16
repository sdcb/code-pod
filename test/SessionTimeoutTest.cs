// Session Timeout 测试
// 测试点：
// 1. 执行shell脚本、下载文件、上传文件、检查文件夹操作能自动延长timeout时间
// 2. timeout超时后，session和docker容器都会自动销毁

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var baseUrl = "http://localhost:5099";
var client = new HttpClient { BaseAddress = new Uri(baseUrl) };
var jsonOptions = new JsonSerializerOptions 
{ 
    PropertyNameCaseInsensitive = true,
    TypeInfoResolver = AppJsonContext.Default
};

Console.WriteLine("=== Session Timeout 测试 ===\n");

// 测试 1: 验证操作能延长 timeout
await TestActivityExtendsTimeout();

// 测试 2: 验证超时后 session 和容器自动销毁
await TestTimeoutDestroysSessionAndContainer();

Console.WriteLine("\n=== 所有测试完成 ===");

// ========== 测试方法 ==========

async Task TestActivityExtendsTimeout()
{
    Console.WriteLine("【测试1】验证操作能延长 timeout 时间");
    Console.WriteLine("----------------------------------------");
    
    // 创建会话，指定10秒超时
    var session = await CreateSession("TimeoutExtendTest", 10);
    if (session == null) return;
    
    var sessionId = session.SessionId;
    var initialLastActivity = session.LastActivityAt;
    Console.WriteLine($"  初始 LastActivityAt: {initialLastActivity:HH:mm:ss.fff}");
    
    // 等待 1 秒确保时间有变化
    await Task.Delay(1000);
    
    // 测试 1.1: 执行 shell 命令
    Console.WriteLine("\n  [1.1] 执行 shell 命令...");
    var cmdResult = await ExecuteCommand(sessionId, "echo 'hello'");
    if (cmdResult)
    {
        var updatedSession = await GetSession(sessionId);
        if (updatedSession != null && updatedSession.LastActivityAt > initialLastActivity)
        {
            Console.WriteLine($"  ✅ 命令执行后 LastActivityAt 已更新: {updatedSession.LastActivityAt:HH:mm:ss.fff}");
            initialLastActivity = updatedSession.LastActivityAt;
        }
        else
        {
            Console.WriteLine($"  ❌ 命令执行后 LastActivityAt 未更新!");
        }
    }
    
    await Task.Delay(1000);
    
    // 测试 1.2: 上传文件
    Console.WriteLine("\n  [1.2] 上传文件...");
    var uploadResult = await UploadFile(sessionId, "test.txt", "Hello, World!");
    if (uploadResult)
    {
        var updatedSession = await GetSession(sessionId);
        if (updatedSession != null && updatedSession.LastActivityAt > initialLastActivity)
        {
            Console.WriteLine($"  ✅ 文件上传后 LastActivityAt 已更新: {updatedSession.LastActivityAt:HH:mm:ss.fff}");
            initialLastActivity = updatedSession.LastActivityAt;
        }
        else
        {
            Console.WriteLine($"  ❌ 文件上传后 LastActivityAt 未更新!");
        }
    }
    
    await Task.Delay(1000);
    
    // 测试 1.3: 检查文件夹
    Console.WriteLine("\n  [1.3] 检查文件夹...");
    var listResult = await ListDirectory(sessionId, "/app");
    if (listResult)
    {
        var updatedSession = await GetSession(sessionId);
        if (updatedSession != null && updatedSession.LastActivityAt > initialLastActivity)
        {
            Console.WriteLine($"  ✅ 列出目录后 LastActivityAt 已更新: {updatedSession.LastActivityAt:HH:mm:ss.fff}");
            initialLastActivity = updatedSession.LastActivityAt;
        }
        else
        {
            Console.WriteLine($"  ❌ 列出目录后 LastActivityAt 未更新!");
        }
    }
    
    await Task.Delay(1000);
    
    // 测试 1.4: 下载文件
    Console.WriteLine("\n  [1.4] 下载文件...");
    var downloadResult = await DownloadFile(sessionId, "/app/test.txt");
    if (downloadResult)
    {
        var updatedSession = await GetSession(sessionId);
        if (updatedSession != null && updatedSession.LastActivityAt > initialLastActivity)
        {
            Console.WriteLine($"  ✅ 文件下载后 LastActivityAt 已更新: {updatedSession.LastActivityAt:HH:mm:ss.fff}");
        }
        else
        {
            Console.WriteLine($"  ❌ 文件下载后 LastActivityAt 未更新!");
        }
    }
    
    // 清理
    await DestroySession(sessionId);
    Console.WriteLine("\n  测试1完成，会话已清理");
}

async Task TestTimeoutDestroysSessionAndContainer()
{
    Console.WriteLine("\n【测试2】验证超时后 session 和容器自动销毁");
    Console.WriteLine("----------------------------------------");
    
    // 创建会话，指定10秒超时
    var session = await CreateSession("TimeoutDestroyTest", 10);
    if (session == null) return;
    
    var sessionId = session.SessionId;
    var containerId = session.ContainerId;
    
    Console.WriteLine($"  Session ID: {sessionId}");
    Console.WriteLine($"  Container ID: {containerId?[..12] ?? "null"}");
    Console.WriteLine($"  创建时间: {session.CreatedAt:HH:mm:ss}");
    Console.WriteLine($"  等待超时（配置为 10 秒）...\n");
    
    // 每秒检查一次状态，最多等待 20 秒
    var timeout = TimeSpan.FromSeconds(20);
    var startTime = DateTime.UtcNow;
    var sessionDestroyed = false;
    var containerDestroyed = false;
    
    while (DateTime.UtcNow - startTime < timeout)
    {
        await Task.Delay(1000);
        var elapsed = (DateTime.UtcNow - startTime).TotalSeconds;
        
        // 检查 session 状态
        var currentSession = await GetSession(sessionId);
        if (currentSession == null && !sessionDestroyed)
        {
            sessionDestroyed = true;
            Console.WriteLine($"  [{elapsed:F0}s] ✅ Session 已被销毁");
        }
        else if (currentSession != null)
        {
            Console.WriteLine($"  [{elapsed:F0}s] Session 状态: {currentSession.Status}, LastActivity: {currentSession.LastActivityAt:HH:mm:ss}");
        }
        
        // 检查容器状态
        if (!string.IsNullOrEmpty(containerId) && !containerDestroyed)
        {
            var containers = await GetAllContainers();
            var containerExists = containers?.Any(c => c.ContainerId == containerId) ?? false;
            if (!containerExists)
            {
                containerDestroyed = true;
                Console.WriteLine($"  [{elapsed:F0}s] ✅ Container 已被销毁");
            }
        }
        
        // 两者都销毁了就退出
        if (sessionDestroyed && containerDestroyed)
        {
            Console.WriteLine($"\n  ✅✅ 测试通过！Session 和 Container 都已在超时后自动销毁");
            return;
        }
    }
    
    // 超时未完成
    Console.WriteLine($"\n  ❌ 测试失败！等待 {timeout.TotalSeconds} 秒后:");
    Console.WriteLine($"     Session 销毁: {(sessionDestroyed ? "是" : "否")}");
    Console.WriteLine($"     Container 销毁: {(containerDestroyed ? "是" : "否")}");
}

// ========== 辅助方法 ==========

async Task<SessionInfo?> CreateSession(string name, int? timeoutSeconds = null)
{
    try
    {
        var request = new CreateSessionRequest { Name = name, TimeoutSeconds = timeoutSeconds };
        var content = new StringContent(
            JsonSerializer.Serialize(request, AppJsonContext.Default.CreateSessionRequest),
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync("/api/sessions", content);
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.ApiResponseSessionInfo);
        if (result?.Success == true && result.Data != null)
        {
            Console.WriteLine($"  创建会话成功: {result.Data.SessionId[..8]}..., 超时: {timeoutSeconds ?? -1}秒");
            return result.Data;
        }
        Console.WriteLine($"  ❌ 创建会话失败: {result?.Error}");
        return null;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ❌ 创建会话异常: {ex.Message}");
        return null;
    }
}

async Task<SessionInfo?> GetSession(string sessionId)
{
    try
    {
        var response = await client.GetAsync($"/api/sessions/{sessionId}");
        if (!response.IsSuccessStatusCode) return null;
        
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.ApiResponseSessionInfo);
        return result?.Data;
    }
    catch
    {
        return null;
    }
}

async Task<bool> ExecuteCommand(string sessionId, string command)
{
    try
    {
        var request = new ExecuteCommandRequest { Command = command };
        var content = new StringContent(
            JsonSerializer.Serialize(request, AppJsonContext.Default.ExecuteCommandRequest),
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync($"/api/sessions/{sessionId}/commands", content);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

async Task<bool> UploadFile(string sessionId, string fileName, string content)
{
    try
    {
        using var formContent = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        formContent.Add(fileContent, "file", fileName);
        
        var response = await client.PostAsync($"/api/sessions/{sessionId}/files/upload?targetPath=/app", formContent);
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

async Task<bool> ListDirectory(string sessionId, string path)
{
    try
    {
        var response = await client.GetAsync($"/api/sessions/{sessionId}/files/list?path={path}");
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

async Task<bool> DownloadFile(string sessionId, string path)
{
    try
    {
        var response = await client.GetAsync($"/api/sessions/{sessionId}/files/download?path={path}");
        return response.IsSuccessStatusCode;
    }
    catch
    {
        return false;
    }
}

async Task DestroySession(string sessionId)
{
    try
    {
        await client.DeleteAsync($"/api/sessions/{sessionId}");
    }
    catch { }
}

async Task<List<ContainerInfo>?> GetAllContainers()
{
    try
    {
        var response = await client.GetAsync("/api/admin/containers");
        if (!response.IsSuccessStatusCode) return null;
        
        var json = await response.Content.ReadAsStringAsync();
        var result = JsonSerializer.Deserialize(json, AppJsonContext.Default.ApiResponseListContainerInfo);
        return result?.Data;
    }
    catch
    {
        return null;
    }
}

// ========== 模型类 ==========

class ApiResponse<T>
{
    public bool Success { get; set; }
    public T? Data { get; set; }
    public string? Error { get; set; }
}

class SessionInfo
{
    public string SessionId { get; set; } = "";
    public string? Name { get; set; }
    public string? ContainerId { get; set; }
    public string Status { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }
}

class ContainerInfo
{
    public string ContainerId { get; set; } = "";
    public string Status { get; set; } = "";
}

class CreateSessionRequest
{
    public string? Name { get; set; }
    public int? TimeoutSeconds { get; set; }
}

class ExecuteCommandRequest
{
    public string Command { get; set; } = "";
}

[JsonSerializable(typeof(ApiResponse<SessionInfo>))]
[JsonSerializable(typeof(ApiResponse<List<ContainerInfo>>))]
[JsonSerializable(typeof(CreateSessionRequest))]
[JsonSerializable(typeof(ExecuteCommandRequest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
partial class AppJsonContext : JsonSerializerContext
{
}
