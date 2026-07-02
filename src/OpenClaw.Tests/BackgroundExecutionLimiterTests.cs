using OpenClaw.Core.Models;
using OpenClaw.Gateway.Background;
using Xunit;

namespace OpenClaw.Tests;

public sealed class BackgroundExecutionLimiterTests
{
    private static GatewayConfig NewConfig(int maxConcurrent = 2) => new()
    {
        BackgroundExecution = new BackgroundExecutionConfig
        {
            MaxConcurrentBackgroundTurns = maxConcurrent
        }
    };

    private static InboundMessage NonBackgroundMsg() => new()
    {
        ChannelId = "websocket",
        SenderId = "user1",
        Text = "hello"
    };

    private static InboundMessage BackgroundMsg() => new()
    {
        ChannelId = "websocket",
        SenderId = "user1",
        Text = "continue",
        Type = BackgroundMessageTypes.AutoContinue,
        IsSystem = true
    };

    [Fact]
    public async Task NonBackgroundMessages_ReturnNoOpReleaser()
    {
        await using var limiter = new BackgroundExecutionLimiter(NewConfig());
        var releaser = await limiter.TryAcquireAsync(NonBackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.NotNull(releaser);
        // No-op releaser should not throw
        releaser.Value.Dispose();
    }

    [Fact]
    public async Task BackgroundMessage_AcquiresPermit_WhenAvailable()
    {
        await using var limiter = new BackgroundExecutionLimiter(NewConfig(maxConcurrent: 1));
        var releaser = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.NotNull(releaser);
        releaser.Value.Dispose();
    }

    [Fact]
    public async Task BackgroundMessage_ReturnsNull_WhenPermitsExhausted()
    {
        await using var limiter = new BackgroundExecutionLimiter(NewConfig(maxConcurrent: 1));
        var first = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        var second = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.Null(second);

        first.Value.Dispose();
    }

    [Fact]
    public async Task DisposingReleaser_ReleasesPermit_ForReacquisition()
    {
        await using var limiter = new BackgroundExecutionLimiter(NewConfig(maxConcurrent: 1));
        var first = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        // Release permit
        first.Value.Dispose();

        // Should be able to acquire again
        var second = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.NotNull(second);
        second.Value.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_CanBeCalledCleanly()
    {
        var limiter = new BackgroundExecutionLimiter(NewConfig());
        var releaser = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.NotNull(releaser);
        releaser.Value.Dispose();
        await limiter.DisposeAsync();
    }

    [Fact]
    public async Task MultipleConcurrent_RespectsLimit()
    {
        await using var limiter = new BackgroundExecutionLimiter(NewConfig(maxConcurrent: 2));
        var r1 = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        var r2 = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.NotNull(r1);
        Assert.NotNull(r2);

        var r3 = await limiter.TryAcquireAsync(BackgroundMsg(), TestContext.Current.CancellationToken);
        Assert.Null(r3);

        r1.Value.Dispose();
        r2.Value.Dispose();
    }

    [Fact]
    public void IsBackgroundContinuation_DetectsAutoContinue()
    {
        var msg = new InboundMessage
        {
            ChannelId = "websocket",
            SenderId = "user1",
            Text = "continue",
            Type = BackgroundMessageTypes.AutoContinue,
            IsSystem = true
        };
        Assert.True(BackgroundExecutionLimiter.IsBackgroundContinuation(msg));
    }

    [Fact]
    public void IsBackgroundContinuation_DetectsAutoResume()
    {
        var msg = new InboundMessage
        {
            ChannelId = "websocket",
            SenderId = "user1",
            Text = "resume",
            Type = BackgroundMessageTypes.AutoResume,
            IsSystem = true
        };
        Assert.True(BackgroundExecutionLimiter.IsBackgroundContinuation(msg));
    }

    [Fact]
    public void IsBackgroundContinuation_RejectsNormalMessage()
    {
        Assert.False(BackgroundExecutionLimiter.IsBackgroundContinuation(NonBackgroundMsg()));
    }
}
