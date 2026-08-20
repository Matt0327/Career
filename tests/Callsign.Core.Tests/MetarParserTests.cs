using System.Text.Json;
using Callsign.Core.World;
using Xunit;

namespace Callsign.Core.Tests;

/// <summary>Phase 9b — the pure METAR parser. Canned inputs only; no network. Every untrustworthy field
/// degrades the whole observation to null (→ synthetic), and units are normalised explicitly at the boundary.</summary>
public class MetarParserTests
{
    private static MetarJson Json(string body) => JsonSerializer.Deserialize<MetarJson>(body)!;

    [Fact]
    public void FromJson_HappyPath_ParsesEveryField()
    {
        var w = MetarParser.FromJson(Json("""
            {"icaoId":"KJFK","obsTime":1700000000,"wdir":270,"wspd":12,"wgst":20,"visib":10,"temp":8,
             "wxString":"-RA","clouds":[{"cover":"BKN","base":1500},{"cover":"OVC","base":3000}]}
            """));
        Assert.NotNull(w);
        Assert.Equal(270, w!.WindDirDeg);
        Assert.Equal(12, w.WindKts);
        Assert.Equal(20, w.GustKts);
        Assert.Equal(10, w.VisibilitySm);
        Assert.Equal(1500, w.CeilingFt); // lowest BKN/OVC base
        Assert.Equal(8, w.TempC);
        Assert.Equal("Rain", w.Condition);
    }

    [Fact]
    public void FromJson_UnionTypes_VrbDirAnd10Plus()
    {
        var vrb = MetarParser.FromJson(Json("""{"obsTime":1,"wdir":"VRB","wspd":5,"visib":"10+","temp":15}"""));
        Assert.Equal(0, vrb!.WindDirDeg);   // VRB → 0 (display-only)
        Assert.Equal(5, vrb.WindKts);       // speed kept
        Assert.Equal(10, vrb.VisibilitySm); // "10+" → 10

        var six = MetarParser.FromJson(Json("""{"obsTime":1,"wdir":360,"wspd":0,"visib":"6+","temp":15}"""));
        Assert.Equal(6, six!.VisibilitySm);
        Assert.Equal(0, six.WindDirDeg);    // 360 → 0
    }

    [Fact]
    public void FromJson_Gust_NullDefaultsToWind_PresentIsKept()
    {
        var noGust = MetarParser.FromJson(Json("""{"obsTime":1,"wdir":90,"wspd":14,"visib":9,"temp":10}"""));
        Assert.Equal(14, noGust!.GustKts); // null gust → == wind
        var gust = MetarParser.FromJson(Json("""{"obsTime":1,"wdir":90,"wspd":14,"wgst":25,"visib":9,"temp":10}"""));
        Assert.Equal(25, gust!.GustKts);
    }

    [Fact]
    public void FromJson_Ceiling_LowestBrokenOrOvercast_ElseHigh()
    {
        Assert.Equal(800, MetarParser.FromJson(Json("""{"obsTime":1,"visib":10,"clouds":[{"cover":"BKN","base":800},{"cover":"OVC","base":2000}]}"""))!.CeilingFt);
        Assert.Equal(25_000, MetarParser.FromJson(Json("""{"obsTime":1,"visib":10,"clouds":[{"cover":"FEW","base":300},{"cover":"SCT","base":900}]}"""))!.CeilingFt); // FEW/SCT don't set a ceiling
        Assert.Equal(200, MetarParser.FromJson(Json("""{"obsTime":1,"visib":1,"clouds":[{"cover":"VV","base":200}]}"""))!.CeilingFt);
    }

    [Theory]
    [InlineData("TSRA", 1.0, "Storm")]
    [InlineData("SN", 1.0, "Snow")]
    [InlineData("-RA", 8.0, "Rain")]
    [InlineData("FG", 0.25, "Fog")]
    [InlineData("BR", 8.0, "Clear")] // mist but good vis → not Fog
    public void FromJson_ConditionMapping(string wx, double vis, string expected)
    {
        var w = MetarParser.FromJson(Json($$"""{"obsTime":1,"wspd":3,"visib":{{vis}},"temp":5,"wxString":"{{wx}}"}"""));
        Assert.Equal(expected, w!.Condition);
    }

    [Fact]
    public void FromJson_OvercastWithNoWx_IsCloudy()
        => Assert.Equal("Cloudy", MetarParser.FromJson(Json("""{"obsTime":1,"wspd":3,"visib":9,"temp":5,"clouds":[{"cover":"OVC","base":1200}]}"""))!.Condition);

    [Fact]
    public void FromJson_Untrustworthy_ReturnsNull()
    {
        Assert.Null(MetarParser.FromJson(null));
        Assert.Null(MetarParser.FromJson(Json("""{"obsTime":1,"wdir":270,"wspd":5,"temp":8}""")));          // no visibility
        Assert.Null(MetarParser.FromJson(Json("""{"obsTime":1,"wdir":270,"wspd":5,"visib":"garbage","temp":8}""")));
    }

    [Fact]
    public void FromJson_AbsurdValues_AreClamped()
    {
        var w = MetarParser.FromJson(Json("""{"obsTime":1,"wdir":9999,"wspd":900,"visib":999,"temp":200}"""));
        Assert.InRange(w!.WindKts, 0, 250);
        Assert.InRange(w.VisibilitySm, 0.05, 10); // 999 sm clamped to 10, never left absurd
        Assert.InRange(w.TempC, -90, 60);
        Assert.InRange(w.WindDirDeg, 0, 359);
    }

    [Theory]
    [InlineData("KJFK 281951Z 27008KT 10SM FEW250 08/M03 A3012", 270, 8, 8, 10.0)]
    [InlineData("KORD 281951Z 27008G21KT 10SM SCT040 12/05 A2998", 270, 8, 21, 10.0)]
    [InlineData("KLGA 281951Z VRB03KT 10SM CLR 15/07 A3000", 0, 3, 3, 10.0)]
    public void FromRaw_WindAndVisibility(string raw, int dir, int wind, int gust, double vis)
    {
        var w = MetarParser.FromRaw(raw);
        Assert.NotNull(w);
        Assert.Equal(dir, w!.WindDirDeg);
        Assert.Equal(wind, w.WindKts);
        Assert.Equal(gust, w.GustKts);
        Assert.Equal(vis, w.VisibilitySm);
    }

    [Fact]
    public void FromRaw_Units_AndSpecials()
    {
        Assert.Equal(0.5, MetarParser.FromRaw("KXYZ 281951Z 18004KT 1/2SM FG OVC002 03/03")!.VisibilitySm);
        Assert.Equal(1.5, MetarParser.FromRaw("KXYZ 281951Z 27008KT 1 1/2SM BR OVC010 12/10 A3000")!.VisibilitySm); // whole+fraction, not the bare 0.5
        Assert.Equal("Fog", MetarParser.FromRaw("KXYZ 281951Z 18004KT 1/2SM FG OVC002 03/03")!.Condition);
        Assert.Equal(200, MetarParser.FromRaw("KXYZ 281951Z 18004KT 1/2SM FG OVC002 03/03")!.CeilingFt);

        Assert.Equal(10, MetarParser.FromRaw("KXYZ 281951Z 00000KT CAVOK 20/10 Q1013")!.VisibilitySm);       // CAVOK → 10
        Assert.Equal("Clear", MetarParser.FromRaw("KXYZ 281951Z 00000KT CAVOK 20/10 Q1013")!.Condition);
        Assert.Equal(10, MetarParser.FromRaw("EDDF 281950Z 24010KT 9999 SCT030 18/12 Q1015")!.VisibilitySm);  // 9999 m → 10 sm, never 9999

        var metres = MetarParser.FromRaw("EDDF 281950Z 24010KT 4800 BR SCT008 05/04 Q1000")!;
        Assert.InRange(metres.VisibilitySm, 2.9, 3.1); // 4800 m ≈ 3.0 sm (not 4800 sm)

        var mps = MetarParser.FromRaw("UUEE 281930Z 27010MPS 9999 OVC020 M03/M07 Q1005")!;
        Assert.Equal(19, mps.WindKts); // 10 m/s ≈ 19 kt
        Assert.Equal(-3, mps.TempC);   // M03 → -3
        Assert.Equal(2000, mps.CeilingFt);
    }

    [Fact]
    public void FromRaw_Garbage_ReturnsNull()
    {
        Assert.Null(MetarParser.FromRaw(null));
        Assert.Null(MetarParser.FromRaw("   "));
        Assert.Null(MetarParser.FromRaw("KJFK 281951Z NIL"));
        Assert.Null(MetarParser.FromRaw("this is not a metar at all"));
    }
}
