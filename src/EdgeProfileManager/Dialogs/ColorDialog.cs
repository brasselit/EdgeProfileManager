using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EdgeProfileManager.Dialogs;

/// <summary>Color picker for a profile: fixed palette and custom color.</summary>
public static class ProfileColorDialog
{
    /// <summary>Easily distinguishable colors, based on the Edge color themes.</summary>
    public static readonly (string Name, int Rgb)[] Palette =
    [
        ("Blue", 0x0F6CBD), ("Indigo", 0x4F6BED), ("Purple", 0x8764B8), ("Pink", 0xE3008C),
        ("Red", 0xD13438), ("Orange", 0xCA5010), ("Yellow", 0xC19C00), ("Lime", 0x73AA24),
        ("Green", 0x107C10), ("Teal", 0x038387), ("Gray", 0x69797E), ("Navy", 0x243A5E),
    ];

    /// <summary>Returns the chosen color (<c>Rgb</c> null = "No color") or null if canceled.</summary>
    public static ProfileColorChoice? Show(Window owner, string profileName, int? current)
    {
        var dialog = new Window
        {
            Title = $"Color for {profileName}",
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
            ShowInTaskbar = false,
        };
        ProfileColorChoice? result = null;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Sets the color theme and profile color in Edge, or removes them with \"No color\". Edge must be closed for this.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 360,
            Margin = new Thickness(0, 0, 0, 12),
        });

        var swatches = new WrapPanel { MaxWidth = 380 };
        foreach (var (name, rgb) in Palette)
        {
            var swatch = new Button
            {
                Width = 52,
                Height = 52,
                Margin = new Thickness(0, 0, 8, 8),
                ToolTip = $"{name} (#{rgb:X6})",
                Background = new SolidColorBrush(ToColor(rgb)),
                BorderThickness = new Thickness(rgb == current ? 3 : 1),
                BorderBrush = rgb == current ? Brushes.Black : Brushes.White,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            swatch.Click += (_, _) => { result = new ProfileColorChoice(rgb); dialog.DialogResult = true; };
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(swatches);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var custom = new Button { Content = "Custom color…", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        custom.Click += (_, _) =>
        {
            using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (current is { } c) picker.Color = System.Drawing.Color.FromArgb(c | unchecked((int)0xFF000000));
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            result = new ProfileColorChoice(picker.Color.ToArgb() & 0xFFFFFF);
            dialog.DialogResult = true;
        };
        var none = new Button
        {
            Content = "No color",
            Padding = new Thickness(10, 5, 10, 5),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Remove the color theme and return to the default Edge theme",
            IsEnabled = current is not null,
        };
        none.Click += (_, _) => { result = new ProfileColorChoice(null); dialog.DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(10, 5, 10, 5) };
        buttons.Children.Add(none);
        buttons.Children.Add(custom);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        return dialog.ShowDialog() == true ? result : null;
    }

    public static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>Result of the color dialog; <see cref="Rgb"/> null = no color.</summary>
public sealed record ProfileColorChoice(int? Rgb);
