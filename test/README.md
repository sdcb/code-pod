## Docker Shell Host Web API 测试

本目录包含用于测试 Docker Shell Host Web API 服务的测试文件。所有测试使用 .NET 10 单文件运行器语法。

### 前置条件

**必须先启动 Web 服务才能运行测试：**

```powershell
cd web
.\start.ps1
```

### 运行测试

```powershell
cd test
dotnet run <文件名>.cs
```

### 测试文件说明

| 文件 | 描述 |
|------|------|
| `DockerApiTest.cs` | Web API 完整测试套件：覆盖所有 API 端点 |
| `PerformanceTest.cs` | 性能测试：会话创建和命令执行的性能指标 |
| `SessionTimeoutTest.cs` | 超时测试：验证会话超时和自动清理机制 |

### 测试覆盖

1. **系统管理 API**
   - 获取系统状态
   - 容器列表和管理
   - 预热触发

2. **会话 API**
   - 会话创建/获取/销毁
   - 会话超时机制

3. **命令执行 API**
   - 基本命令执行
   - 错误处理
   - 多行输出

4. **文件操作 API**
   - 文件上传
   - 文件列表
   - 文件下载
   - 文件删除

### 停止服务

测试完成后停止服务：

```powershell
cd web
.\stop.ps1
```
