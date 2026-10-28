using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace EdgeProfileManager.Dialogs;

/// <summary>Farbauswahl für ein Profil: feste Palette und eigene Farbe.</summary>
public static class ProfileColorDialog
{
    /// <summary>Gut unterscheidbare Farben, angelehnt an die Edge-Farbdesigns.</summary>
    public static readonly (string Name, int Rgb)[] Palette =
    [
        ("Blau", 0x0F6CBD), ("Indigo", 0x4F6BED), ("Lila", 0x8764B8), ("Pink", 0xE3008C),
        ("Rot", 0xD13438), ("Orange", 0xCA5010), ("Gelb", 0xC19C00), ("Limette", 0x73AA24),
        ("Grün", 0x107C10), ("Türkis", 0x038387), ("Grau", 0x69797E), ("Marine", 0x243A5E),
    ];

    public static int? Show(Window owner, string profileName, int? current)
    {
        var dialog = new Window
        {
            Title = $"Farbe für {profileName}",
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
            ShowInTaskbar = false,
        };
        int? result = null;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = "Setzt das Farbdesign und die Profilfarbe in Edge. Edge muss dafür geschlossen sein.",
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
            swatch.Click += (_, _) => { result = rgb; dialog.DialogResult = true; };
            swatches.Children.Add(swatch);
        }
        panel.Children.Add(swatches);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var custom = new Button { Content = "Eigene Farbe…", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        custom.Click += (_, _) =>
        {
            using var picker = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true };
            if (current is { } c) picker.Color = System.Drawing.Color.FromArgb(c | unchecked((int)0xFF000000));
            if (picker.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            result = picker.Color.ToArgb() & 0xFFFFFF;
            dialog.DialogResult = true;
        };
        var cancel = new Button { Content = "Abbrechen", IsCancel = true, Padding = new Thickness(10, 5, 10, 5) };
        buttons.Children.Add(custom);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;

        return dialog.ShowDialog() == true ? result : null;
    }

    public static Color ToColor(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}
