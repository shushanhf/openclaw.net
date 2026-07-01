using OpenClaw.Core.Models;

namespace OpenClaw.Gateway.Background;

internal static class GatewayBackgroundCancellation
{
    public static CancellationToken ResolveProcessingCancellation(InboundMessage message, CancellationToken applicationStopping)
        => BackgroundExecutionLimiter.IsBackgroundContinuation(message)
            ? applicationStopping
            : message.RequestCancellation.CanBeCanceled
                ? CancellationTokenSource.CreateLinkedTokenSource(message.RequestCancellation, applicationStopping).Token
                : applicationStopping;
}
