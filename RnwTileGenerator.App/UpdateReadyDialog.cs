using System.Windows;
using System.Windows.Controls;
using RnwTileGenerator.Updates;

namespace RnwTileGenerator.App;

/// <summary>
/// "Version X is ready" question before the restart. With an open tile: save and restart / restart without
/// saving / cancel; without a tile only restart / cancel.
/// </summary>
public sealed class UpdateReadyDialog : Window
{
    public UpdateRestartChoice Choice { get; private set; } = UpdateRestartChoice.Cancel;

    private UpdateReadyDialog(Window owner, AppVersion version, bool hasProject)
    {
        Title = UpdateTexts.ReadyTitle;
        Owner = owner;
        Icon = AppIcon.Frame;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(new TextBlock { Text = UpdateTexts.ReadyQuestion(version), TextWrapping = TextWrapping.Wrap });
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        if (hasProject)
        {
            row.Children.Add(MakeButton(Loc.T("update.ready.save"), UpdateRestartChoice.SaveAndRestart, isDefault: true));
            row.Children.Add(MakeButton(Loc.T("update.ready.restartNoSave"), UpdateRestartChoice.RestartWithoutSaving));
        }
        else
        {
            row.Children.Add(MakeButton(Loc.T("update.ready.restart"), UpdateRestartChoice.RestartWithoutSaving, isDefault: true));
        }

        var cancel = MakeButton(UpdateTexts.Cancel, UpdateRestartChoice.Cancel);
        cancel.IsCancel = true;
        row.Children.Add(cancel);
        root.Children.Add(row);
        Content = root;
    }

    public static UpdateRestartChoice Ask(Window owner, AppVersion version, bool hasProject)
    {
        var dialog = new UpdateReadyDialog(owner, version, hasProject);
        dialog.ShowDialog();
        return dialog.Choice;
    }

    private Button MakeButton(string text, UpdateRestartChoice choice, bool isDefault = false)
    {
        var button = new Button { Content = text, IsDefault = isDefault, MinWidth = 90, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(6, 0, 0, 0) };
        button.Click += (_, _) => { Choice = choice; Close(); };
        return button;
    }
}
