using System.Collections.Generic;
using System.Security.Claims;
using Imperial2030.Server.Controllers;
using Xunit;

namespace Imperial2030.Tests;

/// <summary>
/// How a caller is identified for replay-session capacity accounting.
///
/// Originally this was the transport-level remote address alone. That is correct in principle — a
/// client can forge X-Forwarded-For, so trusting it would hand an attacker a fresh budget per request
/// — but it collapses every caller into ONE owner behind a reverse proxy that does not rewrite the
/// connection address, which is exactly how the VPS runs nginx. The observed result: a signed-in user
/// was refused with "You already have the maximum number of replay sessions open" because unrelated
/// traffic had consumed the shared five-session budget.
///
/// An authenticated caller carries an identity the server minted and verified, so it cannot be forged
/// the way a header can. Using it in preference to the address gives every signed-in user (and every
/// guest, who also holds a token) their own budget regardless of how many proxies sit in front.
/// </summary>
public class ReplayOwnerKeyTests
{
    private const string ProxyAddress = "10.0.0.7";   // what nginx presents for everyone
    private const string OtherAddress = "10.0.0.9";

    private static ClaimsPrincipal UserWithId(string id) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, id) }, "jwt"));

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    /// <summary>The reported bug: two signed-in users behind one proxy must not share a budget.</summary>
    [Fact]
    public void TwoAuthenticatedUsersBehindTheSameProxyGetDifferentKeys()
    {
        var a = GamesController.ResolveReplayOwnerKey(UserWithId("user-a"), ProxyAddress);
        var b = GamesController.ResolveReplayOwnerKey(UserWithId("user-b"), ProxyAddress);

        Assert.NotEqual(a, b);
    }

    /// <summary>
    /// Identity wins over address, so the same person is billed to one budget however they connect —
    /// switching networks or reconnecting through a different proxy must not mint a fresh allowance.
    /// </summary>
    [Fact]
    public void TheSameUserFromDifferentAddressesGetsOneKey()
    {
        var fromProxy = GamesController.ResolveReplayOwnerKey(UserWithId("user-a"), ProxyAddress);
        var fromElsewhere = GamesController.ResolveReplayOwnerKey(UserWithId("user-a"), OtherAddress);

        Assert.Equal(fromProxy, fromElsewhere);
    }

    /// <summary>
    /// Guests hold a real token with a NameIdentifier, so they are keyed per guest rather than lumped
    /// into the anonymous bucket. This is what keeps the Blazor client's watch-only flow working while
    /// the load-test tool hammers the same endpoint anonymously.
    /// </summary>
    [Fact]
    public void TwoGuestsAreKeyedSeparately()
    {
        var first = GamesController.ResolveReplayOwnerKey(UserWithId("guest-1"), ProxyAddress);
        var second = GamesController.ResolveReplayOwnerKey(UserWithId("guest-2"), ProxyAddress);

        Assert.NotEqual(first, second);
    }

    /// <summary>
    /// With no identity to go on there is nothing better than the address. Anonymous callers behind one
    /// proxy do still share a budget — accepted deliberately, since the global cap is what protects the
    /// process and an unauthenticated flood is the case the per-caller cap was aimed at in the first place.
    /// </summary>
    [Fact]
    public void AnonymousCallersFallBackToTheRemoteAddress()
    {
        var a = GamesController.ResolveReplayOwnerKey(Anonymous(), ProxyAddress);
        var b = GamesController.ResolveReplayOwnerKey(Anonymous(), ProxyAddress);
        var elsewhere = GamesController.ResolveReplayOwnerKey(Anonymous(), OtherAddress);

        Assert.Equal(a, b);
        Assert.NotEqual(a, elsewhere);
    }

    /// <summary>
    /// An authenticated caller and an anonymous one from the same address must not collide — otherwise a
    /// signed-in user could be locked out by anonymous traffic, which is the bug being fixed.
    /// </summary>
    [Fact]
    public void AnAuthenticatedUserIsNeverKeyedAsAnonymousTraffic()
    {
        var signedIn = GamesController.ResolveReplayOwnerKey(UserWithId("user-a"), ProxyAddress);
        var anonymous = GamesController.ResolveReplayOwnerKey(Anonymous(), ProxyAddress);

        Assert.NotEqual(signedIn, anonymous);
    }

    /// <summary>A missing address must still produce a usable, stable key rather than throwing.</summary>
    [Fact]
    public void AMissingAddressStillProducesAKey()
    {
        var key = GamesController.ResolveReplayOwnerKey(Anonymous(), null);

        Assert.False(string.IsNullOrWhiteSpace(key));
    }

    /// <summary>
    /// A user id that happens to look like an address must not be able to impersonate the anonymous
    /// bucket for that address — the two namespaces are kept distinct.
    /// </summary>
    [Fact]
    public void AUserIdShapedLikeAnAddressDoesNotCollideWithThatAddressesAnonymousBucket()
    {
        var spoofer = GamesController.ResolveReplayOwnerKey(UserWithId(ProxyAddress), OtherAddress);
        var anonymousAtThatAddress = GamesController.ResolveReplayOwnerKey(Anonymous(), ProxyAddress);

        Assert.NotEqual(spoofer, anonymousAtThatAddress);
    }
}
