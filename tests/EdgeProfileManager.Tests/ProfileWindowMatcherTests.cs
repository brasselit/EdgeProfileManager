using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Tests;

internal sealed class ProfileWindowMatcherTests
{
    private static readonly EdgeProfile[] Profiles =
    [
        new(EdgeChannel.Stable, "Default", "isolutions", null, "", null),
        new(EdgeChannel.Stable, "Profile 1", "Privat", null, "", null),
        new(EdgeChannel.Stable, "Profile 10", "Kunde A", null, "", null),
        new(EdgeChannel.Beta, "Profile 1", "Kunde", null, "", null),
    ];

    private static EdgeWindow Window(EdgeChannel channel, string? aumid, string title = "Seite - X - Microsoft​ Edge") =>
        new(1, 1, channel, title, aumid, false);

    [Test]
    public void MatchesByAppUserModelId()
    {
        Assert.Equal("Default", ProfileWindowMatcher.FindProfile(Window(EdgeChannel.Stable, "MSEdge"), Profiles)?.Folder);
        Assert.Equal("Profile 1", ProfileWindowMatcher.FindProfile(Window(EdgeChannel.Stable, "MSEdge.UserData.Profile1"), Profiles)?.Folder);
        Assert.Equal("Profile 10", ProfileWindowMatcher.FindProfile(Window(EdgeChannel.Stable, "MSEdge.UserData.Profile10"), Profiles)?.Folder);
        Assert.Equal(EdgeChannel.Beta, ProfileWindowMatcher.FindProfile(Window(EdgeChannel.Beta, "MSEdgeBeta.UserData.Profile1"), Profiles)?.Channel);
    }

    [Test]
    public void UnknownSuffixIsNotTreatedAsDefault()
    {
        Assert.Equal(null, ProfileWindowMatcher.FindProfile(Window(EdgeChannel.Stable, "MSEdge.UserData.Profile7"), Profiles));
        // Beta hat kein Default-Profil.
        Assert.Equal(null, ProfileWindowMatcher.FindProfile(Window(EdgeChannel.Beta, "MSEdgeBeta"), Profiles));
    }

    [Test]
    public void FallsBackToTitleWithEnDashOrHyphen()
    {
        var enDash = Window(EdgeChannel.Stable, null, "Neuer Tab und 3 weitere Seiten – Kunde A – Microsoft​ Edge");
        var hyphen = Window(EdgeChannel.Stable, null, "Neuer Tab - Privat - Microsoft Edge");
        Assert.Equal("Profile 10", ProfileWindowMatcher.FindProfile(enDash, Profiles)?.Folder);
        Assert.Equal("Profile 1", ProfileWindowMatcher.FindProfile(hyphen, Profiles)?.Folder);
    }

    [Test]
    public void MatchesRequiresSameChannel()
    {
        var beta = Window(EdgeChannel.Beta, "MSEdgeBeta.UserData.Profile1");
        Assert.True(!ProfileWindowMatcher.Matches(beta, Profiles[1], Profiles), "Stable-Profil darf nicht auf Beta-Fenster passen");
        Assert.True(ProfileWindowMatcher.Matches(beta, Profiles[3], Profiles));
    }
}
