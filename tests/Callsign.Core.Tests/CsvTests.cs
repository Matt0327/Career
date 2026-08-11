using Callsign.Core.Import;
using Xunit;

namespace Callsign.Core.Tests;

public class CsvTests
{
    [Fact]
    public void ReadRecords_ParsesQuotesCommasAndEscapedQuotes()
    {
        var csv = "a,b,c\n\"x,y\",\"he said \"\"hi\"\"\",z\n";
        var recs = Csv.ReadRecords(new StringReader(csv)).ToList();

        Assert.Equal(2, recs.Count);
        Assert.Equal(new[] { "a", "b", "c" }, recs[0]);
        Assert.Equal(new[] { "x,y", "he said \"hi\"", "z" }, recs[1]);
    }

    [Fact]
    public void ReadRecords_HandlesEmbeddedNewlineInsideQuotes()
    {
        var recs = Csv.ReadRecords(new StringReader("a,b\n\"line1\nline2\",second\n")).ToList();

        Assert.Equal(2, recs.Count);
        Assert.Equal("line1\nline2", recs[1][0]);
        Assert.Equal("second", recs[1][1]);
    }

    [Fact]
    public void ReadRecords_YieldsFinalRecordWithoutTrailingNewline()
    {
        var recs = Csv.ReadRecords(new StringReader("a,b\nc,d")).ToList();

        Assert.Equal(2, recs.Count);
        Assert.Equal(new[] { "c", "d" }, recs[1]);
    }

    [Fact]
    public void ReadRecords_PreservesEmptyFields()
    {
        var recs = Csv.ReadRecords(new StringReader("a,,c,\n")).ToList();

        Assert.Single(recs);
        Assert.Equal(new[] { "a", "", "c", "" }, recs[0]);
    }
}
