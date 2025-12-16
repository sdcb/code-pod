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

## 许可证

[MIT License](LICENSE)
