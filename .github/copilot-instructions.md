# Docker Shell Host - GitHub Copilot Instructions

## 语言规范

- **对话语言**：与用户对话时使用中文
- **代码语言**：代码、错误信息、API 响应等使用英文
- **文档和注释**：可以使用中文

## 项目概述

这是一个基于 ASP.NET Core 的 Docker 容器管理平台，专为大模型 Code Interpreter 和高级数据分析场景设计。

## 技术栈

- .NET 10
- ASP.NET Core Web API
- Docker.DotNet（Docker Engine API 客户端）
- SharpCompress（TAR 归档处理）
- Bootstrap 5（前端 UI）
- SignalR（实时状态更新）

## 项目结构

```
csharp-docker-manager/
├── exp/                          # Docker 底层实验文件
│   ├── DockerBasics.cs          # Docker 基础操作实验
│   ├── DockerFileWorkflow.cs    # 文件操作实验
│   ├── DockerPoolSimulation.cs  # 池化管理实验
│   └── DockerCompleteTest.cs    # 完整功能验证
├── test/                        # Web API 测试文件
│   ├── DockerApiTest.cs         # API 完整测试（推荐运行）
│   ├── PerformanceTest.cs       # 性能测试
│   └── SessionTimeoutTest.cs    # 超时测试
└── web/                         # ASP.NET Core 项目
    ├── Configuration/           # 配置类
    ├── Controllers/             # API 控制器
    ├── Exceptions/              # 自定义异常类
    ├── Middleware/              # 中间件（异常处理等）
    ├── Models/                  # 数据模型
    ├── Services/                # 业务服务
    ├── Hubs/                    # SignalR Hub
    ├── wwwroot/                 # 前端静态文件
    ├── start.ps1                # 启动脚本
    └── stop.ps1                 # 停止脚本
```

## 开发工作流

### 启动/停止服务

```powershell
# 启动服务（推荐使用脚本）
cd web
.\start.ps1

# 停止服务
.\stop.ps1
```

脚本特点：
- 自动检查 Docker 是否运行
- 构建项目
- 后台启动服务并记录 PID
- 优雅处理 Ctrl+C 中断
- 日志输出到 `web/logs/app.log`

### 运行 API 测试

```powershell
cd test
dotnet run DockerApiTest.cs
```

测试覆盖：
1. 系统状态获取
2. 会话创建/获取/销毁
3. 命令执行（基本输出、错误处理、多行输出）
4. 文件上传（表单方式）
5. 文件列表
6. 文件下载
7. 文件删除
8. 容器列表
9. 预热补充逻辑

## API 端点

### 管理员 API (`/api/admin`)

| 方法 | 路径 | 描述 |
|------|------|------|
| GET | `/status` | 获取系统状态 |
| GET | `/containers` | 获取所有容器 |
| POST | `/containers` | 创建预热容器 |
| DELETE | `/containers/{id}` | 删除容器 |
| DELETE | `/containers` | 删除所有容器 |
| GET | `/sessions` | 获取所有会话 |
| POST | `/prewarm` | 触发预热 |

### 会话 API (`/api/sessions`)

| 方法 | 路径 | 描述 |
|------|------|------|
| GET | `/` | 获取所有会话 |
| POST | `/` | 创建新会话 |
| GET | `/{sessionId}` | 获取会话详情 |
| DELETE | `/{sessionId}` | 销毁会话 |

### 命令 API (`/api/sessions/{sessionId}/commands`)

| 方法 | 路径 | 描述 |
|------|------|------|
| POST | `/` | 执行命令（非流式响应） |
| POST | `/stream` | 执行命令（SSE 流式响应） |

#### SSE 流式命令执行

`POST /api/sessions/{sessionId}/commands/stream` 返回 Server-Sent Events 流，实时输出命令的 stdout/stderr。

**请求体：**
```json
{
  "command": "your-command",
  "workingDirectory": "/app",
  "timeoutSeconds": 60
}
```

**SSE 事件类型：**
- `stdout`: 标准输出数据，`data` 字段包含输出内容
- `stderr`: 标准错误数据，`data` 字段包含错误内容
- `exit`: 命令完成，包含 `exitCode` 和 `executionTimeMs`

**示例响应：**
```
event: stdout
data: {"data":"Hello World\n"}

event: stderr
data: {"data":"Warning message\n"}

event: exit
data: {"exitCode":0,"executionTimeMs":1234}
```

### 文件 API (`/api/sessions/{sessionId}/files`)

| 方法 | 路径 | 描述 |
|------|------|------|
| GET | `/list?path=/app` | 列出目录 |
| POST | `/upload?targetPath=/app` | 上传文件（表单方式） |
| GET | `/download?path=/app/file` | 下载文件 |
| DELETE | `?path=/app/file` | 删除文件 |

## 文件上传方式

使用表单上传（Form Post），效率高，支持大文件，二进制安全：

```bash
curl -X POST "http://localhost:5099/api/sessions/{sessionId}/files/upload?targetPath=/app" \
  -F "file=@hello.cs"
```

## 异常处理

项目使用自定义异常类和全局异常处理中间件：

### 自定义异常类型

| 异常类型 | HTTP 状态码 | 错误代码 | 描述 |
|----------|-------------|----------|------|
| `DockerConnectionException` | 503 | `DOCKER_CONNECTION_ERROR` | Docker 服务连接失败 |
| `ContainerNotFoundException` | 404 | `CONTAINER_NOT_FOUND` | 容器不存在或已被删除 |
| `DockerOperationException` | 500 | `DOCKER_OPERATION_ERROR` | Docker 操作失败 |

### 错误响应格式

```json
{
  "success": false,
  "error": "Unable to connect to Docker service. Please ensure Docker Desktop is running.",
  "errorInfo": {
    "code": "DOCKER_CONNECTION_ERROR",
    "message": "Unable to connect to Docker service. Please ensure Docker Desktop is running.",
    "details": "Please ensure Docker Desktop is running"
  },
  "timestamp": "2025-12-16T06:00:00Z"
}
```

## 配置项

`appsettings.json`:

```json
{
  "DockerPool": {
    "Image": "mcr.microsoft.com/dotnet/sdk:10.0",
    "PrewarmCount": 2,
    "MaxContainers": 10,
    "SessionTimeoutMinutes": 5,
    "WorkDir": "/app",
    "LabelPrefix": "dsh"
  }
}
```

## 代码规范

1. 所有 Docker 操作应通过 `IDockerService` 接口
2. 使用 `WrapDockerOperationAsync` 包装 Docker 操作以获得统一的错误处理
3. 文件上传使用表单方式 (`upload`)
4. 长时间运行的操作应支持 `CancellationToken`
5. 使用日志记录关键操作
6. **运行时错误信息使用英文**

## 测试指南

### ⚠️ 重要：必须使用脚本启动服务

**绝对不要直接使用 `dotnet run` 启动服务进行测试！** 原因如下：

1. **终端阻塞问题**：`dotnet run` 会阻塞当前终端，导致无法在同一终端运行测试代码
2. **多终端冲突**：如果在另一个终端运行 `dotnet run`，会因为端口占用或文件锁导致冲突
3. **进程管理困难**：没有 PID 记录，难以可靠地停止服务

**正确的测试流程：**

```powershell
# 1. 启动服务（使用脚本，后台运行）
cd web
.\start.ps1

# 2. 运行测试（在同一终端即可）
cd ..\test
dotnet run DockerApiTest.cs

# 3. 查看服务日志（如需调试）
Get-Content ..\web\logs\app.log -Tail 50

# 4. 停止服务
cd ..\web
.\stop.ps1
```

### start.ps1 脚本的优势

| 特性 | `start.ps1` | `dotnet run` |
|------|-------------|--------------|
| 后台运行 | ✅ 不阻塞终端 | ❌ 阻塞终端 |
| 日志管理 | ✅ 输出到 logs/app.log | ❌ 混杂在终端 |
| 进程管理 | ✅ 记录 PID，可靠停止 | ❌ 需手动 Ctrl+C |
| 健康检查 | ✅ 等待服务就绪 | ❌ 无 |
| 重复启动保护 | ✅ 检测已运行实例 | ❌ 无 |
| 固定端口 | ✅ 默认 5099 | ❌ 取决于 launchSettings |

### 脚本详情

**start.ps1**：
- 检查 .NET 和 Docker 环境
- 构建项目（Release 模式）
- 后台启动服务，监听 `http://localhost:5099`
- 日志输出到 `web/logs/app.log`
- 等待服务健康检查通过后返回

**stop.ps1**：
- 通过 PID 文件停止服务
- 清理所有残留的 DockerShellHost 进程
