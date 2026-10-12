using RemoteAgent.Core.Abstractions;
using RemoteAgent.Core.Configuration;
using RemoteAgent.Security;
using Xunit;

namespace RemoteAgent.Protocol.Tests;

/// <summary>
/// Checks that the per-connection request budget leaves room for remote input.
/// </summary>
/// <remarks>
/// A phone dragging the pointer sends about sixty moves a second, without waiting for replies. The
/// rate limiter exists to stop a runaway client, and must not mistake that for one. These tests
/// drive the limiter with the default budget and a hand-moved clock, at the rate and in the bursts
/// input actually produces.
/// </remarks>
public sealed class InputThroughputTests
{
    private static readonly int DefaultBudget = new SecurityOptions().MaxRequestsPerMinute;

    [Fact]
    public void SixtyMovesASecondIsNeverThrottledAtTheDefaultBudget()
    {
        var clock = new StepClock();
        var limiter = new RequestRateLimiter(clock, DefaultBudget);

        // A full minute of continuous dragging, one move every 1/60 s.
        for (int i = 0; i < 60 * 60; i++)
        {
            Assert.True(limiter.TryAcquire(), $"Move {i} was throttled.");
            clock.Advance(TimeSpan.FromSeconds(1.0 / 60));
        }
    }

    [Fact]
    public void ADragWithClicksKeysAndStatusCallsAlongsideStillFits()
    {
        var clock = new StepClock();
        var limiter = new RequestRateLimiter(clock, DefaultBudget);

        // Sixty moves a second plus another thirty assorted commands a second, for thirty seconds.
        for (int tick = 0; tick < 30 * 60; tick++)
        {
            Assert.True(limiter.TryAcquire());

            if (tick % 2 == 0)
            {
                Assert.True(limiter.TryAcquire());
            }

            clock.Advance(TimeSpan.FromSeconds(1.0 / 60));
        }
    }

    [Fact]
    public void TheBacklogFromAShortNetworkStallArrivesWithoutBeingThrottled()
    {
        var clock = new StepClock();
        var limiter = new RequestRateLimiter(clock, DefaultBudget);

        // Two seconds of moves that queued on the phone while Wi-Fi hiccuped, delivered at once.
        for (int i = 0; i < 120; i++)
        {
            Assert.True(limiter.TryAcquire(), $"Queued move {i} was throttled.");
        }

        // And dragging simply continues afterwards.
        for (int i = 0; i < 600; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1.0 / 60));
            Assert.True(limiter.TryAcquire());
        }
    }

    [Fact]
    public void ARunawayClientIsStillThrottledAndThenRecovers()
    {
        var clock = new StepClock();
        var limiter = new RequestRateLimiter(clock, DefaultBudget);

        int accepted = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (limiter.TryAcquire())
            {
                accepted++;
            }
        }

        // No time passed, so only the burst allowance got through: far fewer than were sent.
        Assert.InRange(accepted, 100, 400);
        Assert.False(limiter.TryAcquire());

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(limiter.TryAcquire());
    }

    private sealed class StepClock : IClock
    {
        private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

        public DateTimeOffset UtcNow => _now;

        public long UnixTimeMilliseconds => _now.ToUnixTimeMilliseconds();

        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
