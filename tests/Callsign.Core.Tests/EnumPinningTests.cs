using Callsign.Core.Domain;
using Xunit;

namespace Callsign.Core.Tests;

// Persisted enums must keep fixed numeric meanings so reordering members can never
// corrupt stored rows. (LedgerCategory is stored as a string, so it is exempt.)
public class EnumPinningTests
{
    [Fact]
    public void PilotRank_ValuesArePinned()
    {
        Assert.Equal(0, (int)PilotRank.Trainee);
        Assert.Equal(1, (int)PilotRank.Copilot);
        Assert.Equal(2, (int)PilotRank.Captain);
        Assert.Equal(3, (int)PilotRank.SeniorCaptain);
        Assert.Equal(4, (int)PilotRank.Chief);
    }

    [Fact]
    public void LedgerRefType_ValuesArePinned()
    {
        Assert.Equal(1, (int)LedgerRefType.Job);
        Assert.Equal(2, (int)LedgerRefType.Loan);
        Assert.Equal(3, (int)LedgerRefType.StockLot);
        Assert.Equal(4, (int)LedgerRefType.CheckFlight);
        Assert.Equal(5, (int)LedgerRefType.Campaign);
        Assert.Equal(6, (int)LedgerRefType.InsuranceClaim);
        Assert.Equal(7, (int)LedgerRefType.Rental);
        Assert.Equal(8, (int)LedgerRefType.Fuel);
    }
}
