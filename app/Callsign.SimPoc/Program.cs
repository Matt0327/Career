using Callsign.SimConnect;
using Callsign.SimConnect.Windows;

// Minimal SimConnect proof of concept (brief §10.3):
//   connect, read altitude / airspeed / on-ground / aircraft title, print at 4 Hz,
//   and survive the sim closing and reopening.
//
// Usage:
//   Callsign.SimPoc                      try real SimConnect, fall back to fake if unavailable
//   Callsign.SimPoc --fake               force the synthetic source (no sim needed)
//   Callsign.SimPoc --fake --seconds 6   run for 6 seconds, then exit (smoke test)

bool forceFake = args.Contains("--fake", StringComparer.OrdinalIgnoreCase);

using var cts = new CancellationTokenSource();

// Optional self-terminate after N seconds (handy for smoke tests and this PoC run).
int secIdx = Array.FindIndex(args, a => string.Equals(a, "--seconds", StringComparison.OrdinalIgnoreCase));
if (secIdx >= 0 && secIdx + 1 < args.Length &&
    int.TryParse(args[secIdx + 1], out int runSeconds) && runSeconds > 0)
    cts.CancelAfter(TimeSpan.FromSeconds(runSeconds));

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;   // let us shut down cleanly instead of hard-killing
    cts.Cancel();
};

ISimTelemetrySource source = CreateSource(forceFake);
Console.WriteLine($"Callsign SimConnect PoC — source: {source.GetType().Name}");
Console.WriteLine("Press Ctrl+C to exit.\n");

source.StateChanged += s => Console.WriteLine($"[state] {s}");
source.TelemetryReceived += Print;

await source.StartAsync(cts.Token);
try
{
    await Task.Delay(Timeout.Infinite, cts.Token);
}
catch (OperationCanceledException)
{
    // Ctrl+C
}
await source.StopAsync();
Console.WriteLine("\nBye.");
return;

static ISimTelemetrySource CreateSource(bool forceFake)
{
    if (!forceFake)
    {
        try
        {
            return new SimConnectTelemetrySource(hz: 4);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[warn] SimConnect unavailable ({ex.GetType().Name}: {ex.Message})");
            Console.WriteLine("[warn] Falling back to the synthetic source.\n");
        }
    }
    return new FakeTelemetrySource(hz: 4);
}

static void Print(TelemetrySnapshot t)
    => Console.WriteLine(
        $"#{t.Sequence,-5} " +
        $"alt {t.AltitudeFt,7:F0} ft  " +
        $"ias {t.IndicatedAirspeedKts,5:F0} kt  " +
        $"gs {t.GroundSpeedKts,5:F0} kt  " +
        $"vs {t.VerticalSpeedFpm,7:F0} fpm  " +
        $"{(t.OnGround ? "GND" : "AIR")}  " +
        $"\"{t.AircraftTitle}\"");
