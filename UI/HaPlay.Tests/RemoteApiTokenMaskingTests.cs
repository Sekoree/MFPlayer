using Avalonia.Headless;
using HaPlay.ViewModels;
using Xunit;

namespace HaPlay.Tests;

public sealed class RemoteApiTokenMaskingTests
{
    // API-01: the remote-API token must never be shown in the clear by default (the review captured the full
    // token as plain selectable text on the Project workspace). It is masked until the operator explicitly
    // reveals it, and clearing the token also drops the reveal state so a later token starts masked again.
    [Fact]
    // Returns the Dispatch Task so xunit awaits it: the earlier `void` body DISCARDED it, which
    // threw away every assertion failure raised inside the dispatched lambda (the test passed
    // no matter what the code under test did).
    public Task Token_IsMasked_UntilRevealed_AndClearingResetsReveal() =>
        HeadlessUnitTestSession
            .GetOrStartForAssembly(typeof(RemoteApiTokenMaskingTests).Assembly)
            .DispatchGuarded(static () =>
            {
                var vm = new MainViewModel();

                vm.RegenerateRestApiTokenCommand.Execute(null);
                var token = vm.RestApiAccessToken;
                Assert.False(string.IsNullOrEmpty(token));

                // Masked by default: the display shows neither the token nor its length.
                Assert.False(vm.RevealRestApiToken);
                Assert.NotEqual(token, vm.RestApiAccessTokenDisplay);
                Assert.DoesNotContain(token, vm.RestApiAccessTokenDisplay);

                // Revealing shows the raw token.
                vm.RevealRestApiToken = true;
                Assert.Equal(token, vm.RestApiAccessTokenDisplay);

                // Clearing removes the token and re-masks for the next one.
                vm.ClearRestApiTokenCommand.Execute(null);
                Assert.True(string.IsNullOrEmpty(vm.RestApiAccessToken));
                Assert.False(vm.RevealRestApiToken);
            }, CancellationToken.None);
}
