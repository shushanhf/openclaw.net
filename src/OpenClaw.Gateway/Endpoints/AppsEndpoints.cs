using System.Text;
using System.Text.Json.Nodes;
using OpenClaw.Core.Models;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Composition;
using OpenClaw.McpApp;

namespace OpenClaw.Gateway.Endpoints;

internal static class AppsEndpoints
{
    public static void MapOpenClawAppsEndpoints(
        this WebApplication app,
        GatewayStartupContext startup,
        GatewayAppRuntime runtime)
    {
        app.MapGet("/apps/health", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!AppsAuthorized(ctx, startup))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Results.Empty;
            }

            var registry = ctx.RequestServices.GetRequiredService<McpAppRegistry>();
            await registry.LoadAllAsync(ct);

            var selectedApp = registry.Apps.FirstOrDefault(app => app.Client is not null && app.HasUi)
                ?? registry.Apps.FirstOrDefault(app => app.Client is not null);

            var payload = new JsonObject
            {
                ["ok"] = selectedApp is not null,
                ["name"] = selectedApp?.AppId ?? "mcpapp",
                ["mcp"] = selectedApp is null ? null : BuildAppMcpUrl(ctx, selectedApp.AppId),
                ["sandbox"] = "http://localhost:3101/sandbox.html",
            };

            return Results.Content(payload.ToJsonString(), "application/json");
        });

        app.MapPost("/apps/chat", async (HttpContext ctx, CancellationToken ct) =>
        {
            if (!AppsAuthorized(ctx, startup))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            JsonNode? body;
            try
            {
                body = await JsonNode.ParseAsync(ctx.Request.Body, cancellationToken: ct);
            }
            catch
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            var message = AsString(body?["message"]);
            var requestedSessionId = AsString(body?["sessionId"]);
            var appEvents = body?["appEvents"] as JsonArray;
            var userText = BuildPrompt(message, appEvents);
            if (string.IsNullOrWhiteSpace(userText))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Connection = "keep-alive";

            var sessionId = string.IsNullOrWhiteSpace(requestedSessionId)
                ? $"apps-{Guid.NewGuid():N}"
                : requestedSessionId!;
            var session = await runtime.SessionManager.GetOrCreateByIdAsync(sessionId, "apps", sessionId, ct);

            await SendAsync(ctx, new JsonObject { ["type"] = "session", ["sessionId"] = sessionId }, ct);

            try
            {
                await foreach (var evt in runtime.AgentRuntime.RunStreamingAsync(session, userText, ct))
                {
                    switch (evt.Type)
                    {
                        case AgentStreamEventType.TextDelta when !string.IsNullOrEmpty(evt.Content):
                            await SendAsync(ctx, new JsonObject { ["type"] = "text", ["text"] = evt.Content }, ct);
                            break;
                        case AgentStreamEventType.ToolStart:
                            await SendAsync(ctx, new JsonObject
                            {
                                ["type"] = "tool",
                                ["id"] = Guid.NewGuid().ToString("N"),
                                ["name"] = evt.ToolName ?? evt.Content,
                                ["input"] = ParseArgs(evt.ToolArguments),
                            }, ct);
                            break;
                        case AgentStreamEventType.Error:
                            await SendAsync(ctx, new JsonObject { ["type"] = "error", ["error"] = evt.Content }, ct);
                            break;
                        case AgentStreamEventType.Done:
                            await SendAsync(ctx, new JsonObject { ["type"] = "result", ["text"] = "", ["isError"] = false }, ct);
                            await SendAsync(ctx, new JsonObject { ["type"] = "done" }, ct);
                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                await SendAsync(ctx, new JsonObject { ["type"] = "error", ["error"] = ex.Message }, ct);
            }
        });
    }

    private static bool AppsAuthorized(HttpContext ctx, GatewayStartupContext startup)
    {
        var ip = ctx.Connection.RemoteIpAddress;
        if (ip is not null && System.Net.IPAddress.IsLoopback(ip))
            return true;

        return EndpointHelpers.IsAuthorizedRequest(ctx, startup.Config, startup.IsNonLoopbackBind);
    }

    private static string? AsString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static string BuildPrompt(string? message, JsonArray? appEvents)
    {
        var builder = new StringBuilder();
        if (appEvents is { Count: > 0 })
        {
            builder.Append("[界面事件] 用户刚在界面里完成了操作(这些工具调用绕过了你,特此同步,结果已是定稿):\n");
            foreach (var entry in appEvents)
            {
                var tool = AsString(entry?["tool"]) ?? "?";
                var args = entry?["args"]?.ToJsonString() ?? "{}";
                var resultText = AsString(entry?["resultText"]) ?? string.Empty;
                builder.Append($"- 工具 {tool}({args}) → {resultText}\n");
            }

            if (string.IsNullOrWhiteSpace(message))
                builder.Append("请基于以上结果决定下一步:用一两句话确认/承接结论,并在合适时调用相应工具继续(不要重复罗列界面已展示的明细)。\n");
        }

        if (!string.IsNullOrWhiteSpace(message))
            builder.Append(message);

        return builder.ToString();
    }

    private static JsonNode ParseArgs(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson))
            return new JsonObject();

        try
        {
            return JsonNode.Parse(argsJson) ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    private static async Task SendAsync(HttpContext ctx, JsonObject payload, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"data: {payload.ToJsonString()}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static string BuildAppMcpUrl(HttpContext ctx, string appId)
        => $"{ctx.Request.Scheme}://{ctx.Request.Host}{ctx.Request.PathBase}/apps/mcp/{Uri.EscapeDataString(appId)}";
}