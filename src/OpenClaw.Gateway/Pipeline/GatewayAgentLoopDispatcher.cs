using Microsoft.Extensions.Logging;
using OpenClaw.Core.Loops;
using OpenClaw.Core.Models;
using OpenClaw.Core.Pipeline;

namespace OpenClaw.Gateway.Pipeline;

/// <summary>
/// Gateway implementation of IAgentLoopDispatcher.
/// Injects loop prompts as InboundMessages into the MessagePipeline,
/// where they are consumed by GatewayInboundMessageWorker and processed
/// through the configured IAgentRuntime (AgentRuntime or MafAgentRuntime).
/// </summary>
internal sealed class GatewayAgentLoopDispatcher : IAgentLoopDispatcher
{
    private readonly MessagePipeline _pipeline;
    private readonly ILogger<GatewayAgentLoopDispatcher> _logger;

    public GatewayAgentLoopDispatcher(
        MessagePipeline pipeline,
        ILogger<GatewayAgentLoopDispatcher> logger)
    {
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
        _logger = logger;
    }

    public async Task<bool> DispatchAsync(string sessionId, string prompt, CancellationToken ct)
    {
        // Inject loop prompt into the message pipeline.
        // Marked IsSystem so it doesn't hit rate limits or pairing gates.
        // Session existence is validated by the worker — if the session is gone,
        // the worker handles it gracefully.
        await _pipeline.InboundWriter.WriteAsync(new InboundMessage
        {
            SessionId = sessionId,
            ChannelId = "cron",
            SenderId = "loop",
            Text = prompt,
            IsSystem = true
        }, ct);

        _logger.LogInformation("Loop prompt dispatched for session {SessionId}", sessionId);
        return true;
    }
}
