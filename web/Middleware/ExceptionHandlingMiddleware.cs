using DockerShellHost.Exceptions;
using DockerShellHost.Models;
using System.Net;
using System.Text.Json;

namespace DockerShellHost.Middleware;

/// <summary>
/// 全局异常处理中间件
/// </summary>
public class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;

    public ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            await HandleExceptionAsync(context, ex);
        }
    }

    private async Task HandleExceptionAsync(HttpContext context, Exception exception)
    {
        var (statusCode, errorMessage, errorCode) = exception switch
        {
            DockerConnectionException => (HttpStatusCode.ServiceUnavailable, exception.Message, "DOCKER_CONNECTION_ERROR"),
            ContainerNotFoundException => (HttpStatusCode.NotFound, exception.Message, "CONTAINER_NOT_FOUND"),
            DockerOperationException => (HttpStatusCode.InternalServerError, exception.Message, "DOCKER_OPERATION_ERROR"),
            OperationCanceledException => (HttpStatusCode.RequestTimeout, "Operation timed out", "OPERATION_TIMEOUT"),
            ArgumentException => (HttpStatusCode.BadRequest, exception.Message, "INVALID_ARGUMENT"),
            FileNotFoundException => (HttpStatusCode.NotFound, exception.Message, "FILE_NOT_FOUND"),
            _ => (HttpStatusCode.InternalServerError, "Internal server error", "INTERNAL_ERROR")
        };

        _logger.LogError(exception, "Request processing failed: {ErrorCode} - {Message}", errorCode, exception.Message);

        context.Response.ContentType = "application/json";
        context.Response.StatusCode = (int)statusCode;

        var errorInfo = new ErrorInfo
        {
            Code = errorCode,
            Message = errorMessage,
            Details = exception is DockerConnectionException ? "Please ensure Docker Desktop is running" : null
        };

        var response = new ApiResponse<object>
        {
            Success = false,
            Error = errorMessage,
            ErrorInfo = errorInfo
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        await context.Response.WriteAsJsonAsync(response, options);
    }
}

/// <summary>
/// 中间件扩展方法
/// </summary>
public static class ExceptionHandlingMiddlewareExtensions
{
    public static IApplicationBuilder UseExceptionHandling(this IApplicationBuilder builder)
    {
        return builder.UseMiddleware<ExceptionHandlingMiddleware>();
    }
}
