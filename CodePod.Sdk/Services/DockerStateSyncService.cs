using CodePod.Sdk.Configuration;
using CodePod.Sdk.Models;
using CodePod.Sdk.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodePod.Sdk.Services;

/// <summary>
/// Docker 状态同步服务接口
/// </summary>
public interface IDockerStateSyncService
{
    /// <summary>
    /// 同步 Docker 容器状态到数据库
    /// </summary>
    Task SyncStateAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Docker 状态同步服务实现
/// 启动时将 Docker 中实际的容器状态同步到数据库
/// </summary>
public class DockerStateSyncService : IDockerStateSyncService
{
    private readonly IDockerService _dockerService;
    private readonly IDbContextFactory<CodePodDbContext> _contextFactory;
    private readonly CodePodConfig _config;
    private readonly ILogger<DockerStateSyncService>? _logger;

    public DockerStateSyncService(
        IDockerService dockerService,
        IDbContextFactory<CodePodDbContext> contextFactory,
        CodePodConfig config,
        ILogger<DockerStateSyncService>? logger = null)
    {
        _dockerService = dockerService;
        _contextFactory = contextFactory;
        _config = config;
        _logger = logger;
    }

    public async Task SyncStateAsync(CancellationToken cancellationToken = default)
    {
        _logger?.LogInformation("Starting Docker state synchronization...");

        // 获取 Docker 中实际的受管理容器
        List<ContainerInfo> dockerContainers = await _dockerService.GetManagedContainersAsync(cancellationToken);
        HashSet<string> dockerContainerIds = dockerContainers.Select(c => c.ContainerId).ToHashSet();

        _logger?.LogInformation("Found {Count} managed containers in Docker", dockerContainers.Count);

        // 获取数据库中的容器和会话记录
        List<ContainerEntity> dbContainers;
        List<SessionEntity> dbSessions;
        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            dbContainers = await context.Containers.ToListAsync(cancellationToken);
            dbSessions = await context.Sessions.Where(s => s.Status != SessionStatus.Destroyed).ToListAsync(cancellationToken);
        }

        HashSet<string> busyContainerIds = dbSessions
            .Where(s => s.Status == SessionStatus.Active && !string.IsNullOrEmpty(s.ContainerId))
            .Select(s => s.ContainerId!)
            .ToHashSet();

        HashSet<string> dbContainerIds = dbContainers.Select(c => c.ContainerId).ToHashSet();
        _logger?.LogInformation("Found {Count} containers in database", dbContainers.Count);

        // 1. 处理数据库中存在但 Docker 中不存在的容器（已被删除）
        List<string> deletedContainerIds = dbContainerIds.Except(dockerContainerIds).ToList();
        foreach (string containerId in deletedContainerIds)
        {
            _logger?.LogInformation("Container {ContainerId} no longer exists in Docker, removing from database",
                containerId.Length >= 12 ? containerId[..12] : containerId);

            await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);

            // 如果有关联的会话，标记为已销毁
            SessionEntity? session = await context.Sessions.FirstOrDefaultAsync(s => s.ContainerId == containerId, cancellationToken);
            if (session != null && session.Status != SessionStatus.Destroyed)
            {
                session.Status = SessionStatus.Destroyed;
                _logger?.LogInformation("Session {SessionId} marked as destroyed due to missing container", session.Id);
            }

            ContainerEntity? container = await context.Containers.FindAsync([containerId], cancellationToken);
            if (container != null)
            {
                context.Containers.Remove(container);
            }

            await context.SaveChangesAsync(cancellationToken);
        }

        // 2. 处理 Docker 中存在但数据库中不存在的容器（新发现或遗留）
        List<string> newContainerIds = dockerContainerIds.Except(dbContainerIds).ToList();
        foreach (string containerId in newContainerIds)
        {
            ContainerInfo dockerContainer = dockerContainers.First(c => c.ContainerId == containerId);
            bool isRunning = dockerContainer.DockerStatus == "running";

            if (isRunning)
            {
                // 运行中的容器：添加到数据库，根据是否被活动会话引用判断状态
                ContainerStatus status = busyContainerIds.Contains(containerId)
                    ? ContainerStatus.Busy
                    : ContainerStatus.Idle;

                await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                context.Containers.Add(ContainerEntity.FromModel(new ContainerInfo
                {
                    ContainerId = dockerContainer.ContainerId,
                    Name = dockerContainer.Name,
                    Image = dockerContainer.Image,
                    DockerStatus = dockerContainer.DockerStatus,
                    Status = status,
                    CreatedAt = dockerContainer.CreatedAt,
                    StartedAt = dockerContainer.StartedAt,
                    Labels = dockerContainer.Labels
                }));
                await context.SaveChangesAsync(cancellationToken);

                _logger?.LogInformation("Added container {ContainerId} to database with status {Status}",
                    dockerContainer.ShortId, status);

            }
            else
            {
                // 非运行中的容器：这是遗留容器，需要清理
                _logger?.LogInformation("Cleaning up non-running container {ContainerId} with status {Status}",
                    dockerContainer.ShortId, dockerContainer.DockerStatus);
                try
                {
                    await _dockerService.DeleteContainerAsync(containerId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete non-running container {ContainerId}", dockerContainer.ShortId);
                }
            }
        }

        // 3. 更新数据库中已存在容器的状态
        List<string> existingContainerIds = dbContainerIds.Intersect(dockerContainerIds).ToList();
        foreach (string containerId in existingContainerIds)
        {
            ContainerInfo dockerContainer = dockerContainers.First(c => c.ContainerId == containerId);
            ContainerEntity dbContainer = dbContainers.First(c => c.ContainerId == containerId);
            bool isRunning = dockerContainer.DockerStatus == "running";

            if (!isRunning)
            {
                // 容器已停止，需要清理
                _logger?.LogInformation("Container {ContainerId} is no longer running (status: {Status}), cleaning up",
                    dockerContainer.ShortId, dockerContainer.DockerStatus);

                await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);

                // 如果有关联的会话，标记为已销毁
                SessionEntity? session = await context.Sessions.FirstOrDefaultAsync(s => s.ContainerId == containerId && s.Status != SessionStatus.Destroyed, cancellationToken);
                if (session != null)
                {
                    session.Status = SessionStatus.Destroyed;
                    _logger?.LogInformation("Session {SessionId} marked as destroyed due to stopped container", session.Id);
                }

                ContainerEntity? container = await context.Containers.FindAsync([containerId], cancellationToken);
                if (container != null)
                {
                    context.Containers.Remove(container);
                }
                await context.SaveChangesAsync(cancellationToken);

                try
                {
                    await _dockerService.DeleteContainerAsync(containerId, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "Failed to delete stopped container {ContainerId}", dockerContainer.ShortId);
                }
            }
            else
            {
                // 容器运行中，同步状态
                ContainerStatus expectedStatus = busyContainerIds.Contains(containerId)
                    ? ContainerStatus.Busy
                    : ContainerStatus.Idle;

                bool needsUpdate = false;

                if (dbContainer.Status != expectedStatus || dbContainer.Status == ContainerStatus.Warming || dbContainer.Status == ContainerStatus.Destroying)
                {
                    _logger?.LogInformation("Updating container {ContainerId} status from {OldStatus} to {NewStatus}",
                        dockerContainer.ShortId, dbContainer.Status, expectedStatus);
                    needsUpdate = true;
                }

                if (needsUpdate)
                {
                    await using CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken);
                    ContainerEntity? container = await context.Containers.FindAsync([containerId], cancellationToken);
                    if (container != null)
                    {
                        container.Status = expectedStatus;
                        await context.SaveChangesAsync(cancellationToken);
                    }
                }
            }
        }

        // 4. 清理数据库中状态为 Destroying 或 Warming 的孤立记录（容器已不存在）
        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            List<ContainerEntity> orphanedContainers = await context.Containers
                .Where(c => (c.Status == ContainerStatus.Destroying || c.Status == ContainerStatus.Warming) &&
                           !dockerContainerIds.Contains(c.ContainerId))
                .ToListAsync(cancellationToken);

            foreach (ContainerEntity? container in orphanedContainers)
            {
                _logger?.LogInformation("Removing orphaned {Status} container record {ContainerId}",
                    container.Status, container.ContainerId[..Math.Min(12, container.ContainerId.Length)]);
                context.Containers.Remove(container);
            }
            await context.SaveChangesAsync(cancellationToken);
        }

        // 5. 清理孤立的活动会话（ContainerId 指向不存在的容器）
        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            List<SessionEntity> orphanedSessions = await context.Sessions
                .Where(s => s.Status != SessionStatus.Destroyed &&
                           s.ContainerId != null &&
                           !dockerContainerIds.Contains(s.ContainerId))
                .ToListAsync(cancellationToken);

            foreach (SessionEntity? session in orphanedSessions)
            {
                string containerIdShort = session.ContainerId != null
                    ? session.ContainerId[..Math.Min(12, session.ContainerId.Length)]
                    : "(null)";
                                _logger?.LogInformation("Session {SessionId} references non-existent container {ContainerId}, marking as destroyed",
                    session.Id, containerIdShort);

                session.Status = SessionStatus.Destroyed;
            }
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (CodePodDbContext context = await _contextFactory.CreateDbContextAsync(cancellationToken))
        {
            int finalCount = await context.Containers.CountAsync(cancellationToken);
            _logger?.LogInformation("Docker state synchronization completed. {Count} containers in database", finalCount);
        }
    }
}
