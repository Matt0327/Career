using Callsign.Core.Aircraft;
using Xunit;

namespace Callsign.Core.Tests;

public class AircraftCfgTests
{
    // Mirrors the real community format: spaced '=', quotes, capitalised Title, comments, junk sections.
    private const string Cfg =
        "; a comment\n" +
        "[VERSION]\n" +
        "major = 1\n" +
        "[GENERAL]\n" +
        "atc_type =\"Pilatus\"\n" +
        "Category = \"Airplane\"\n" +
        "icao_type_designator = \"PC12\"\n" +
        "icao_manufacturer = \"PILATUS\"\n" +
        "[EFFECTS]\n" +
        "somekey = ignored\n" +
        "[FLTSIM.0]\n" +
        "Title=\"Pilatus PC-12/47 OH-JEM\"\n" +
        "ui_manufacturer=\"Pilatus\"\n" +
        "ui_type=\"PC-12/47\"\n" +
        "ui_typerole=\"Single Engine Turboprop\"\n" +
        "[FLTSIM.1]\n" +
        "Title=\"Pilatus PC-12/47 White\"\n";

    [Fact]
    public void Parse_ReadsGeneralAndFltSimBlocks()
    {
        var cfg = AircraftCfg.Parse(new StringReader(Cfg));

        Assert.Equal("PC12", cfg.General["icao_type_designator"]);
        Assert.Equal("Airplane", cfg.General["Category"]);
        Assert.Equal("Pilatus", cfg.General["atc_type"]);      // space before '=' handled
        Assert.False(cfg.General.ContainsKey("somekey"));       // [EFFECTS] ignored

        Assert.Equal(2, cfg.FltSims.Count);
        Assert.Equal("Pilatus PC-12/47 OH-JEM", cfg.FltSims[0]["title"]); // case-insensitive key
        Assert.Equal("Single Engine Turboprop", cfg.FltSims[0]["ui_typerole"]);
        Assert.Equal("Pilatus PC-12/47 White", cfg.FltSims[1]["Title"]);
    }

    [Fact]
    public void Parse_StripsInlineComments()
    {
        var cfg = AircraftCfg.Parse(new StringReader(
            "[FLTSIM.0]\nTitle=\"SR-71 Blackbird ASARS\" ; Variation name\nui_type=Something ; note\n"));

        Assert.Equal("SR-71 Blackbird ASARS", cfg.FltSims[0]["Title"]);
        Assert.Equal("Something", cfg.FltSims[0]["ui_type"]);
    }
}
