using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DshShell;

/// <summary>Settings editor: URL, launch command, and working directory.</summary>
public sealed partial class SettingsDialog : ContentDialog
{
    /// <summary>The validated settings when the dialog closed with Save.</summary>
    public ShellSettings Result { get; private set; }

    public SettingsDialog(ShellSettings current)
    {
        InitializeComponent();
        UrlBox.Text = current.Url;
        CommandBox.Text = current.Command;
        CwdBox.Text = current.WorkingDirectory;
        Result = current;
    }

    private void OnPrimaryClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var url = UrlBox.Text.Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ErrorText.Text = "URL must be an absolute http(s) address.";
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
            return;
        }
        Result = new ShellSettings
        {
            Url = url,
            Command = CommandBox.Text.Trim(),
            WorkingDirectory = CwdBox.Text.Trim(),
            StartTimeoutSeconds = 90,
        };
    }
}
