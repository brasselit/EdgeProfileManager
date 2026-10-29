using System.Resources;

namespace EdgeProfileManager;

/// <summary>
/// Texts of a resource file for XAML bindings: <c>{Binding [Key], Source={StaticResource Board}}</c>.
/// XAML must not use <c>x:Static</c> on the generated resource classes: a reference to a type of this
/// project makes WPF compile a temporary assembly, which does not contain the generated classes.
/// </summary>
public sealed class ResourceText(ResourceManager manager)
{
    public string this[string key] => manager.GetString(key) ?? key;
}
