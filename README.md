# Docker Shell Host

一个基于 ASP.NET Core 的 Docker 容器管理平台，专为 AI 大模型 Code Interpreter 和高级数据分析场景设计。

通过提供安全隔离的容器环境，让 AI 能够安全地执行代码、处理文件和运行数据分析任务。

## 功能特性

- **容器池化管理**：预热容器机制，减少冷启动延迟
- **会话管理**：支持多用户会话，自动超时清理
- **文件操作**：上传、下载、浏览容器内文件
- **命令执行**：在容器内执行 Shell 命令，支持流式输出（SSE）
- **实时状态**：通过 SignalR 推送系统状态更新

## 技术栈

- .NET 10 / ASP.NET Core Web API
- Docker.DotNet（Docker Engine API 客户端）
- SignalR（实时通信）
- Bootstrap 5（管理界面）

## 快速开始

### 前置要求

- .NET 10 SDK
- Docker Desktop（运行中）

### 启动服务

```powershell
cd web
.\start.ps1
```

访问 http://localhost:5099 打开管理界面。

### 停止服务

```powershell
cd web
.\stop.ps1
```

## 配置说明

配置文件位于 `web/appsettings.json`，主要配置项如下：

```json
{
  "DockerPool": {
    "Image": "mcr.microsoft.com/dotnet/sdk:10.0",
    "PrewarmCount": 2,
    "MaxContainers": 10,
    "SessionTimeoutSeconds": 180,
    "WorkDir": "/app",
    "LabelPrefix": "dsh"
  }
}
```

### 配置项说明

| 配置项 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| `Image` | string | `mcr.microsoft.com/dotnet/sdk:10.0` | Docker 镜像名称，可更换为其他镜像 |
| `PrewarmCount` | int | `2` | 预热容器数量，用于快速分配会话 |
| `MaxContainers` | int | `10` | 最大容器数量限制，防止资源耗尽 |
| `SessionTimeoutSeconds` | int | `180` | 会话超时时间（秒），超时后自动清理 |
| `WorkDir` | string | `/app` | 容器内工作目录 |
| `LabelPrefix` | string | `dsh` | 容器标签前缀，用于识别和管理容器 |

### 自定义配置

根据实际需求，可以调整以下配置：

- **性能优化**：增加 `PrewarmCount` 提高并发响应速度
- **资源限制**：调整 `MaxContainers` 控制资源使用
- **超时控制**：修改 `SessionTimeoutSeconds` 适应不同场景
- **镜像切换**：更改 `Image` 使用特定环境（如 Python、Node.js）

## 许可证

[MIT License](LICENSE)
