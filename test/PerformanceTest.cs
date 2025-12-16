// Docker Shell Host 性能测试 - 会话创建和命令执行
// 使用 .NET 10 single-file runner 语法。执行命令: dotnet run PerformanceTest.cs
// 确保 Docker Shell Host 服务正在运行

using System.Diagnostics;
using System.Text;
using System.Text.Json;

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5099";
var testRounds = args.Length > 1 && int.TryParse(args[1], out var r) ? r : 5;

using var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(120) };

Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
Console.WriteLine("║       Docker Shell Host 性能测试                              ║");
Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
Console.WriteLine($"  服务地址: {baseUrl}");
Console.WriteLine($"  测试轮数: {testRounds}");
Console.WriteLine();

// 存储每轮测试结果
var sessionCreateTimes = new List<double>();
var commandExecTimes = new List<double>();
var totalTimes = new List<double>();
var createdSessions = new List<string>();

try
{
    // 先检查服务是否可用
    Console.WriteLine("🔍 检查服务状态...");
    try
    {
        var statusJson = await client.GetStringAsync("/api/admin/status");
        var statusDoc = JsonDocument.Parse(statusJson);
        var data = statusDoc.RootElement.GetProperty("data");
        Console.WriteLine($"  ✓ 服务正常，可用容器: {data.GetProperty("availableContainers").GetInt32()}");
        Console.WriteLine();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ✗ 服务不可用: {ex.Message}");
        return;
    }

    // 开始测试
    for (int round = 1; round <= testRounds; round++)
    {
        Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.WriteLine($"📋 第 {round}/{testRounds} 轮测试");
        Console.WriteLine($"━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        
        var totalSw = Stopwatch.StartNew();
        
        // ============================================================================
        // 步骤 1: 创建会话
        // ============================================================================
        Console.WriteLine("\n📦 步骤 1: 创建会话...");
        var createSw = Stopwatch.StartNew();
        
        var createContent = new StringContent($"{{\"name\": \"性能测试-{round}\"}}", Encoding.UTF8, "application/json");
        var createResponse = await client.PostAsync("/api/sessions", createContent);
        createResponse.EnsureSuccessStatusCode();
        
        var createJson = await createResponse.Content.ReadAsStringAsync();
        var createDoc = JsonDocument.Parse(createJson);
        var createData = createDoc.RootElement.GetProperty("data");
        var sessionId = createData.GetProperty("sessionId").GetString()!;
        createdSessions.Add(sessionId);
        
        // 等待会话就绪（容器分配完成）
        var sessionReady = false;
        string? containerId = null;
        
        for (int i = 0; i < 60; i++) // 最多等待60秒
        {
            var sessionJson = await client.GetStringAsync($"/api/sessions/{sessionId}");
            var sessionDoc = JsonDocument.Parse(sessionJson);
            var sessionData = sessionDoc.RootElement.GetProperty("data");
            
            if (sessionData.TryGetProperty("containerId", out var cid) && cid.ValueKind != JsonValueKind.Null)
            {
                containerId = cid.GetString();
                sessionReady = true;
                break;
            }
            
            await Task.Delay(100); // 每100ms检查一次
        }
        
        createSw.Stop();
        var sessionCreateTime = createSw.Elapsed.TotalMilliseconds;
        sessionCreateTimes.Add(sessionCreateTime);
        
        if (!sessionReady)
        {
            Console.WriteLine($"  ✗ 会话创建超时！");
            continue;
        }
        
        Console.WriteLine($"  ✓ 会话已就绪");
        Console.WriteLine($"    会话ID: {sessionId}");
        Console.WriteLine($"    容器ID: {containerId?[..12]}...");
        Console.WriteLine($"    耗时: {sessionCreateTime:F2} ms");
        
        // ============================================================================
        // 步骤 2: 执行命令
        // ============================================================================
        Console.WriteLine("\n⚡ 步骤 2: 执行命令...");
        var execSw = Stopwatch.StartNew();
        
        var cmdContent = new StringContent("{\"command\": \"echo 'Hello World' && date\"}", Encoding.UTF8, "application/json");
        var cmdResponse = await client.PostAsync($"/api/sessions/{sessionId}/commands", cmdContent);
        cmdResponse.EnsureSuccessStatusCode();
        
        execSw.Stop();
        var commandExecTime = execSw.Elapsed.TotalMilliseconds;
        commandExecTimes.Add(commandExecTime);
        
        var cmdJson = await cmdResponse.Content.ReadAsStringAsync();
        var cmdDoc = JsonDocument.Parse(cmdJson);
        var cmdData = cmdDoc.RootElement.GetProperty("data");
        
        var stdout = cmdData.GetProperty("stdout").GetString()?.Trim();
        var exitCode = cmdData.GetProperty("exitCode").GetInt32();
        var serverExecTime = cmdData.GetProperty("executionTimeMs").GetInt64();
        
        Console.WriteLine($"  ✓ 命令执行完成");
        Console.WriteLine($"    输出: {stdout}");
        Console.WriteLine($"    退出码: {exitCode}");
        Console.WriteLine($"    客户端耗时: {commandExecTime:F2} ms");
        Console.WriteLine($"    服务端执行: {serverExecTime} ms");
        
        totalSw.Stop();
        var totalTime = totalSw.Elapsed.TotalMilliseconds;
        totalTimes.Add(totalTime);
        
        Console.WriteLine($"\n  📊 本轮总耗时: {totalTime:F2} ms");
        Console.WriteLine();
    }

    // ============================================================================
    // 统计结果
    // ============================================================================
    Console.WriteLine();
    Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine("║                       📊 测试统计结果                         ║");
    Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
    Console.WriteLine();
    
    if (sessionCreateTimes.Count > 0)
    {
        Console.WriteLine("┌────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ 会话创建时间 (ms)                                              │");
        Console.WriteLine("├────────────────────────────────────────────────────────────────┤");
        Console.WriteLine($"│   最小值:   {sessionCreateTimes.Min(),10:F2} ms                          │");
        Console.WriteLine($"│   最大值:   {sessionCreateTimes.Max(),10:F2} ms                          │");
        Console.WriteLine($"│   平均值:   {sessionCreateTimes.Average(),10:F2} ms                          │");
        if (sessionCreateTimes.Count > 1)
        {
            var stdDev = Math.Sqrt(sessionCreateTimes.Sum(x => Math.Pow(x - sessionCreateTimes.Average(), 2)) / sessionCreateTimes.Count);
            Console.WriteLine($"│   标准差:   {stdDev,10:F2} ms                          │");
        }
        Console.WriteLine("└────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();
    }
    
    if (commandExecTimes.Count > 0)
    {
        Console.WriteLine("┌────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ 命令执行时间 (ms)                                              │");
        Console.WriteLine("├────────────────────────────────────────────────────────────────┤");
        Console.WriteLine($"│   最小值:   {commandExecTimes.Min(),10:F2} ms                          │");
        Console.WriteLine($"│   最大值:   {commandExecTimes.Max(),10:F2} ms                          │");
        Console.WriteLine($"│   平均值:   {commandExecTimes.Average(),10:F2} ms                          │");
        if (commandExecTimes.Count > 1)
        {
            var stdDev = Math.Sqrt(commandExecTimes.Sum(x => Math.Pow(x - commandExecTimes.Average(), 2)) / commandExecTimes.Count);
            Console.WriteLine($"│   标准差:   {stdDev,10:F2} ms                          │");
        }
        Console.WriteLine("└────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();
    }
    
    if (totalTimes.Count > 0)
    {
        Console.WriteLine("┌────────────────────────────────────────────────────────────────┐");
        Console.WriteLine("│ 总时间（创建+执行）(ms)                                        │");
        Console.WriteLine("├────────────────────────────────────────────────────────────────┤");
        Console.WriteLine($"│   最小值:   {totalTimes.Min(),10:F2} ms                          │");
        Console.WriteLine($"│   最大值:   {totalTimes.Max(),10:F2} ms                          │");
        Console.WriteLine($"│   平均值:   {totalTimes.Average(),10:F2} ms                          │");
        if (totalTimes.Count > 1)
        {
            var stdDev = Math.Sqrt(totalTimes.Sum(x => Math.Pow(x - totalTimes.Average(), 2)) / totalTimes.Count);
            Console.WriteLine($"│   标准差:   {stdDev,10:F2} ms                          │");
        }
        Console.WriteLine("└────────────────────────────────────────────────────────────────┘");
        Console.WriteLine();
    }
    
    // 各轮详细数据
    Console.WriteLine("┌────────────────────────────────────────────────────────────────┐");
    Console.WriteLine("│ 各轮详细数据                                                   │");
    Console.WriteLine("├──────┬──────────────┬──────────────┬──────────────────────────┤");
    Console.WriteLine("│ 轮次 │ 会话创建(ms) │ 命令执行(ms) │     总计(ms)             │");
    Console.WriteLine("├──────┼──────────────┼──────────────┼──────────────────────────┤");
    for (int i = 0; i < totalTimes.Count; i++)
    {
        Console.WriteLine($"│  {i + 1,2}  │ {sessionCreateTimes[i],12:F2} │ {commandExecTimes[i],12:F2} │ {totalTimes[i],12:F2}             │");
    }
    Console.WriteLine("└──────┴──────────────┴──────────────┴──────────────────────────┘");
}
finally
{
    // ============================================================================
    // 清理：删除测试创建的会话（并行清理）
    // ============================================================================
    Console.WriteLine();
    Console.WriteLine("🧹 清理测试会话...");
    
    var cleanupTasks = createdSessions.Select(async sessionId =>
    {
        try
        {
            await client.DeleteAsync($"/api/sessions/{sessionId}");
            Console.WriteLine($"  ✓ 已删除会话: {sessionId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ 删除会话失败 {sessionId}: {ex.Message}");
        }
    });
    
    await Task.WhenAll(cleanupTasks);
    
    Console.WriteLine();
    Console.WriteLine("✅ 测试完成！");
}
