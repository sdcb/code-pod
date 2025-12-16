## Docker Shell Host 实验

本目录包含用于验证 Docker 底层功能的实验文件。这些实验直接使用 Docker.DotNet 库，不依赖 Web API 服务。

所有实验使用 .NET 10 单文件运行器语法。

### 运行方式

```powershell
dotnet run <文件名>.cs
```

### 实验文件说明

| 文件 | 描述 |
|------|------|
| `DockerBasics.cs` | 基础 Docker 操作：连接、创建、启动、检查和删除容器 |
| `DockerFileWorkflow.cs` | 文件操作：上传文件、列出目录、下载文件（无需 CLI） |
| `DockerPoolSimulation.cs` | 容器池管理：预热、分配、队列和释放 |
| `DockerCompleteTest.cs` | 完整功能验证：验证所有核心功能包括 .NET 代码执行 |

> **注意**: Web API 测试文件已移至 `/test` 目录

### 使用的镜像

所有实验使用 `mcr.microsoft.com/dotnet/sdk:10.0` 镜像，支持在容器内执行.NET代码。

### 依赖包

- `Docker.DotNet` - Docker Engine API客户端
- `SharpCompress` - TAR归档处理（用于文件上传/下载）

### 核心功能演示

1. **容器生命周期管理**
   - 创建、启动、停止、删除容器
   - 标签管理用于追踪

2. **命令执行**
   - 在容器内执行Shell命令
   - 获取stdout/stderr输出

3. **文件操作**
   - 上传文件到容器
   - 列出容器内目录
   - 从容器下载文件

4. **池化管理**
   - 预热容器减少冷启动延迟
   - 最大容器数量限制
   - 排队机制
