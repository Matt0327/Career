using System.Collections.Concurrent;
using Callsign.SimConnect;
using Xunit;

namespace Callsign.SimConnect.Tests;

public class FakeTelemetrySourceTests
{
    [Fact]
    public async Task Emits_Telemetry_And_Reports_Connected()
    {
        await using var src = new FakeTelemetrySource(hz: 20);
        var states = new ConcurrentQueue<SimConnectionState>();
        var frames = new ConcurrentQueue<TelemetrySnapshot>();
        src.StateChanged += states.Enqueue;
        src.TelemetryReceived += frames.Enqueue;

        await src.StartAsync();

        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (frames.Count < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(25);

        await src.StopAsync();

        Assert.Contains(SimConnectionState.Connected, states);
        Assert.False(frames.IsEmpty);
        Assert.All(frames, f => Assert.False(string.IsNullOrWhiteSpace(f.AircraftTitle)));
        Assert.All(frames, f => Assert.True(f.AltitudeFt >= 0));
    }

    [Fact]
    public async Task Sequence_Numbers_Are_Monotonic()
    {
        await using var src = new FakeTelemetrySource(hz: 20);
        var frames = new ConcurrentQueue<TelemetrySnapshot>();
        src.TelemetryReceived += frames.Enqueue;

        await src.StartAsync();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (frames.Count < 5 && DateTime.UtcNow < deadline)
            await Task.Delay(25);
        await src.StopAsync();

        var seqs = frames.Select(f => f.Sequence).ToList();
        var sorted = seqs.OrderBy(x => x).ToList();
        Assert.Equal(sorted, seqs);
    }
}
