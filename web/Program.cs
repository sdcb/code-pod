using System.Text.Json.Serialization;
using DockerShellHost.Configuration;
using DockerShellHost.Hubs;
using DockerShellHost.Middleware;
using DockerShellHost.Services;

var builder = WebApplication.CreateBuilder(args);

// 配置
builder.Services.Configure<DockerPoolConfig>(builder.Configuration.GetSection("DockerPool"));

// 服务注册
builder.Services.AddSingleton<IDockerClientFactory, DockerClientFactory>();
builder.Services.AddSingleton<IDockerService, DockerService>();
builder.Services.AddSingleton<IDockerPoolService, DockerPoolService>();
builder.Services.AddSingleton<ISessionService, SessionService>();

// 后台服务
builder.Services.AddHostedService<SessionCleanupService>();
builder.Services.AddHostedService<PrewarmService>();

// 控制器和API - 枚举序列化为字符串
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });
builder.Services.AddOpenApi();

// SignalR - 枚举序列化为字符串
builder.Services.AddSignalR()
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

// 静态文件和Razor Pages
builder.Services.AddRazorPages();

// CORS（开发用）
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

// 异常处理中间件（放在最前面）
app.UseExceptionHandling();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
app.UseCors();
app.UseRouting();
app.UseAuthorization();

app.MapControllers();
app.MapHub<StatusHub>("/hubs/status");
app.MapRazorPages();
app.MapFallbackToFile("index.html");

app.Run();
