using Microsoft.Extensions.Options;
using SecondBrain.Mcp.Configuration;
using SecondBrain.Mcp.Endpoints;
using SecondBrain.Mcp.Services;
using Serilog;
using Serilog.Events;

var configPath = Environment.GetEnvironmentVariable("MCP_CONFIG_PATH")
    ?? Path.Combine(AppContext.BaseDirectory, "mcp_config.json");

var mcpSettings = McpSettings.Load(configPath);

var loggerConfig = new LoggerConfiguration();

if (mcpSettings.EnableLogging)
{
    var logLevel = mcpSettings.LogLevel.ToUpperInvariant() switch
    {
        "DEBUG" => LogEventLevel.Debug,
        "WARNING" => LogEventLevel.Warning,
        "ERROR" => LogEventLevel.Error,
        "CRITICAL" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };

    var logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    Directory.CreateDirectory(logDir);
    var logFile = Path.Combine(logDir, $"second_brain_{DateTime.Now:yyyyMMdd_HHmmss}.log");

    loggerConfig.MinimumLevel.Is(logLevel).WriteTo.Console().WriteTo.File(logFile);
}
else
{
    loggerConfig.MinimumLevel.Fatal();
}

Log.Logger = loggerConfig.CreateLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog();
    builder.Host.UseWindowsService(options =>
    {
        options.ServiceName = mcpSettings.ServiceName;
    });

    builder.Services.Configure<McpSettings>(opts =>
    {
        opts.ServiceName = mcpSettings.ServiceName;
        opts.DisplayName = mcpSettings.DisplayName;
        opts.Description = mcpSettings.Description;
        opts.HttpHost = mcpSettings.HttpHost;
        opts.HttpPort = mcpSettings.HttpPort;
        opts.McpTimeout = mcpSettings.McpTimeout;
        opts.LogLevel = mcpSettings.LogLevel;
        opts.EnableLogging = mcpSettings.EnableLogging;
        opts.SecondBrain = mcpSettings.SecondBrain;
    });

    builder.Services.AddSingleton<McpServiceState>();
    builder.Services.AddHostedService<McpHostedService>();
    builder.Services.AddHostedService<IndexRefreshService>();

    builder.WebHost.ConfigureKestrel(options =>
    {
        options.ListenAnyIP(mcpSettings.HttpPort);
    });

    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
            policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
    });

    var app = builder.Build();
    app.UseCors();
    app.MapMcpEndpoints();
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
