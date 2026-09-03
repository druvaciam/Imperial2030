using System;
using System.IO;

namespace Imperial2030.Server.Configuration;

/// <summary>
/// Where NLog's rolling file target writes.
///
/// The app directory is not always writable. Azure App Service runs this site with
/// WEBSITE_RUN_FROM_PACKAGE=1, which mounts the deployed zip READ-ONLY - so nlog.config's original
/// "${basedir}/logs" could never create its directory there, and an NLog file target that cannot write
/// does not fail startup, it just silently produces nothing.
///
/// The writable location is derived rather than configured, because its literal value is not knowable
/// from the repository: App Service exposes it through HOME, which is "/home" on Linux and a drive-rooted
/// path on Windows whose letter has differed between stamps. Hardcoding any of those would be a guess.
/// </summary>
public static class LogPaths
{
    /// <summary>Optional explicit override, read from the environment. Wins over everything below.</summary>
    public const string LogDirectoryVariable = "LOG_DIRECTORY";

    /// <summary>
    /// Set by Azure App Service and by nothing else, which is what makes it a safe discriminator.
    /// HOME alone is not: it is set on every Linux desktop and inside Git Bash on Windows, where it
    /// points at the developer's home directory - so keying off HOME by itself would quietly relocate
    /// local development logs to ~/LogFiles.
    /// </summary>
    public const string AppServiceMarkerVariable = "WEBSITE_SITE_NAME";

    /// <summary>The persistent, writable share on App Service. "/home" on Linux, drive-rooted on Windows.</summary>
    public const string HomeVariable = "HOME";

    /// <summary>Subdirectory of HOME that App Service's log stream reads.</summary>
    public const string AppServiceLogSubdirectory = "LogFiles";

    /// <summary>Subdirectory of the application directory used everywhere else.</summary>
    public const string LocalLogSubdirectory = "logs";

    /// <summary>Resolves the log directory from the current environment.</summary>
    public static string Resolve() => Resolve(
        Environment.GetEnvironmentVariable(LogDirectoryVariable),
        Environment.GetEnvironmentVariable(HomeVariable),
        Environment.GetEnvironmentVariable(AppServiceMarkerVariable),
        AppContext.BaseDirectory);

    /// <summary>
    /// The decision itself, as a pure function of its inputs.
    ///
    /// Split from the environment read above rather than defaulting each parameter to a
    /// GetEnvironmentVariable call: with that shape a null argument means both "not supplied" and
    /// "genuinely unset", so a test could not express an absent HOME - it would silently pick up the
    /// developer's own, which is exactly what happened the first time these tests ran.
    /// </summary>
    public static string Resolve(
        string? configuredDirectory,
        string? home,
        string? appServiceSiteName,
        string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredDirectory)) return configuredDirectory;

        bool onAppService = !string.IsNullOrWhiteSpace(appServiceSiteName) && !string.IsNullOrWhiteSpace(home);

        return onAppService
            ? Path.Combine(home!, AppServiceLogSubdirectory)
            : Path.Combine(baseDirectory, LocalLogSubdirectory);
    }

    /// <summary>
    /// Publishes the resolved directory back into the environment so nlog.config's
    /// "${environment:variable=LOG_DIRECTORY}" picks it up. Must run BEFORE AddNLog, which is when the
    /// config file is read. A value already present is preserved by <see cref="Resolve"/>, so calling
    /// this never overrides an explicit setting.
    /// </summary>
    public static string ApplyToEnvironment()
    {
        var directory = Resolve();
        Environment.SetEnvironmentVariable(LogDirectoryVariable, directory);
        return directory;
    }
}
