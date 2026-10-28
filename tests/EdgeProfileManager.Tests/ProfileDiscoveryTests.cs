using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Tests;

internal sealed class ProfileDiscoveryTests : IDisposable
{
    private readonly FakeEdge _edge = new();

    [Test]
    public void FindsRegisteredProfilesInNumericOrder()
    {
        _edge.AddProfile(EdgeChannel.Stable, "Profile 10", "Zehn");
        _edge.AddProfile(EdgeChannel.Stable, "Profile 2", "Zwei");
        _edge.AddProfile(EdgeChannel.Stable, "Default", "Privat");

        var profiles = new ProfileDiscovery(_edge.Environment).GetProfiles(EdgeChannel.Stable);

        Assert.SequenceEqual(["Default", "Profile 2", "Profile 10"], profiles.Select(p => p.Folder));
        Assert.SequenceEqual(["Privat", "Zwei", "Zehn"], profiles.Select(p => p.Name));
        Assert.Equal("Stable/Profile 2", profiles[1].Key);
    }

    [Test]
    public void IgnoresSystemFoldersAndUninitializedUnregisteredProfiles()
    {
        _edge.AddProfile(EdgeChannel.Beta, "Profile 1", "Kunde");
        _edge.AddProfile(EdgeChannel.Beta, "System Profile", registered: false);
        _edge.AddProfile(EdgeChannel.Beta, "Profile 5", registered: false, preferences: false);
        // Wie im Script: Preferences ohne Registrierung zählt als Profil (Name = Ordner).
        _edge.AddProfile(EdgeChannel.Beta, "Profile 6", registered: false);

        var profiles = new ProfileDiscovery(_edge.Environment).GetProfiles(EdgeChannel.Beta);

        Assert.SequenceEqual(["Profile 1", "Profile 6"], profiles.Select(p => p.Folder));
        Assert.Equal("Profile 6", profiles[1].Name);
    }

    [Test]
    public void CustomerProfileUserWinsOverSignedInAccount()
    {
        _edge.AddProfile(EdgeChannel.Dev, "Profile 1", userName: "konto@firma.ch", customerUser: "kunde@kunde.ch");
        _edge.AddProfile(EdgeChannel.Dev, "Profile 2", userName: "konto@firma.ch");
        _edge.AddProfile(EdgeChannel.Dev, "Profile 3");

        var profiles = new ProfileDiscovery(_edge.Environment).GetProfiles(EdgeChannel.Dev);

        Assert.SequenceEqual(["kunde@kunde.ch", "konto@firma.ch", null], profiles.Select(p => p.User));
    }

    [Test]
    public void CombinesAllChannelsAndToleratesMissingOrBrokenLocalState()
    {
        _edge.AddProfile(EdgeChannel.Stable, "Default");
        var canary = _edge.AddProfile(EdgeChannel.Canary, "Profile 1", registered: false);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(canary)!, "Local State"), "{ kaputt");

        var profiles = new ProfileDiscovery(_edge.Environment).GetProfiles();

        Assert.SequenceEqual(["Stable/Default", "Canary/Profile 1"], profiles.Select(p => p.Key));
    }

    [Test]
    public void DetectsChannelFromExecutablePath()
    {
        Assert.Equal(EdgeChannel.Stable, EdgeChannels.FromExecutablePath(@"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"));
        Assert.Equal(EdgeChannel.Beta, EdgeChannels.FromExecutablePath(@"C:\Program Files (x86)\Microsoft\Edge Beta\Application\msedge.exe"));
        Assert.Equal(EdgeChannel.Canary, EdgeChannels.FromExecutablePath(@"C:\Users\x\AppData\Local\Microsoft\Edge SxS\Application\msedge.exe"));
        Assert.Equal(null, EdgeChannels.FromExecutablePath(@"C:\Program Files\Google\Chrome\Application\chrome.exe"));
    }

    public void Dispose() => _edge.Dispose();
}
