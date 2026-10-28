using System.Text.Json.Nodes;
using EdgeProfileManager.Core.Edge;

namespace EdgeProfileManager.Tests;

/// <summary>Temporäre Edge-Verzeichnisstruktur (%LOCALAPPDATA%\Microsoft\Edge*\User Data) für Tests.</summary>
internal sealed class FakeEdge : IDisposable
{
    public FakeEdge()
    {
        Root = Path.Combine(Path.GetTempPath(), "EdgeProfileManagerTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Environment = new EdgeEnvironment { LocalAppData = Root, ProgramRoots = [Root] };
    }

    public string Root { get; }
    public EdgeEnvironment Environment { get; }

    /// <summary>Legt ein Profil an; <paramref name="registered"/> trägt es in Local State ein.</summary>
    public string AddProfile(EdgeChannel channel, string folder, string? name = null, bool registered = true,
        bool preferences = true, string? userName = null, string? customerUser = null)
    {
        var userData = Environment.UserDataDir(channel);
        var dir = Path.Combine(userData, folder);
        Directory.CreateDirectory(dir);
        if (preferences) File.WriteAllText(Path.Combine(dir, "Preferences"), "{}");
        if (customerUser is not null) File.WriteAllText(Path.Combine(dir, "customer-profile.json"), $$"""{"User":"{{customerUser}}"}""");
        if (registered)
        {
            var statePath = Path.Combine(userData, "Local State");
            var state = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath))!.AsObject() : new JsonObject();
            state["profile"] ??= new JsonObject { ["info_cache"] = new JsonObject() };
            state["profile"]!["info_cache"]![folder] = new JsonObject { ["name"] = name ?? folder, ["user_name"] = userName ?? "" };
            File.WriteAllText(statePath, state.ToJsonString());
        }
        return dir;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}
