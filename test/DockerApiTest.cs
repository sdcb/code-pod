// Docker Shell Host Web API 完整测试
// 使用 .NET 10 single-file runner 语法。执行命令: dotnet run DockerApiTest.cs
// 确保 Docker Shell Host 服务正在运行

using System.Text;
using System.Text.Json;

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5099";
using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(60) };

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║       Docker Shell Host Web API 完整测试套件                  ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine($"  服务地址: {baseUrl}");
Console.WriteLine();

var passed = 0;
var failed = 0;

// ============================================================================
// 测试 1: 获取系统状态
// ============================================================================
await RunTest("获取系统状态", async () =>
{
    var json = await client.GetStringAsync("/api/admin/status");
    var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    
    Assert(root.GetProperty("success").GetBoolean(), "请求应成功");
    var data = root.GetProperty("data");
    
    var availableContainers = data.GetProperty("availableContainers").GetInt32();
    var maxContainers = data.GetProperty("maxContainers").GetInt32();
    var activeSessions = data.GetProperty("activeSessions").GetInt32();
    var warmingContainers = data.GetProperty("warmingContainers").GetInt32();
    var destroyingContainers = data.GetProperty("destroyingContainers").GetInt32();
    
    Console.WriteLine($"  可用容器: {availableContainers}/{maxContainers}");
    Console.WriteLine($"  活动会话: {activeSessions}");
    Console.WriteLine($"  创建中: {warmingContainers}");
    Console.WriteLine($"  销毁中: {destroyingContainers}");
    
    Assert(maxContainers > 0, "最大容器数应大于0");
});

// ============================================================================
// 测试 2: 创建会话
// ============================================================================
string? sessionId = null;

await RunTest("创建会话", async () =>
{
    var content = new StringContent("{\"name\": \"API测试会话\"}", Encoding.UTF8, "application/json");
    var response = await client.PostAsync("/api/sessions", content);
    response.EnsureSuccessStatusCode();
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    
    Assert(root.GetProperty("success").GetBoolean(), "请求应成功");
    var data = root.GetProperty("data");
    
    sessionId = data.GetProperty("sessionId").GetString();
    var status = data.GetProperty("status").ToString();
    var containerId = data.TryGetProperty("containerId", out var cid) ? cid.GetString() : null;
    
    Console.WriteLine($"  会话ID: {sessionId}");
    Console.WriteLine($"  状态: {status}");
    Console.WriteLine($"  容器ID: {containerId ?? "(排队中)"}");
    
    Assert(!string.IsNullOrEmpty(sessionId), "会话ID不应为空");
});

// 等待会话就绪
if (sessionId != null)
{
    Console.WriteLine("\n⏳ 等待会话就绪...");
    for (int i = 0; i < 30; i++)
    {
        await Task.Delay(1000);
        var json = await client.GetStringAsync($"/api/sessions/{sessionId}");
        var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.GetProperty("data");
        if (data.TryGetProperty("containerId", out var cid) && cid.ValueKind != JsonValueKind.Null)
        {
            Console.WriteLine($"✓ 会话已就绪，容器ID: {cid.GetString()?[..12]}");
            break;
        }
        Console.Write(".");
    }
    Console.WriteLine();
}

// ============================================================================
// 测试 3: 获取会话详情
// ============================================================================
await RunTest("获取会话详情", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var json = await client.GetStringAsync($"/api/sessions/{sessionId}");
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    Console.WriteLine($"  会话ID: {data.GetProperty("sessionId").GetString()}");
    Console.WriteLine($"  状态: {data.GetProperty("status")}");
    Console.WriteLine($"  容器ID: {data.GetProperty("containerId").GetString()?[..12] ?? "-"}");
    Console.WriteLine($"  命令数: {data.GetProperty("commandCount").GetInt32()}");
});

// ============================================================================
// 测试 4: 执行命令 - 基本输出
// ============================================================================
await RunTest("执行命令 - 基本输出", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var content = new StringContent("{\"command\": \"echo 'Hello from Docker Shell Host!'\"}", Encoding.UTF8, "application/json");
    var response = await client.PostAsync($"/api/sessions/{sessionId}/commands", content);
    response.EnsureSuccessStatusCode();
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    var stdout = data.GetProperty("stdout").GetString();
    var stderr = data.TryGetProperty("stderr", out var se) ? se.GetString() : null;
    var exitCode = data.GetProperty("exitCode").GetInt32();
    var execTime = data.GetProperty("executionTimeMs").GetInt64();
    
    Console.WriteLine($"  stdout: {stdout?.Trim()}");
    Console.WriteLine($"  stderr: {stderr ?? "(空)"}");
    Console.WriteLine($"  退出码: {exitCode}");
    Console.WriteLine($"  耗时: {execTime}ms");
    
    Assert(exitCode == 0, "退出码应为0");
    Assert(stdout?.Contains("Hello from Docker Shell Host!") == true, "输出应包含预期内容");
});

// ============================================================================
// 测试 5: 执行命令 - 错误处理
// ============================================================================
await RunTest("执行命令 - 错误处理", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var content = new StringContent("{\"command\": \"nonexistent_command_12345\"}", Encoding.UTF8, "application/json");
    var response = await client.PostAsync($"/api/sessions/{sessionId}/commands", content);
    response.EnsureSuccessStatusCode();
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    var stdout = data.TryGetProperty("stdout", out var so) ? so.GetString() : null;
    var stderr = data.TryGetProperty("stderr", out var se) ? se.GetString() : null;
    var exitCode = data.GetProperty("exitCode").GetInt32();
    
    Console.WriteLine($"  stdout: {stdout ?? "(空)"}");
    Console.WriteLine($"  stderr: {stderr?.Trim()}");
    Console.WriteLine($"  退出码: {exitCode}");
    
    Assert(exitCode != 0, "退出码应非0");
    Assert(!string.IsNullOrEmpty(stderr), "stderr应有错误信息");
});

// ============================================================================
// 测试 6: 执行命令 - 多行输出
// ============================================================================
await RunTest("执行命令 - 多行输出", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var content = new StringContent("{\"command\": \"for i in 1 2 3; do echo \\\"Line $i\\\"; done\"}", Encoding.UTF8, "application/json");
    var response = await client.PostAsync($"/api/sessions/{sessionId}/commands", content);
    response.EnsureSuccessStatusCode();
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    var stdout = data.GetProperty("stdout").GetString() ?? "";
    var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    
    Console.WriteLine($"  输出行数: {lines.Length}");
    foreach (var line in lines)
    {
        Console.WriteLine($"    > {line}");
    }
    
    Assert(lines.Length == 3, $"应有3行输出，实际: {lines.Length}");
});

// ============================================================================
// 测试 6.5: 执行命令 - SSE 流式输出
// ============================================================================
await RunTest("执行命令 - SSE 流式输出", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var command = "for i in 1 2 3; do echo \\\"stdout: Line $i\\\"; echo \\\"stderr: Warning $i\\\" >&2; sleep 0.2; done";
    var content = new StringContent($"{{\"command\": \"{command}\", \"timeoutSeconds\": 30}}", Encoding.UTF8, "application/json");
    
    using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/sessions/{sessionId}/commands/stream");
    request.Content = content;
    
    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    response.EnsureSuccessStatusCode();
    
    Assert(response.Content.Headers.ContentType?.MediaType == "text/event-stream", "Content-Type 应为 text/event-stream");
    
    var stdoutEvents = new List<string>();
    var stderrEvents = new List<string>();
    long? exitCode = null;
    long? executionTimeMs = null;
    
    await using var stream = await response.Content.ReadAsStreamAsync();
    
    Console.WriteLine("  收到的 SSE 事件:");
    
    // 使用 System.Net.ServerSentEvents 解析 SSE
    await foreach (var sseItem in System.Net.ServerSentEvents.SseParser.Create(stream).EnumerateAsync())
    {
        // 跳过空事件
        if (string.IsNullOrEmpty(sseItem.Data)) continue;
        
        var payload = JsonDocument.Parse(sseItem.Data).RootElement;
        
        switch (sseItem.EventType)
        {
            case "stdout":
                var stdoutData = payload.GetProperty("data").GetString() ?? "";
                stdoutEvents.Add(stdoutData);
                Console.WriteLine($"    [stdout] {stdoutData.TrimEnd()}");
                break;
            case "stderr":
                var stderrData = payload.GetProperty("data").GetString() ?? "";
                stderrEvents.Add(stderrData);
                Console.WriteLine($"    [stderr] {stderrData.TrimEnd()}");
                break;
            case "exit":
                exitCode = payload.GetProperty("exitCode").GetInt64();
                executionTimeMs = payload.GetProperty("executionTimeMs").GetInt64();
                Console.WriteLine($"    [exit] 退出码: {exitCode}, 耗时: {executionTimeMs}ms");
                break;
        }
    }
    
    Console.WriteLine($"  stdout 事件数: {stdoutEvents.Count}");
    Console.WriteLine($"  stderr 事件数: {stderrEvents.Count}");
    
    Assert(stdoutEvents.Count > 0, "应收到 stdout 事件");
    Assert(stderrEvents.Count > 0, "应收到 stderr 事件");
    Assert(exitCode != null, "应收到 exit 事件");
    Assert(exitCode == 0, $"退出码应为 0，实际: {exitCode}");
});

// ============================================================================
// 测试 7: 上传文件（表单方式）
// ============================================================================
await RunTest("上传文件（表单方式）", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var fileContent = "Hello, this is a test file!\n测试中文内容";
    
    // 使用 MultipartFormDataContent 模拟表单上传
    using var formContent = new MultipartFormDataContent();
    var fileBytes = Encoding.UTF8.GetBytes(fileContent);
    var fileStreamContent = new ByteArrayContent(fileBytes);
    fileStreamContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
    formContent.Add(fileStreamContent, "file", "test.txt");
    
    var response = await client.PostAsync($"/api/sessions/{sessionId}/files/upload?targetPath=/app", formContent);
    response.EnsureSuccessStatusCode();
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    Assert(doc.RootElement.GetProperty("success").GetBoolean(), "上传应成功");
    
    var data = doc.RootElement.GetProperty("data");
    var filePath = data.GetProperty("filePath").GetString();
    Console.WriteLine($"  文件上传成功: {filePath}");
    Console.WriteLine($"  上传大小: {fileBytes.Length} bytes");
});

// ============================================================================
// 测试 8: 列出文件
// ============================================================================
await RunTest("列出文件", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var json = await client.GetStringAsync($"/api/sessions/{sessionId}/files/list?path=/app");
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    Console.WriteLine($"  目录: {data.GetProperty("path").GetString()}");
    
    var entries = data.GetProperty("entries");
    Console.WriteLine($"  文件数: {entries.GetArrayLength()}");
    
    bool hasTestFile = false;
    foreach (var entry in entries.EnumerateArray())
    {
        var name = entry.GetProperty("name").GetString();
        var isDir = entry.GetProperty("isDirectory").GetBoolean();
        var icon = isDir ? "📁" : "📄";
        Console.WriteLine($"    {icon} {name}");
        if (name == "test.txt") hasTestFile = true;
    }
    
    Assert(hasTestFile, "应包含test.txt文件");
});

// ============================================================================
// 测试 9: 下载文件
// ============================================================================
await RunTest("下载文件", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var response = await client.GetAsync($"/api/sessions/{sessionId}/files/download?path=/app/test.txt");
    response.EnsureSuccessStatusCode();
    
    var content = await response.Content.ReadAsStringAsync();
    var contentDisposition = response.Content.Headers.ContentDisposition;
    
    Console.WriteLine($"  Content-Disposition: {contentDisposition}");
    Console.WriteLine($"  Content-Type: {response.Content.Headers.ContentType}");
    Console.WriteLine($"  内容: {content.Trim()}");
    
    Assert(content.Contains("Hello"), "内容应包含预期文本");
    Assert(content.Contains("测试中文内容"), "内容应包含中文");
});

// ============================================================================
// 测试 11: 获取会话列表
// ============================================================================
await RunTest("获取会话列表", async () =>
{
    var json = await client.GetStringAsync("/api/sessions");
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    Console.WriteLine($"  会话数量: {data.GetArrayLength()}");
    foreach (var session in data.EnumerateArray())
    {
        var sid = session.GetProperty("sessionId").GetString();
        var status = session.GetProperty("status").ToString();
        Console.WriteLine($"    - {sid?[..8]}... [{status}]");
    }
    
    Assert(data.GetArrayLength() > 0, "应至少有一个会话");
});

// ============================================================================
// 测试 12: 获取容器列表
// ============================================================================
await RunTest("获取容器列表", async () =>
{
    var json = await client.GetStringAsync("/api/admin/containers");
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    Console.WriteLine($"  容器数量: {data.GetArrayLength()}");
    foreach (var container in data.EnumerateArray())
    {
        var shortId = container.GetProperty("shortId").GetString();
        var status = container.GetProperty("status").ToString();
        var sid = container.TryGetProperty("sessionId", out var s) && s.ValueKind != JsonValueKind.Null 
            ? s.GetString()?[..8] : "-";
        Console.WriteLine($"    - {shortId} [{status}] Session: {sid}");
    }
});

// ============================================================================
// 测试 13: 删除文件
// ============================================================================
await RunTest("删除文件", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var response = await client.DeleteAsync($"/api/sessions/{sessionId}/files?path=/app/test.txt");
    response.EnsureSuccessStatusCode();
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    Assert(doc.RootElement.GetProperty("success").GetBoolean(), "删除应成功");
    Console.WriteLine("  文件删除成功");
    
    // 验证文件已删除
    var listJson = await client.GetStringAsync($"/api/sessions/{sessionId}/files/list?path=/app");
    var listDoc = JsonDocument.Parse(listJson);
    var entries = listDoc.RootElement.GetProperty("data").GetProperty("entries");
    
    bool hasTestFile = false;
    foreach (var entry in entries.EnumerateArray())
    {
        if (entry.GetProperty("name").GetString() == "test.txt")
        {
            hasTestFile = true;
            break;
        }
    }
    Assert(!hasTestFile, "文件应已被删除");
});

// ============================================================================
// 测试 14: 销毁会话
// ============================================================================
await RunTest("销毁会话", async () =>
{
    AssertNotNull(sessionId, "需要先创建会话");
    
    var response = await client.DeleteAsync($"/api/sessions/{sessionId}");
    response.EnsureSuccessStatusCode();
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    Assert(doc.RootElement.GetProperty("success").GetBoolean(), "销毁应成功");
    Console.WriteLine("  会话销毁成功");
    
    // 验证会话已销毁
    await Task.Delay(1000);
    var sessionsJson = await client.GetStringAsync("/api/sessions");
    var sessionsDoc = JsonDocument.Parse(sessionsJson);
    var sessions = sessionsDoc.RootElement.GetProperty("data");
    
    bool sessionExists = false;
    foreach (var s in sessions.EnumerateArray())
    {
        if (s.GetProperty("sessionId").GetString() == sessionId)
        {
            sessionExists = true;
            break;
        }
    }
    Assert(!sessionExists, "会话应已被销毁");
});

// ============================================================================
// 测试 14.1: 会话自定义超时时间生效
// ============================================================================
await RunTest("会话自定义超时时间生效 (1秒超时)", async () =>
{
    // 创建一个 1 秒超时的会话
    var content = new StringContent("{\"name\": \"短超时测试\", \"timeoutSeconds\": 1}", Encoding.UTF8, "application/json");
    var response = await client.PostAsync("/api/sessions", content);
    response.EnsureSuccessStatusCode();
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    var shortTimeoutSessionId = data.GetProperty("sessionId").GetString();
    var timeoutSeconds = data.TryGetProperty("timeoutSeconds", out var ts) && ts.ValueKind != JsonValueKind.Null 
        ? ts.GetInt32() 
        : -1;
    
    Console.WriteLine($"  会话ID: {shortTimeoutSessionId}");
    Console.WriteLine($"  超时时间: {timeoutSeconds}秒");
    
    Assert(timeoutSeconds == 1, "超时时间应为1秒");
    
    // 等待会话就绪
    for (int i = 0; i < 30; i++)
    {
        await Task.Delay(500);
        var sessionJson = await client.GetStringAsync($"/api/sessions/{shortTimeoutSessionId}");
        var sessionDoc = JsonDocument.Parse(sessionJson);
        var sessionData = sessionDoc.RootElement.GetProperty("data");
        if (sessionData.TryGetProperty("containerId", out var cid) && cid.ValueKind != JsonValueKind.Null)
        {
            Console.WriteLine($"  容器ID: {cid.GetString()?[..12]}");
            break;
        }
    }
    
    // 等待超过 1 秒（给清理服务一些时间检测）
    Console.WriteLine("  等待超时...");
    await Task.Delay(3000);
    
    // 检查会话是否已被销毁
    var checkResponse = await client.GetAsync($"/api/sessions/{shortTimeoutSessionId}");
    Console.WriteLine($"  检查会话状态: {checkResponse.StatusCode}");
    
    Assert(checkResponse.StatusCode == System.Net.HttpStatusCode.NotFound, 
        "1秒超时的会话应在超时后被自动销毁");
    Console.WriteLine("  ✅ 短超时会话已被正确清理");
});

// ============================================================================
// 测试 14.2: 超时时间超过系统限制应报错
// ============================================================================
await RunTest("超时时间超过系统限制应报错", async () =>
{
    // 尝试创建一个超过系统限制的超时时间
    var content = new StringContent("{\"name\": \"超长超时测试\", \"timeoutSeconds\": 999999}", Encoding.UTF8, "application/json");
    var response = await client.PostAsync("/api/sessions", content);
    
    Console.WriteLine($"  响应状态: {response.StatusCode}");
    
    Assert(response.StatusCode == System.Net.HttpStatusCode.BadRequest, 
        "超过系统限制的超时时间应返回400错误");
    
    var json = await response.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);
    var root = doc.RootElement;
    
    var success = root.GetProperty("success").GetBoolean();
    var error = root.TryGetProperty("error", out var err) ? err.GetString() : null;
    
    Console.WriteLine($"  success: {success}");
    Console.WriteLine($"  error: {error}");
    
    Assert(!success, "请求应失败");
    Assert(error?.Contains("cannot exceed") == true || error?.Contains("limit") == true, 
        "错误信息应提示超过限制");
    Console.WriteLine("  ✅ 超长超时正确被拒绝");
});

// ============================================================================
// 测试 15: 检查预热补充逻辑
// ============================================================================
await RunTest("检查预热补充逻辑", async () =>
{
    // 获取当前状态
    var beforeJson = await client.GetStringAsync("/api/admin/status");
    var beforeDoc = JsonDocument.Parse(beforeJson);
    var beforeData = beforeDoc.RootElement.GetProperty("data");
    var availableBefore = beforeData.GetProperty("availableContainers").GetInt32();
    var warmingBefore = beforeData.GetProperty("warmingContainers").GetInt32();
    Console.WriteLine($"  测试前 - 可用: {availableBefore}, 创建中: {warmingBefore}");
    
    // 创建一个会话（会消耗一个空闲容器，并触发补充）
    var content = new StringContent("{\"name\": \"补充测试\"}", Encoding.UTF8, "application/json");
    var response = await client.PostAsync("/api/sessions", content);
    response.EnsureSuccessStatusCode();
    
    var sessionJson = await response.Content.ReadAsStringAsync();
    var sessionDoc = JsonDocument.Parse(sessionJson);
    var newSessionId = sessionDoc.RootElement.GetProperty("data").GetProperty("sessionId").GetString();
    
    // 稍等让补充逻辑启动
    await Task.Delay(1000);
    
    var afterJson = await client.GetStringAsync("/api/admin/status");
    var afterDoc = JsonDocument.Parse(afterJson);
    var afterData = afterDoc.RootElement.GetProperty("data");
    var availableAfter = afterData.GetProperty("availableContainers").GetInt32();
    var warmingAfter = afterData.GetProperty("warmingContainers").GetInt32();
    Console.WriteLine($"  测试后 - 可用: {availableAfter}, 创建中: {warmingAfter}");
    
    // 清理
    await client.DeleteAsync($"/api/sessions/{newSessionId}");
    
    // 验证：创建会话后应该有容器正在预热或可用容器数未减少太多
    var total = availableAfter + warmingAfter;
    Console.WriteLine($"  可用 + 创建中 = {total}");
    Assert(total >= 1, "应有容器正在补充或已可用");
});

// ============================================================================
// 测试 16: 最终检查系统状态
// ============================================================================
await RunTest("检查系统状态 (测试后)", async () =>
{
    // 等待容器回收/预热
    await Task.Delay(3000);
    
    var json = await client.GetStringAsync("/api/admin/status");
    var doc = JsonDocument.Parse(json);
    var data = doc.RootElement.GetProperty("data");
    
    var availableContainers = data.GetProperty("availableContainers").GetInt32();
    var maxContainers = data.GetProperty("maxContainers").GetInt32();
    var activeSessions = data.GetProperty("activeSessions").GetInt32();
    var warmingContainers = data.GetProperty("warmingContainers").GetInt32();
    var destroyingContainers = data.GetProperty("destroyingContainers").GetInt32();
    
    Console.WriteLine($"  可用容器: {availableContainers}/{maxContainers}");
    Console.WriteLine($"  活动会话: {activeSessions}");
    Console.WriteLine($"  创建中: {warmingContainers}");
    Console.WriteLine($"  销毁中: {destroyingContainers}");
});

// ============================================================================
// 测试结果汇总
// ============================================================================
Console.WriteLine();
Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║                       测试结果汇总                            ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine();
Console.WriteLine($"  ✓ 通过: {passed}");
Console.WriteLine($"  ✗ 失败: {failed}");
Console.WriteLine($"  总计:   {passed + failed}");
Console.WriteLine();

if (failed > 0)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("部分测试失败！");
    Console.ResetColor();
    return 1;
}
else
{
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine("所有测试通过！");
    Console.ResetColor();
    return 0;
}

// ============================================================================
// 辅助方法
// ============================================================================

async Task RunTest(string name, Func<Task> test)
{
    Console.WriteLine($"\n▶ 测试: {name}");
    Console.WriteLine(new string('-', 50));
    
    try
    {
        await test();
        passed++;
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"✓ 通过");
        Console.ResetColor();
    }
    catch (Exception ex)
    {
        failed++;
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"✗ 失败: {ex.Message}");
        Console.ResetColor();
    }
}

void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

void AssertNotNull(object? obj, string message)
{
    if (obj == null) throw new Exception(message);
}
