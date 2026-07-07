using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using OpenClaw.Core.Models;
using OpenClaw.Core.Plugins;
using OpenClaw.Gateway.Bootstrap;
using OpenClaw.Gateway.Endpoints;
using OpenClaw.McpApp;
using OpenClaw.McpApp.Models;
using Xunit;

namespace OpenClaw.Tests;

public sealed class AppsMcpProxyEndpointTests : IAsyncDisposable
{
    private readonly List<WebApplication> _apps = [];
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task ToolsList_PassesThroughAllTools_NoVisibilityFiltering()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAsync("inventory-app", upstreamUrl);

        await using var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/inventory-app") }),
            cancellationToken: CancellationToken.None);

        var tools = await mcpClient.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Contains(tools, t => t.Name == "echo_session");
        Assert.Contains(tools, t => t.Name == "app_only_tool");
    }

    [Fact]
    public async Task CallTool_InjectsSessionIdFromQueryIntoMeta()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAsync("inventory-app", upstreamUrl);

        await using var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/inventory-app?sessionId=abc123") }),
            cancellationToken: CancellationToken.None);

        var result = await mcpClient.CallToolAsync("echo_session", cancellationToken: CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        Assert.NotNull(result.StructuredContent);
        var doc = result.StructuredContent!.Value;
        Assert.Equal("abc123", doc.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task CallTool_UnknownAppId_DoesNotThrowOrCrashGateway()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAsync("inventory-app", upstreamUrl);

        await using var mcpClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/nonexistent") }),
            cancellationToken: CancellationToken.None);

        await Assert.ThrowsAnyAsync<Exception>(
            () => mcpClient.CallToolAsync("echo_session", cancellationToken: CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task ProxyConfiguration_DoesNotOverride_DefaultMcpRouteHandlers()
    {
        var upstreamUrl = await StartFakeUpstreamAsync();
        await using var gateway = await StartGatewayWithProxyAndDefaultMcpAsync("inventory-app", upstreamUrl);

        await using var defaultClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}mcp") }),
            cancellationToken: CancellationToken.None);
        var defaultTools = await defaultClient.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Contains(defaultTools, tool => tool.Name == "default_gateway_tool");
        Assert.DoesNotContain(defaultTools, tool => tool.Name == "echo_session");

        await using var proxiedClient = await McpClient.CreateAsync(
            new HttpClientTransport(new HttpClientTransportOptions { Endpoint = new Uri($"{gateway.BaseAddress}apps/mcp/inventory-app") }),
            cancellationToken: CancellationToken.None);
        var proxiedTools = await proxiedClient.ListToolsAsync(cancellationToken: CancellationToken.None);

        Assert.Contains(proxiedTools, tool => tool.Name == "echo_session");
        Assert.DoesNotContain(proxiedTools, tool => tool.Name == "default_gateway_tool");
    }

    private async Task<string> StartFakeUpstreamAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "fake-upstream", Version = "1.0.0" };
            })
            .WithHttpTransport(options => { options.Stateless = true; })
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool { Name = "echo_session", Description = "echoes back _meta.sessionId" },
                    new Tool { Name = "app_only_tool", Description = "app-only, visibility excludes model" },
                ],
            }))
            .WithCallToolHandler((ctx, _) =>
            {
                var sessionId = ctx.Params?.Meta?["sessionId"]?.ToString();
                return ValueTask.FromResult(new CallToolResult
                {
                    Content = [],
                    StructuredContent = JsonSerializer.SerializeToElement(new { sessionId, tool = ctx.Params?.Name }),
                });
            });

        var app = builder.Build();
        app.MapMcp("/mcp");
        await app.StartAsync();
        _apps.Add(app);
        return $"{app.Urls.Single().TrimEnd('/')}/mcp";
    }

    private async Task<GatewayProxyTestHarness> StartGatewayWithProxyAsync(string appId, string upstreamUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-apps-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        var manifest = new McpAppManifest
        {
            Id = appId,
            Name = "Inventory App",
            Version = "1.0",
            Transport = "http",
            Url = upstreamUrl,
            ToolNamePrefix = "inventory.",
        };
        var manifestPath = Path.Combine(root, "openclaw.mcpapp.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(manifest, McpAppManifestJsonContext.Default.McpAppManifest));

        var config = new GatewayConfig
        {
            BindAddress = "127.0.0.1",
            AuthToken = "test-token",
            McpApps = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [root],
            }
        };
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            IsNonLoopbackBind = false,
            WorkspacePath = null,
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenClawMcpAppServices(config.McpApps);
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "OpenClaw Gateway MCP", Version = "1.0.0" };
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = AppsMcpProxyEndpoint.ConfigureSessionOptionsAsync;
            });

        var app = builder.Build();
        app.MapOpenClawAppsMcpProxy(startup);
        await app.StartAsync();
        _apps.Add(app);

        var registry = app.Services.GetRequiredService<McpAppRegistry>();
        await registry.LoadAllAsync(CancellationToken.None);

        return new GatewayProxyTestHarness(app, registry, app.Urls.Single().TrimEnd('/') + "/");
    }

    private async Task<GatewayProxyTestHarness> StartGatewayWithProxyAndDefaultMcpAsync(string appId, string upstreamUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), "openclaw-apps-proxy-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirs.Add(root);

        var manifest = new McpAppManifest
        {
            Id = appId,
            Name = "Inventory App",
            Version = "1.0",
            Transport = "http",
            Url = upstreamUrl,
            ToolNamePrefix = "inventory.",
        };
        await File.WriteAllTextAsync(
            Path.Combine(root, "openclaw.mcpapp.json"),
            JsonSerializer.Serialize(manifest, McpAppManifestJsonContext.Default.McpAppManifest));

        var config = new GatewayConfig
        {
            BindAddress = "127.0.0.1",
            AuthToken = "test-token",
            McpApps = new McpAppsConfig
            {
                Enabled = true,
                DiscoveryPaths = [root],
            }
        };
        var startup = new GatewayStartupContext
        {
            Config = config,
            RuntimeState = RuntimeModeResolver.Resolve(config.Runtime),
            IsNonLoopbackBind = false,
            WorkspacePath = null,
        };

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddOpenClawMcpAppServices(config.McpApps);
        builder.Services.AddMcpServer(options =>
            {
                options.ServerInfo = new Implementation { Name = "OpenClaw Gateway MCP", Version = "1.0.0" };
            })
            .WithHttpTransport(options =>
            {
                options.Stateless = true;
                options.ConfigureSessionOptions = AppsMcpProxyEndpoint.ConfigureSessionOptionsAsync;
            })
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools =
                [
                    new Tool { Name = "default_gateway_tool", Description = "default route handler tool" },
                ],
            }));

        var app = builder.Build();
        app.MapOpenClawAppsMcpProxy(startup);
        app.MapMcp("/mcp");
        await app.StartAsync();
        _apps.Add(app);

        var registry = app.Services.GetRequiredService<McpAppRegistry>();
        await registry.LoadAllAsync(CancellationToken.None);

        return new GatewayProxyTestHarness(app, registry, app.Urls.Single().TrimEnd('/') + "/");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in _apps)
            await app.DisposeAsync();

        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            }
            catch
            {
            }
        }
    }

    private sealed record GatewayProxyTestHarness(WebApplication App, McpAppRegistry Registry, string BaseAddress) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Registry.DisposeAsync();
            await App.DisposeAsync();
        }
    }
}