<# 
.SYNOPSIS
    启动 Docker Shell Host 服务（后台运行）

.DESCRIPTION
    此脚本启动 Docker Shell Host 服务，等待服务就绪后返回控制权。
    服务将在后台运行，输出日志到 logs/app.log 文件。

.EXAMPLE
    .\start.ps1
    .\start.ps1 -Port 5099
#>

param(
    [int]$Port = 5099,
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

# 创建日志目录
$logDir = Join-Path $scriptDir "logs"
if (-not (Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir | Out-Null
}

$logFile = Join-Path $logDir "app.log"
$pidFile = Join-Path $scriptDir ".app.pid"

# 检查是否已经在运行
if (Test-Path $pidFile) {
    $existingPid = Get-Content $pidFile -ErrorAction SilentlyContinue
    if ($existingPid) {
        $process = Get-Process -Id $existingPid -ErrorAction SilentlyContinue
        if ($process) {
            Write-Host "服务已在运行 (PID: $existingPid)" -ForegroundColor Yellow
            Write-Host "如需重启，请先运行 .\stop.ps1" -ForegroundColor Yellow
            exit 0
        }
    }
}

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Docker Shell Host 启动脚本" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

# 检查 .NET 运行时
Write-Host "[1/4] 检查 .NET 运行时..." -ForegroundColor Gray
$dotnetVersion = dotnet --version 2>$null
if (-not $dotnetVersion) {
    Write-Host "错误: 未找到 .NET 运行时，请安装 .NET 10 SDK" -ForegroundColor Red
    exit 1
}
Write-Host "  .NET 版本: $dotnetVersion" -ForegroundColor Green

# 检查 Docker
Write-Host "[2/4] 检查 Docker..." -ForegroundColor Gray
$dockerVersion = docker --version 2>$null
if (-not $dockerVersion) {
    Write-Host "错误: 未找到 Docker，请确保 Docker Desktop 已安装并运行" -ForegroundColor Red
    exit 1
}
Write-Host "  Docker 版本: $dockerVersion" -ForegroundColor Green

# 构建项目
Write-Host "[3/4] 构建项目..." -ForegroundColor Gray
$buildOutput = dotnet build -c Release 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "构建失败:" -ForegroundColor Red
    Write-Host $buildOutput -ForegroundColor Red
    exit 1
}
Write-Host "  构建成功" -ForegroundColor Green

# 启动服务（后台运行）
Write-Host "[4/4] 启动服务..." -ForegroundColor Gray

# 清空旧日志
"" | Out-File $logFile -Encoding UTF8

# 获取项目文件路径
$projectPath = Join-Path $scriptDir "DockerShellHost.csproj"

# 设置环境变量
$env:ASPNETCORE_URLS = "http://localhost:$Port"

# 直接启动 dotnet run 进程（后台运行，输出重定向到日志）
$dotnetProcess = Start-Process -FilePath "dotnet" `
    -ArgumentList "run", "--project", $projectPath, "-c", "Release", "--no-build", "--no-launch-profile" `
    -WorkingDirectory $scriptDir `
    -WindowStyle Hidden `
    -RedirectStandardOutput $logFile `
    -RedirectStandardError (Join-Path $logDir "error.log") `
    -PassThru

Write-Host "  dotnet 进程已启动 (PID: $($dotnetProcess.Id))" -ForegroundColor Gray
Write-Host ""

# 等待服务就绪
Write-Host "等待服务就绪..." -ForegroundColor Yellow
$startTime = Get-Date
$ready = $false

while (-not $ready) {
    $elapsed = ((Get-Date) - $startTime).TotalSeconds
    
    if ($elapsed -gt $TimeoutSeconds) {
        Write-Host ""
        Write-Host "错误: 服务启动超时 ($TimeoutSeconds 秒)" -ForegroundColor Red
        Write-Host "查看日志: Get-Content $logFile -Tail 50" -ForegroundColor Yellow
        exit 1
    }
    
    # 检查 dotnet 进程是否还在
    $runningProcess = Get-Process -Id $dotnetProcess.Id -ErrorAction SilentlyContinue
    if (-not $runningProcess) {
        Write-Host ""
        Write-Host "错误: dotnet 进程已退出" -ForegroundColor Red
        Write-Host "查看日志: Get-Content $logFile -Tail 50" -ForegroundColor Yellow
        exit 1
    }
    
    # 检查 HTTP 端点
    try {
        $response = Invoke-WebRequest -Uri "http://localhost:$Port/api/admin/status" `
            -UseBasicParsing -TimeoutSec 2 -ErrorAction SilentlyContinue
        if ($response.StatusCode -eq 200) {
            $ready = $true
        }
    } catch {
        # 继续等待
        Write-Host -NoNewline "." -ForegroundColor Gray
        Start-Sleep -Milliseconds 500
    }
}

# 查找实际的 DockerShellHost 进程并保存 PID
$appProcess = Get-Process -Name "DockerShellHost" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($appProcess) {
    $appProcess.Id | Out-File $pidFile -Encoding UTF8
    $actualPid = $appProcess.Id
} else {
    # 找不到实际进程，保存 dotnet 进程 PID 作为后备
    $dotnetProcess.Id | Out-File $pidFile -Encoding UTF8
    $actualPid = $dotnetProcess.Id
}

Write-Host ""
Write-Host ""
Write-Host "======================================" -ForegroundColor Green
Write-Host "  服务已就绪！" -ForegroundColor Green
Write-Host "======================================" -ForegroundColor Green
Write-Host ""
Write-Host "  地址: http://localhost:$Port" -ForegroundColor Cyan
Write-Host "  API:  http://localhost:$Port/api/admin/status" -ForegroundColor Cyan
Write-Host "  PID:  $actualPid" -ForegroundColor Gray
Write-Host "  日志: $logFile" -ForegroundColor Gray
Write-Host ""
Write-Host "使用 .\stop.ps1 停止服务" -ForegroundColor Yellow
Write-Host ""
