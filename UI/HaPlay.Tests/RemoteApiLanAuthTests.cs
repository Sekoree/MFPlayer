using Avalonia.Headless;
using HaPlay.Models;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

/// <summary>
/// F-15 (2026-08-14 review): LAN control is AUTHENTICATED BY DEFAULT. Enabling LAN mints a
/// high-entropy token; tokenless LAN survives only through the explicit exception; an older
/// settings file that relied on LAN-without-token gets a token on load instead of silently staying
/// open through an upgrade; and clearing the token without the exception pauses the LAN binding
/// rather than serving the network open.
/// </summary>
public sealed class RemoteApiLanAuthTests
{
    private static void DispatchUi(Action action) =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(RemoteApiLanAuthTests).Assembly)
            .DispatchGuarded(action, CancellationToken.None)
            .GetAwaiter()
            .GetResult();

    private static void WithIsolatedSettings(Action<string> body)
    {
        var dir = Directory.CreateTempSubdirectory("haplay-lan-auth-").FullName;
        AppSettings.FilePathOverride = Path.Combine(dir, "app-settings.json");
        try
        {
            body(dir);
        }
        finally
        {
            AppSettings.FilePathOverride = null;
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void EnablingLan_MintsATokenByDefault()
    {
        WithIsolatedSettings(_ => DispatchUi(static () =>
        {
            var vm = new MainViewModel();
            Assert.Empty(vm.RestApiAccessToken);

            vm.RestApiAllowLan = true;

            // Fresh LAN enablement is authenticated - a high-entropy token appears, and neither
            // warning state is active.
            Assert.NotEmpty(vm.RestApiAccessToken);
            Assert.True(vm.RestApiAccessToken.Length >= 32);
            Assert.False(vm.RestApiOpenLanActive);
            Assert.False(vm.RestApiLanPausedForMissingToken);
            vm.ShutdownCleanup();
        }));
    }

    [Fact]
    public void UpgradeFromTokenlessLanSettings_MintsATokenOnLoad()
    {
        WithIsolatedSettings(_ =>
        {
            // The pre-F-15 shape: LAN on, no token, no exception (it did not exist yet).
            AppSettings.Update(s =>
            {
                s.RestApiAllowLan = true;
                s.RestApiAccessToken = null;
                s.RestApiAllowTokenlessLan = false;
            });

            DispatchUi(static () =>
            {
                var vm = new MainViewModel();

                // Never silently preserve tokenless mode through an upgrade.
                Assert.NotEmpty(vm.RestApiAccessToken);
                Assert.NotNull(AppSettings.Load().RestApiAccessToken); // persisted, not just in memory
                vm.ShutdownCleanup();
            });
        });
    }

    [Fact]
    public void ExplicitTokenlessException_KeepsLanTokenless()
    {
        WithIsolatedSettings(_ => DispatchUi(static () =>
        {
            var vm = new MainViewModel();
            vm.RestApiAllowTokenlessLan = true;

            vm.RestApiAllowLan = true;

            // The exception is what permits tokenless LAN - no token is minted behind it.
            Assert.Empty(vm.RestApiAccessToken);
            Assert.False(vm.RestApiLanPausedForMissingToken);
            vm.ShutdownCleanup();
        }));
    }

    [Fact]
    public void WithdrawingTheException_WhileTokenless_MintsAToken()
    {
        WithIsolatedSettings(_ => DispatchUi(static () =>
        {
            var vm = new MainViewModel();
            vm.RestApiAllowTokenlessLan = true;
            vm.RestApiAllowLan = true;
            Assert.Empty(vm.RestApiAccessToken);

            vm.RestApiAllowTokenlessLan = false;

            // Authenticated LAN is restored rather than silently pausing the binding.
            Assert.NotEmpty(vm.RestApiAccessToken);
            vm.ShutdownCleanup();
        }));
    }

    [Fact]
    public void ClearingTheToken_WithoutTheException_PausesLanBinding()
    {
        WithIsolatedSettings(_ => DispatchUi(static () =>
        {
            var vm = new MainViewModel();
            vm.RestApiPort = FreePort();
            vm.RestApiAllowLan = true;      // mints a token
            vm.RestApiEnabled = true;
            Assert.NotEmpty(vm.RestApiAccessToken);

            vm.ClearRestApiTokenCommand.Execute(null);

            // Fail closed: LAN requested, no credential, no exception - the paused warning is on
            // and the open-LAN state is NOT.
            Assert.True(vm.RestApiLanPausedForMissingToken);
            Assert.False(vm.RestApiOpenLanActive);

            vm.RestApiEnabled = false;
            vm.ShutdownCleanup();
        }));
    }

    private static int FreePort()
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
