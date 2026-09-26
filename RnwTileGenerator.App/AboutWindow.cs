using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace RnwTileGenerator.App;

/// <summary>Help -> About: name, version, license and a link to the GitHub repository.</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow(Window owner)
    {
        Title = Loc.T("help.about.title");
        Owner = owner;
        Icon = AppIcon.Frame;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "RNW Tile Generator", FontSize = 18, FontWeight = FontWeights.Bold });
        root.Children.Add(new TextBlock { Text = string.Format(Loc.T("help.about.version"), AppInfo.Version), Margin = new Thickness(0, 4, 0, 10) });
        root.Children.Add(new TextBlock { Text = Loc.T("help.about.license"), TextWrapping = TextWrapping.Wrap });

        var link = new Hyperlink(new Run(AppInfo.RepositoryUrl)) { NavigateUri = new Uri(AppInfo.RepositoryUrl) };
        link.RequestNavigate += (_, e) =>
        {
            try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception ex) { Dialogs.Error(this, UpdateTexts.ErrorTitle, UpdateTexts.BrowserFailed(ex.Message)); }
        };
        root.Children.Add(new TextBlock { Text = Loc.T("help.about.repository"), Margin = new Thickness(0, 10, 0, 2) });
        root.Children.Add(new TextBlock(link));

        var close = new Button { Content = Loc.T("help.about.close"), IsCancel = true, IsDefault = true, MinWidth = 90, Margin = new Thickness(0, 16, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;
    }
}
