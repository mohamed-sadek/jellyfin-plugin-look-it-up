using System.Xml.Linq;
using Jellyfin.Plugin.LookItUp.Configuration;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.LookItUp.Services;

/// <summary>
/// Effective OpenSubtitles credentials after optional import from Jellyfin's plugin.
/// </summary>
public sealed class OpenSubtitlesEffectiveCredentials
{
    /// <summary>Gets the API key to send on every request.</summary>
    public required string ApiKey { get; init; }

    /// <summary>Gets the OpenSubtitles.com username.</summary>
    public string? Username { get; init; }

    /// <summary>Gets the OpenSubtitles.com password.</summary>
    public string? Password { get; init; }

    /// <summary>Gets whether the username came from Jellyfin's OpenSubtitles plugin.</summary>
    public bool UsernameFromJellyfinPlugin { get; init; }

    /// <summary>Gets whether the password came from Jellyfin's OpenSubtitles plugin.</summary>
    public bool PasswordFromJellyfinPlugin { get; init; }

    /// <summary>Gets whether any field was imported from Jellyfin's OpenSubtitles plugin.</summary>
    public bool UsesJellyfinPluginCredentials =>
        UsernameFromJellyfinPlugin || PasswordFromJellyfinPlugin;
}

/// <summary>
/// Resolves Look it up OpenSubtitles settings, importing from Jellyfin's plugin when empty.
/// </summary>
public static class OpenSubtitlesCredentialResolver
{
    /// <summary>
    /// Shared OpenSubtitles.com API key used by Jellyfin's OpenSubtitles plugin.
    /// </summary>
    public const string JellyfinSharedApiKey = "gUCLWGoAg2PmyseoTM0INFFVPcDCeDlT";

    private const string JellyfinOpenSubtitlesConfigFile = "Jellyfin.Plugin.OpenSubtitles.xml";

    private static readonly object ImportCacheLock = new();
    private static string? _cachedImportPath;
    private static DateTime _cachedImportWriteUtc = DateTime.MinValue;
    private static JellyfinImportedCredentials? _cachedImport;

    /// <summary>
    /// Resolves credentials for API calls. LookItUp fields win; empty fields fall back to Jellyfin's plugin config.
    /// </summary>
    public static OpenSubtitlesEffectiveCredentials Resolve(
        PluginConfiguration config,
        IApplicationPaths appPaths)
    {
        var apiKey = string.IsNullOrWhiteSpace(config.OpenSubtitlesApiKey)
            ? JellyfinSharedApiKey
            : config.OpenSubtitlesApiKey.Trim();

        var username = NullIfWhiteSpace(config.OpenSubtitlesUsername);
        var password = NullIfWhiteSpace(config.OpenSubtitlesPassword);
        var usernameFromJellyfin = false;
        var passwordFromJellyfin = false;

        if (username is null || password is null)
        {
            var imported = ReadJellyfinPluginCredentials(appPaths);
            if (imported is not null)
            {
                if (imported.CredentialsInvalid)
                {
                    // Still try credentials — Jellyfin may have marked them invalid earlier.
                    // OpenSubtitles login will fail clearly if they are still wrong.
                }

                if (username is null && !string.IsNullOrWhiteSpace(imported.Username))
                {
                    username = imported.Username.Trim();
                    usernameFromJellyfin = true;
                }

                if (password is null && !string.IsNullOrWhiteSpace(imported.Password))
                {
                    password = imported.Password;
                    passwordFromJellyfin = true;
                }
            }
        }

        return new OpenSubtitlesEffectiveCredentials
        {
            ApiKey = apiKey,
            Username = username,
            Password = password,
            UsernameFromJellyfinPlugin = usernameFromJellyfin,
            PasswordFromJellyfinPlugin = passwordFromJellyfin
        };
    }

    /// <summary>
    /// Returns true when OpenSubtitles credentials are available (including Jellyfin plugin import).
    /// </summary>
    public static bool IsConfigured(PluginConfiguration config, IApplicationPaths appPaths)
    {
        var creds = Resolve(config, appPaths);
        return !string.IsNullOrWhiteSpace(creds.ApiKey)
               && !string.IsNullOrWhiteSpace(creds.Username)
               && !string.IsNullOrWhiteSpace(creds.Password);
    }

    private static JellyfinImportedCredentials? ReadJellyfinPluginCredentials(IApplicationPaths appPaths)
    {
        var dir = appPaths.PluginConfigurationsPath;
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        var path = Path.Combine(dir, JellyfinOpenSubtitlesConfigFile);
        if (!File.Exists(path))
        {
            lock (ImportCacheLock)
            {
                if (string.Equals(_cachedImportPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _cachedImportPath = null;
                    _cachedImport = null;
                    _cachedImportWriteUtc = DateTime.MinValue;
                }
            }

            return null;
        }

        DateTime writeUtc;
        try
        {
            writeUtc = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
        }

        lock (ImportCacheLock)
        {
            if (_cachedImport is not null
                && string.Equals(_cachedImportPath, path, StringComparison.OrdinalIgnoreCase)
                && writeUtc == _cachedImportWriteUtc)
            {
                return _cachedImport;
            }
        }

        try
        {
            var doc = XDocument.Load(path);
            var root = doc.Root;
            if (root is null)
            {
                return null;
            }

            var imported = new JellyfinImportedCredentials
            {
                Username = ReadElement(root, "Username"),
                Password = ReadElement(root, "Password"),
                CredentialsInvalid = bool.TryParse(ReadElement(root, "CredentialsInvalid"), out var invalid) && invalid
            };

            lock (ImportCacheLock)
            {
                _cachedImportPath = path;
                _cachedImportWriteUtc = writeUtc;
                _cachedImport = imported;
            }

            return imported;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadElement(XElement root, string localName)
    {
        foreach (var element in root.Elements())
        {
            if (element.Name.LocalName.Equals(localName, StringComparison.OrdinalIgnoreCase))
            {
                return string.IsNullOrWhiteSpace(element.Value) ? null : element.Value;
            }
        }

        return null;
    }

    private static string? NullIfWhiteSpace(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private sealed class JellyfinImportedCredentials
    {
        public string? Username { get; init; }

        public string? Password { get; init; }

        public bool CredentialsInvalid { get; init; }
    }
}
