using Callsign.Core.Aircraft;
using Xunit;

namespace Callsign.Core.Tests;

public class MsfsInstallLocatorTests
{
    [Theory]
    [InlineData("InstalledPackagesPath \"C:\\Users\\x\\Packages\"", "C:\\Users\\x\\Packages")]
    [InlineData("InstalledPackagesPath \"D:/MSFS/Packages\"", "D:/MSFS/Packages")]
    [InlineData("SomeOtherSetting \"nope\"", null)]
    public void ParseInstalledPackagesPath_ExtractsQuotedPath(string line, string? expected)
        => Assert.Equal(expected, MsfsInstallLocator.ParseInstalledPackagesPath(line));

    [Fact]
    public void TryGet_ReadsPathFromOverrideUserCfg()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"UserCfg-{Guid.NewGuid():N}.opt");
        File.WriteAllText(tmp, "SomeSetting 1\nInstalledPackagesPath \"C:\\FS\\Packages\"\nMore 2\n");
        try
        {
            Assert.True(MsfsInstallLocator.TryGetInstalledPackagesPath(out var path, tmp));
            Assert.Equal("C:\\FS\\Packages", path);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void TryGet_ReturnsFalse_WhenFileMissing()
        => Assert.False(MsfsInstallLocator.TryGetInstalledPackagesPath(out _, "Z:\\does\\not\\exist.opt"));
}
