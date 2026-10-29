using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace EdgeProfileManager.Dialogs;

public enum FieldType { Text, Combo, File, Folder }

public sealed record Field(string Key, string Label, FieldType Type = FieldType.Text)
{
    public string? Value { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public bool Required { get; init; }
    public string? Filter { get; init; }
}

/// <summary>Simple input dialog built from a list of fields (port of Show-GuiFields).</summary>
public static class FieldsDialog
{
    public static Dictionary<string, string>? Show(Window owner, string title, params Field[] fields)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = owner,
            Width = 530,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
            ShowInTaskbar = false,
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        dialog.Content = panel;
        var getters = new Dictionary<string, Func<string>>();

        foreach (var field in fields)
        {
            panel.Children.Add(new TextBlock { Text = field.Label, Margin = new Thickness(0, 0, 0, 5) });
            switch (field.Type)
            {
                case FieldType.Combo:
                    var combo = new ComboBox { ItemsSource = field.Options, Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(6) };
                    combo.SelectedItem = field.Value is not null && field.Options?.Contains(field.Value) == true ? field.Value : field.Options?.FirstOrDefault();
                    panel.Children.Add(combo);
                    getters[field.Key] = () => combo.SelectedItem as string ?? string.Empty;
                    break;
                case FieldType.File or FieldType.Folder:
                    var row = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
                    var browse = new Button { Content = "…", Width = 36, Margin = new Thickness(6, 0, 0, 0) };
                    DockPanel.SetDock(browse, Dock.Right);
                    var pathBox = new TextBox { Text = field.Value ?? string.Empty, Padding = new Thickness(6) };
                    browse.Click += (_, _) =>
                    {
                        if (field.Type == FieldType.File)
                        {
                            var open = new OpenFileDialog { Filter = field.Filter ?? "All files|*.*", FileName = pathBox.Text };
                            if (open.ShowDialog(dialog) == true) pathBox.Text = open.FileName;
                        }
                        else
                        {
                            var folder = new OpenFolderDialog { InitialDirectory = Directory.Exists(pathBox.Text) ? pathBox.Text : null };
                            if (folder.ShowDialog(dialog) == true) pathBox.Text = folder.FolderName;
                        }
                    };
                    row.Children.Add(browse);
                    row.Children.Add(pathBox);
                    panel.Children.Add(row);
                    getters[field.Key] = () => pathBox.Text.Trim().Trim('"');
                    break;
                default:
                    var text = new TextBox { Text = field.Value ?? string.Empty, Margin = new Thickness(0, 0, 0, 14), Padding = new Thickness(6) };
                    panel.Children.Add(text);
                    getters[field.Key] = () => text.Text.Trim();
                    break;
            }
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var ok = new Button { Content = "OK", IsDefault = true, Width = 90, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(8, 5, 8, 5) };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Width = 90, Padding = new Thickness(8, 5, 8, 5) };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        Dictionary<string, string>? result = null;
        ok.Click += (_, _) =>
        {
            var values = getters.ToDictionary(g => g.Key, g => g.Value());
            var missing = fields.FirstOrDefault(f => f.Required && string.IsNullOrWhiteSpace(values[f.Key]));
            if (missing is not null)
            {
                MessageBox.Show(dialog, $"Please fill in \"{missing.Label}\".", title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            result = values;
            dialog.DialogResult = true;
        };
        dialog.ShowDialog();
        return result;
    }
}
