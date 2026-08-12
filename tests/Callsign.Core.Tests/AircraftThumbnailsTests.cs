using Callsign.Core.Aircraft;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Resolving an aircraft's installed-sim thumbnail from the scan's folder names (Phase 6b).</summary>
public sealed class AircraftThumbnailsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"callsign-ipp-{Guid.NewGuid():N}");

    private string Seed(string source, string package, string aircraft)
    {
        var dir = Path.Combine(_root, source, package, "SimObjects", "Airplanes", aircraft);
        Directory.CreateDirectory(dir);
        var thumb = Path.Combine(dir, "thumbnail.jpg");
        File.WriteAllBytes(thumb, new byte[] { 1, 2, 3 });
        return thumb;
    }

    [Fact]
    public void Resolve_FindsThumbnailInTheAircraftFolder()
    {
        var thumb = Seed("Community2024", "my-pkg", "AC");
        Assert.Equal(thumb, AircraftThumbnails.TryResolve(_root, "Community2024", "my-pkg", "AC"));
    }

    [Fact]
    public void Resolve_PrefersTheMatchingAircraftAmongSeveral()
    {
        Seed("Official2024", "big-pkg", "Jet");
        var prop = Seed("Official2024", "big-pkg", "Prop");
        Assert.Equal(prop, AircraftThumbnails.TryResolve(_root, "Official2024", "big-pkg", "Prop"));
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenNothingOnDisk()
        => Assert.Null(AircraftThumbnails.TryResolve(_root, "Community2024", "missing-pkg", "AC"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }
}
