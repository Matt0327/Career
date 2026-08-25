using Callsign.Core.Data;
using Callsign.Core.Economy;
using Microsoft.EntityFrameworkCore;

namespace Callsign.Host;

/// <summary>
/// Phase 13 — banks autonomous work on its own. Reconcile used to run only at startup and when the player hit
/// "Process now", so a hired crew's completed legs just sat there "ready to bank". This ticks periodically so
/// crew trips end and pay automatically, and pushes a notification to the app when something actually banked —
/// the player no longer has to babysit it. Idempotent (reconcile is dedupe-keyed), so a tick that finds nothing
/// ready is a cheap no-op.
/// </summary>
public sealed class AutoReconcileService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(90);
    private readonly IServiceProvider _sp;
    private readonly FlightSessionService _session;
    private readonly ILogger<AutoReconcileService> _log;

    public AutoReconcileService(IServiceProvider sp, FlightSessionService session, ILogger<AutoReconcileService> log)
    {
        _sp = sp; _session = session; _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        // Let startup's own reconcile run first so the first tick isn't redundant.
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        while (!ct.IsCancellationRequested && await SafeWaitAsync(timer, ct))
        {
            try
            {
                using var scope = _sp.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<CallsignDbContext>();
                var pilot = await db.Pilots.FirstOrDefaultAsync(ct);
                if (pilot is null) continue; // no career yet

                var d = await scope.ServiceProvider.GetRequiredService<OperationsService>().ReconcileAsync(pilot.CompanyId, ct);
                if (d.Trips > 0)
                    await _session.NotifyAsync(
                        $"{d.Trips} autonomous trip{(d.Trips == 1 ? "" : "s")} completed — banked {d.GrossIncomeCents / 100m:C0}.");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogWarning(ex, "Background reconcile tick failed; will retry next interval."); }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
