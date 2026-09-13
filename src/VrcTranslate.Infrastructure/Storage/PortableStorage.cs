namespace VrcTranslate.Infrastructure.Storage;

/// <summary>
/// Resolves where VRCTranslate keeps settings, voiceprints and on-demand model
/// downloads. The product ships as a portable folder, so the default is a "data"
/// folder beside the executable and nothing is written into the user profile.
/// An install that cannot be written (Program Files, a read-only share) falls
/// back to %LOCALAPPDATA%\VRCTranslate so settings still survive a restart, and
/// <see cref="UsesUserProfile"/> reports that so the UI can say so.
/// VRC_TRANSLATE_DATA_DIR overrides everything, which is how the UI smoke tests
/// keep their data isolated.
/// </summary>
public static class PortableStorage
{
    public const string OverrideEnvironmentVariable = "VRC_TRANSLATE_DATA_DIR";

    /// <summary>Folder created beside the executable in the default portable layout.</summary>
    public const string FolderName = "data";

    private static readonly Lazy<Resolution> Current = new(
        () => Select(
            Environment.GetEnvironmentVariable(OverrideEnvironmentVariable),
            Path.Combine(AppContext.BaseDirectory, FolderName),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCTranslate"),
            TryPrepare),
        isThreadSafe: true);

    public static string DataDirectory => Current.Value.Directory;

    /// <summary>True when settings had to go to the user profile because the application folder is read-only.</summary>
    public static bool UsesUserProfile => Current.Value.UsesUserProfile;

    /// <summary>The portable folder that was rejected, for diagnostics; null when the portable layout is in use.</summary>
    public static string? RejectedPortableDirectory => Current.Value.RejectedPortableDirectory;

    public static string GetPath(string fileName) => Path.Combine(DataDirectory, fileName);

    public static string Combine(params string[] parts)
    {
        var all = new string[parts.Length + 1];
        all[0] = DataDirectory;
        Array.Copy(parts, 0, all, 1, parts.Length);
        return Path.Combine(all);
    }

    /// <summary>
    /// Copies settings written by the pre-portable layout, which always used the
    /// user profile, into the portable folder. Runs once, keeps the originals and
    /// skips downloads because those are reproducible.
    /// </summary>
    public static void MigrateLegacyData()
    {
        if (Current.Value.UsesUserProfile || Current.Value.IsOverridden) return;
        var legacy = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VRCTranslate");
        foreach (var name in AppDataFiles.All)
        {
            try
            {
                var source = Path.Combine(legacy, name);
                var destination = GetPath(name);
                if (File.Exists(source) && !File.Exists(destination)) File.Copy(source, destination);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // A migration failure must never stop the application from starting.
            }
        }
    }

    /// <summary>Pure candidate selection, shared with tests: the first writable candidate wins.</summary>
    public static Resolution Select(
        string? configuredDirectory,
        string portableDirectory,
        string userProfileDirectory,
        Func<string, bool> canWrite)
    {
        ArgumentNullException.ThrowIfNull(canWrite);
        if (!string.IsNullOrWhiteSpace(configuredDirectory))
        {
            var configured = Path.GetFullPath(configuredDirectory.Trim());
            if (!canWrite(configured))
            {
                throw new InvalidOperationException($"数据目录不可写：{configured}");
            }

            return new Resolution(configured, UsesUserProfile: false, IsOverridden: true, RejectedPortableDirectory: null);
        }

        if (canWrite(portableDirectory))
        {
            return new Resolution(portableDirectory, UsesUserProfile: false, IsOverridden: false, RejectedPortableDirectory: null);
        }

        if (!canWrite(userProfileDirectory))
        {
            throw new InvalidOperationException("找不到可写的数据目录。");
        }

        return new Resolution(
            userProfileDirectory,
            UsesUserProfile: true,
            IsOverridden: false,
            RejectedPortableDirectory: portableDirectory);
    }

    private static bool TryPrepare(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".portable-probe");
            using (var stream = new FileStream(probe, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.WriteByte(0);
            }

            File.Delete(probe);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public sealed record Resolution(
        string Directory,
        bool UsesUserProfile,
        bool IsOverridden,
        string? RejectedPortableDirectory);
}