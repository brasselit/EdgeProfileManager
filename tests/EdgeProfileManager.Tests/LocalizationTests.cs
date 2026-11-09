using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace EdgeProfileManager.Tests;

/// <summary>Every resource file has complete translations with the same placeholders.</summary>
internal sealed class LocalizationTests
{
    private static readonly string[] Cultures = ["de"];

    [Test]
    public void TranslationsHaveTheSameKeysAndPlaceholders()
    {
        var neutral = Directory.EnumerateFiles(Path.Combine(FindRepositoryRoot(), "src"), "*.resx", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileNameWithoutExtension(f).Contains('.'))
            .ToList();
        Assert.True(neutral.Count >= 3, "No resource files found.");

        var problems = new List<string>();
        foreach (var file in neutral)
        {
            var source = Read(file);
            foreach (var culture in Cultures)
            {
                var translatedFile = Path.ChangeExtension(file, $".{culture}.resx");
                if (!File.Exists(translatedFile))
                {
                    problems.Add($"{Path.GetFileName(translatedFile)} is missing");
                    continue;
                }
                var translated = Read(translatedFile);
                var name = Path.GetFileName(translatedFile);
                problems.AddRange(source.Keys.Except(translated.Keys).Select(k => $"{name}: '{k}' not translated"));
                problems.AddRange(translated.Keys.Except(source.Keys).Select(k => $"{name}: '{k}' not in the neutral file"));
                problems.AddRange(source.Keys.Intersect(translated.Keys)
                    .Where(k => Placeholders(source[k]) != Placeholders(translated[k]))
                    .Select(k => $"{name}: '{k}' has different placeholders"));
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void XamlBindingsUseExistingKeys()
    {
        var app = Path.Combine(FindRepositoryRoot(), "src", "EdgeProfileManager");
        var keys = Read(Path.Combine(app, "Resources", "BoardStrings.resx")).Keys.ToHashSet();
        var missing = Directory.EnumerateFiles(app, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), @"\{Binding \[(\w+)\], Source=\{StaticResource Board\}\}")
                .Select(m => $"{Path.GetFileName(f)}: {m.Groups[1].Value}"))
            .Where(k => !keys.Contains(k.Split(": ")[1]))
            .ToList();
        Assert.True(missing.Count == 0, "Unknown resource keys: " + string.Join(", ", missing));
    }

    private static Dictionary<string, string> Read(string file) =>
        XDocument.Load(file).Root!.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name")!, d => (string?)d.Element("value") ?? "");

    private static string Placeholders(string text) =>
        string.Join(",", Regex.Matches(text, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).Distinct().Order());

    private static string FindRepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (dir.GetFiles("*.slnx").Length > 0) return dir.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
