using System.Runtime.InteropServices;

namespace MainframeEngine.Tests.Steamworks;

/// <summary>
/// The no-Steam path: no steam_api native ships, and arm64 processes cannot even load Steamworks.NET, so on every
/// CI runner and dev machine Steam must stay disabled without throwing, and every wrapper must be a quiet no-op.
/// </summary>
[Collection(nameof(SerialSteam))]
public sealed class SteamTests
{
    private const uint Spacewar = 480;

    [Fact]
    public void TryInitializeFailsGracefullyWithAReason()
    {
        var started = Steam.TryInitialize(Spacewar);

        Assert.False(started);
        Assert.False(Steam.Valid);
        Assert.Equal(Spacewar, Steam.AppId);
        Assert.False(string.IsNullOrWhiteSpace(Steam.StatusMessage));
        var expected = Steam.IsPlatformSupported ? SteamStatus.NativeLibraryMissing : SteamStatus.UnsupportedPlatform;
        Assert.Equal(expected, Steam.Status);
        if (expected == SteamStatus.NativeLibraryMissing)
            Assert.Contains(Steam.NativeLibraryFileName, Steam.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void SteamServerIsInertWithoutSteamAndTicksWithTheTree()
    {
        using var servers = new ServerRegistry();
        var steam = new SteamServer(Spacewar);
        servers.Register(steam);
        Assert.False(steam.Started);

        var tree = new SceneTree(servers);
        tree.Tick(new GameTime { DeltaTime = 1f / 60f }); // RunCallbacks is skipped, not thrown
        tree.Shutdown();
    }

    [Fact]
    public void Arm64ProcessesAreReportedAsUnsupported()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
            Assert.Skip("Only meaningful in an arm64 process.");

        Assert.False(Steam.IsPlatformSupported);
        Steam.TryInitialize(Spacewar);
        Assert.Equal(SteamStatus.UnsupportedPlatform, Steam.Status);
        Assert.Contains("Arm64", Steam.StatusMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void AppIdZeroIsRejected()
    {
        Assert.False(Steam.TryInitialize(0));
        Assert.Equal(SteamStatus.InvalidAppId, Steam.Status);
    }

    [Fact]
    public void LifecycleCallsAreNoOpsWithoutSteam()
    {
        Steam.TryInitialize(Spacewar);

        Steam.RunCallbacks();
        Steam.Shutdown();

        Assert.NotEqual(SteamStatus.Shutdown, Steam.Status); // never started, so nothing to shut down
        Assert.False(Steam.Valid);
    }

    [Fact]
    public void WrappersReturnDefaultsWithoutSteam()
    {
        Steam.TryInitialize(Spacewar);

        Assert.Equal(0ul, Steam.SteamId);
        Assert.Equal(string.Empty, Steam.Username);
        Assert.Equal(string.Empty, Steam.Branch);
        Assert.Null(Steam.GetLaunchCommandLine("-connect"));
        Assert.Empty(Steam.GetFriends());
        Assert.Equal(string.Empty, Steam.GetFriendUsername(76561197960287930));
        Assert.False(Steam.HasDLC(1));

        Assert.False(SteamAchievements.IsUnlocked("ACH_WIN_ONE_GAME"));
        Assert.False(SteamAchievements.Unlock("ACH_WIN_ONE_GAME"));
        Assert.False(SteamAchievements.Clear("ACH_WIN_ONE_GAME"));
        Assert.False(SteamAchievements.StoreStats());

        Assert.False(SteamOverlay.IsEnabled());
        SteamOverlay.OpenWeb("https://example.com");
        SteamOverlay.OpenStore(Spacewar);
        SteamOverlay.InviteFriendToGame("123");
        SteamOverlay.InviteFriendToRemotePlay();

        SteamRichPresence.SetStatus("menu");
        SteamRichPresence.SetGroup(2);
        SteamRichPresence.Clear();
        Assert.Equal(-1, SteamRichPresence.GetFriendGroupSize(1));
        Assert.Equal(string.Empty, SteamRichPresence.GetFriendConnect(1));

        var lobby = new SteamLobbyInfo(42) { LobbyName = "ignored", HostId = 7, IsAdvertising = true };
        Assert.Equal(0ul, lobby.HostId);
        Assert.Equal(string.Empty, lobby.LobbyName);
        Assert.False(lobby.IsAdvertising);
        Assert.False(lobby.IsHost);
        Assert.Equal(0, lobby.PlayerCount);
        Assert.Equal(0, lobby.MaxPlayers);
        Assert.Equal(0ul, lobby.OwnerId);
        Assert.Empty(lobby.GetPlayers());
        Assert.Contains("Lobby 42", lobby.ToString(), StringComparison.Ordinal);
        Assert.Equal(new SteamLobbyInfo(42), lobby);
    }

    [Fact]
    public async Task LobbyCallsReturnNothingWithoutSteam()
    {
        Steam.TryInitialize(Spacewar);

        Assert.Null(await SteamLobby.CreateLobbyAsync("lobby", 4, "1.0", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Null(await SteamLobby.JoinLobbyAsync(1234, TestContext.Current.CancellationToken));
        Assert.Empty(await SteamLobby.GetLobbyListAsync(friendsOnly: false, TestContext.Current.CancellationToken));
        Assert.Empty(await SteamLobby.GetLobbyListAsync(friendsOnly: true, TestContext.Current.CancellationToken));
        SteamLobby.LeaveLobby();
        Assert.Null(SteamLobby.Current);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SteamLobby.CreateLobbyAsync("lobby", 0, cancellationToken: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SteamLobby.JoinLobbyAsync(0, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void DevAppIdFileIsWrittenOnceAndOnlyWhenChanged()
    {
        var directory = Directory.CreateTempSubdirectory("mf-steam-");
        try
        {
            var path = Path.Combine(directory.FullName, SteamAppIdFile.FileName);

            Assert.True(SteamAppIdFile.TryWrite(directory.FullName, Spacewar));
            Assert.Equal("480", File.ReadAllText(path));
            var written = File.GetLastWriteTimeUtc(path);

            File.SetLastWriteTimeUtc(path, written.AddMinutes(-5));
            Assert.True(SteamAppIdFile.TryWrite(directory.FullName, Spacewar));
            Assert.Equal(written.AddMinutes(-5), File.GetLastWriteTimeUtc(path)); // unchanged content → not rewritten

            Assert.True(SteamAppIdFile.TryWrite(directory.FullName, 1234));
            Assert.Equal("1234", File.ReadAllText(path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void DevAppIdFileFailureIsReportedNotThrown()
    {
        var missing = Path.Combine(Path.GetTempPath(), "mf-steam-missing-" + Guid.NewGuid().ToString("N"), "nested");

        Assert.False(SteamAppIdFile.TryWrite(missing, Spacewar));
    }

    [Fact]
    public void NativeFileNameMatchesThePlatform()
    {
        var expected = OperatingSystem.IsWindows() ? "steam_api64.dll" : OperatingSystem.IsMacOS() ? "libsteam_api.dylib" : "libsteam_api.so";
        Assert.Equal(expected, Steam.NativeLibraryFileName);
    }
}

[CollectionDefinition(nameof(SerialSteam), DisableParallelization = true)]
public sealed class SerialSteam;
