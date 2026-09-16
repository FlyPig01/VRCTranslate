using VrcTranslate.Infrastructure.Storage;
using Xunit;

namespace VrcTranslate.Infrastructure.Tests;

public sealed class PortableStorageTests
{
    private static readonly Func<string, bool> AllWritable = _ => true;
    private static readonly Func<string, bool> NoneWritable = _ => false;

    [Fact]
    public void Portable_folder_beside_the_executable_wins_by_default()
    {
        var resolution = PortableStorage.Select(
            configuredDirectory: null,
            portableDirectory: @"D:\app\data",
            userProfileDirectory: @"C:\Users\me\AppData\Local\VRCTranslate",
            canWrite: AllWritable);

        Assert.Equal(@"D:\app\data", resolution.Directory);
        Assert.False(resolution.UsesUserProfile);
        Assert.False(resolution.IsOverridden);
        Assert.Null(resolution.RejectedPortableDirectory);
    }

    [Fact]
    public void Read_only_install_falls_back_to_the_user_profile_and_says_so()
    {
        var resolution = PortableStorage.Select(
            configuredDirectory: null,
            portableDirectory: @"C:\Program Files\VRCTranslate\data",
            userProfileDirectory: @"C:\Users\me\AppData\Local\VRCTranslate",
            canWrite: directory => directory != @"C:\Program Files\VRCTranslate\data");

        Assert.Equal(@"C:\Users\me\AppData\Local\VRCTranslate", resolution.Directory);
        Assert.True(resolution.UsesUserProfile);
        Assert.Equal(@"C:\Program Files\VRCTranslate\data", resolution.RejectedPortableDirectory);
    }

    [Fact]
    public void Environment_override_wins_over_both_candidates()
    {
        var resolution = PortableStorage.Select(
            configuredDirectory: @"E:\portable-data",
            portableDirectory: @"D:\app\data",
            userProfileDirectory: @"C:\Users\me\AppData\Local\VRCTranslate",
            canWrite: AllWritable);

        Assert.Equal(@"E:\portable-data", resolution.Directory);
        Assert.True(resolution.IsOverridden);
        Assert.False(resolution.UsesUserProfile);
    }

    [Fact]
    public void Unwritable_override_is_reported_instead_of_silently_relocated()
    {
        Assert.Throws<InvalidOperationException>(() => PortableStorage.Select(
            configuredDirectory: @"Z:\missing",
            portableDirectory: @"D:\app\data",
            userProfileDirectory: @"C:\Users\me\AppData\Local\VRCTranslate",
            canWrite: NoneWritable));
    }

    [Fact]
    public void No_writable_candidate_is_an_error()
    {
        Assert.Throws<InvalidOperationException>(() => PortableStorage.Select(
            configuredDirectory: null,
            portableDirectory: @"D:\app\data",
            userProfileDirectory: @"C:\Users\me\AppData\Local\VRCTranslate",
            canWrite: NoneWritable));
    }

    [Fact]
    public void Storage_contract_does_not_expose_automatic_legacy_migration()
    {
        Assert.Null(typeof(PortableStorage).GetMethod(
            "MigrateLegacyData",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static));
    }
}
