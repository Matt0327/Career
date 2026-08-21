using Callsign.Core.Text;
using Xunit;

namespace Callsign.Core.Tests;

public class NameGuardTests
{
    // Ordinary names — including the classic false-positive traps — must all pass.
    [Theory]
    [InlineData("Amelia")]
    [InlineData("SkyHawk Air")]
    [InlineData("Delta Wings")]
    [InlineData("Compass Aviation")]     // contains "…pass…"
    [InlineData("Class Act Charters")]   // contains "…ass…"
    [InlineData("Raccoon Airways")]      // contains "…coon…"
    [InlineData("Scunthorpe Flying Club")] // the canonical Scunthorpe problem — contains "…cunt…"
    [InlineData("Nigeria Cargo")]        // must NOT trip the racial-slur substring
    [InlineData("Niger Air")]
    [InlineData("Cassidy")]
    [InlineData("Assateague Island Air")]
    [InlineData("")]                     // blank is a different validation's problem, not this one
    [InlineData("CS-4F7K2")]             // a tail-style name
    public void CleanNames_AreAllowed(string name)
        => Assert.True(NameGuard.IsAllowed(name), $"'{name}' should be allowed");

    // Standalone rude words (as a whole name or a whole token) are caught.
    [Theory]
    [InlineData("ass")]
    [InlineData("Big Ass Jet")]
    [InlineData("a.s.s")]
    public void RudeWholeWords_AreBlocked(string name)
        => Assert.False(NameGuard.IsAllowed(name), $"'{name}' should be blocked");

    // Slurs and hard explicit terms are caught even when embedded, and through common leetspeak evasion.
    [Theory]
    [InlineData("f4gg0t")]     // leet
    [InlineData("Sh1t Air")]   // leet + spacing
    [InlineData("FUCKco")]     // uppercase + embedded
    public void SlursAndLeetEvasion_AreBlocked(string name)
        => Assert.False(NameGuard.IsAllowed(name), $"'{name}' should be blocked");

    [Fact]
    public void Validate_Throws_AFriendlyMessage_WithTheField()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => NameGuard.Validate("Big Ass Jet", "route name"));
        Assert.Contains("route name", ex.Message);
    }

    [Fact]
    public void Validate_CleanName_DoesNotThrow()
        => NameGuard.Validate("Blue Yonder Air", "airline name"); // no throw
}
