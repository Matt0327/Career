namespace Callsign.Core.Domain;

/// <summary>
/// Dormant hooks for the Phase-4 shared-world ADR (last-writer-wins merge + soft
/// delete). Reserved now on syncable aggregates so that adding a shared world later is
/// not a schema-wide retrofit. NOT applied to machine-local tables (e.g. installed-package
/// scan state), which never sync.
/// </summary>
public interface ISyncable
{
    DateTimeOffset UpdatedAt { get; set; }
    bool IsDeleted { get; set; }
    Guid? OriginClientId { get; set; }
}
