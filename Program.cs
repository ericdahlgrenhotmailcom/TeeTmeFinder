
using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TeeTmeFinder.Server;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<MonitorState>();
builder.Services.AddHttpClient("monitor");
builder.Services.AddHostedService<GolfMonitorService>();
builder.Services.AddLogging();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

var app = builder.Build();

app.UseCors();

// Simple API key protection for start/stop endpoints
var configuredApiKey = builder.Configuration["MonitorApiKey"] ?? Environment.GetEnvironmentVariable("MONITOR_API_KEY");
if (string.IsNullOrEmpty(configuredApiKey))
{
    var logger = app.Services.GetService<Microsoft.Extensions.Logging.ILoggerFactory>()?.CreateLogger("Startup");
    logger?.LogWarning("MonitorApiKey not configured. Start/Stop endpoints will be unprotected.");
}

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? string.Empty;
    if (!string.IsNullOrEmpty(configuredApiKey) &&
        (path.Equals("/api/monitor/start", StringComparison.OrdinalIgnoreCase) || path.Equals("/api/monitor/stop", StringComparison.OrdinalIgnoreCase)))
    {
        if (!context.Request.Headers.TryGetValue("X-Api-Key", out var header) || header != configuredApiKey)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized");
            return;
        }
    }

    await next();
});

app.MapGet("/api/monitor/status", (MonitorState state) =>
{
    return Results.Ok(new
    {
        isActive = state.IsActive,
        startDate = state.StartDate,
        endDate = state.EndDate,
        minTime = state.MinTime,
        maxTime = state.MaxTime,
        requiredSlots = state.RequiredSlots,
        webhookUrl = state.WebhookUrl,
        logs = state.Logs
    });
});

app.MapPost("/api/monitor/start", async (MonitorState state, StartRequest? dto) =>
{
    if (dto == null) return Results.BadRequest();
    state.StartDate = dto.StartDate;
    state.EndDate = dto.EndDate;
    state.MinTime = dto.MinTime;
    state.MaxTime = dto.MaxTime;
    state.RequiredSlots = dto.RequiredSlots;
    state.WebhookUrl = dto.WebhookUrl ?? string.Empty;
    state.IsActive = true;
    state.AddLog("Monitor started via API.");
    _ = state.SaveToFileAsync("monitorstate.json");
    return Results.Ok();
});

app.MapPost("/api/monitor/stop", (MonitorState state) =>
{
    state.IsActive = false;
    state.AddLog("Monitor stopped via API.");
    _ = state.SaveToFileAsync("monitorstate.json");
    return Results.Ok();
});

app.Run();

// StartRequest defined in Controllers/MonitorController.cs
