using System;
using System.IO;
using Imperial2030.Server.Configuration;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// Where the rolling log file goes.
///
/// Azure App Service runs this site with WEBSITE_RUN_FROM_PACKAGE=1, which mounts the deployed zip
/// read-only, so nlog.config's original "${basedir}/logs" could never create its directory there. An
/// NLog file target that cannot write does not fail startup - it silently produces nothing, which is
/// exactly the kind of failure a test has to catch instead of a person.
///
/// Every case passes its inputs explicitly rather than mutating process environment variables, which
/// would make these tests order-dependent against anything else reading the environment.
/// </summary>
public class LogPathsTests
{
    private const string Base = "/app";

    [Fact]
    public void AnExplicitLogDirectoryWinsOverEverythingElse()
    {
        var resolved = LogPaths.Resolve(
            "/custom/logs",
            "/home",
            "imperial2030",
            Base);

        Assert.Equal("/custom/logs", resolved);
    }

    /// <summary>
    /// The path is built from HOME rather than written down, because its literal value is not knowable
    /// here: App Service exposes "/home" on Linux and a drive-rooted path on Windows whose letter has
    /// differed between stamps. Anything hardcoded would be a guess.
    /// </summary>
    [Theory]
    [InlineData("/home")]
    [InlineData(@"D:\home")]
    [InlineData(@"C:\home")]
    public void OnAppServiceItUsesWhateverHomePointsAt(string home)
    {
        var resolved = LogPaths.Resolve(
            null,
            home,
            "imperial2030",
            Base);

        Assert.Equal(Path.Combine(home, LogPaths.AppServiceLogSubdirectory), resolved);
    }

    /// <summary>
    /// HOME alone must NOT trigger the App Service path. It is set on every Linux desktop and inside Git
    /// Bash on Windows, where it is the developer's home directory - keying off it by itself would
    /// quietly relocate local logs to ~/LogFiles.
    /// </summary>
    [Fact]
    public void HomeWithoutTheAppServiceMarkerStaysLocal()
    {
        var resolved = LogPaths.Resolve(
            null,
            "/home/druvaciam",
            null,
            Base);

        Assert.Equal(Path.Combine(Base, LogPaths.LocalLogSubdirectory), resolved);
    }

    /// <summary>The marker without HOME cannot build a path, so it must fall back rather than throw.</summary>
    [Fact]
    public void TheAppServiceMarkerWithoutHomeStaysLocal()
    {
        var resolved = LogPaths.Resolve(
            null,
            null,
            "imperial2030",
            Base);

        Assert.Equal(Path.Combine(Base, LogPaths.LocalLogSubdirectory), resolved);
    }

    [Fact]
    public void APlainMachineLogsBesideTheApplication()
    {
        var resolved = LogPaths.Resolve(
            null,
            null,
            null,
            Base);

        Assert.Equal(Path.Combine(Base, LogPaths.LocalLogSubdirectory), resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankSettingIsTreatedAsUnset(string blank)
    {
        var resolved = LogPaths.Resolve(
            blank,
            blank,
            blank,
            Base);

        Assert.Equal(Path.Combine(Base, LogPaths.LocalLogSubdirectory), resolved);
    }
}
