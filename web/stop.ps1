<# 
.SYNOPSIS
    停止 Docker Shell Host 服务

.DESCRIPTION
    此脚本停止后台运行的 Docker Shell Host 服务。
    会停止 DockerShellHost 进程及其父进程 dotnet.exe。

.EXAMPLE
    .\stop.ps1
#>

$ErrorActionPreference = "Stop"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$pidFile = Join-Path $scriptDir ".app.pid"
$processName = "DockerShellHost"

Write-Host "======================================" -ForegroundColor Cyan
Write-Host "  Docker Shell Host 停止脚本" -ForegroundColor Cyan
Write-Host "======================================" -ForegroundColor Cyan
Write-Host ""

$stoppedAny = $false

# 方法1: 通过 PID 文件停止
if (Test-Path $pidFile) {
    $appPid = Get-Content $pidFile -ErrorAction SilentlyContinue
    if ($appPid) {
        $process = Get-Process -Id $appPid -ErrorAction SilentlyContinue
        if ($process) {
            Write-Host "正在停止服务 (PID: $appPid, $($process.ProcessName))..." -ForegroundColor Gray
            Stop-Process -Id $appPid -Force -ErrorAction SilentlyContinue
            $stoppedAny = $true
        }
    }
    Remove-Item $pidFile -Force -ErrorAction SilentlyContinue
}

# 方法2: 按进程名查找并停止所有 DockerShellHost 实例
$processes = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($processes) {
    foreach ($proc in $processes) {
        Write-Host "正在停止残留进程 (PID: $($proc.Id))..." -ForegroundColor Yellow
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        $stoppedAny = $true
    }
}

# 方法3: 查找并停止 dotnet run 进程（通过命令行参数匹配项目路径）
$projectPath = Join-Path $scriptDir "DockerShellHost.csproj"
$dotnetProcesses = Get-CimInstance Win32_Process | Where-Object { 
    $_.Name -eq "dotnet.exe" -and $_.CommandLine -match [regex]::Escape($projectPath)
}
if ($dotnetProcesses) {
    foreach ($proc in $dotnetProcesses) {
        Write-Host "正在停止 dotnet 进程 (PID: $($proc.ProcessId))..." -ForegroundColor Yellow
        Stop-Process -Id $proc.ProcessId -Force -ErrorAction SilentlyContinue
        $stoppedAny = $true
    }
}

# 等待进程完全退出
if ($stoppedAny) {
    Write-Host "等待进程退出..." -ForegroundColor Gray
    $maxWait = 10
    for ($i = 0; $i -lt $maxWait; $i++) {
        Start-Sleep -Milliseconds 500
        $remaining = Get-Process -Name $processName -ErrorAction SilentlyContinue
        if (-not $remaining) {
            break
        }
        Write-Host "  仍有 $($remaining.Count) 个进程在运行..." -ForegroundColor Yellow
    }
    
    # 最终检查
    $remaining = Get-Process -Name $processName -ErrorAction SilentlyContinue
    if ($remaining) {
        Write-Host "警告: 仍有进程无法停止，尝试强制终止..." -ForegroundColor Red
        $remaining | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
    }
}

# 验证停止成功
$finalCheck = Get-Process -Name $processName -ErrorAction SilentlyContinue
if ($finalCheck) {
    Write-Host ""
    Write-Host "错误: 无法停止所有进程！" -ForegroundColor Red
    Write-Host "请手动运行: Stop-Process -Name $processName -Force" -ForegroundColor Red
    exit 1
}

Write-Host ""
if ($stoppedAny) {
    Write-Host "服务已停止" -ForegroundColor Green
} else {
    Write-Host "服务未在运行" -ForegroundColor Yellow
}
Write-Host ""
