using System.Diagnostics;
using System.Windows;

namespace LaunchHere;

/// <summary>"Report a problem": shows the report for the user to check and edit, then opens a pre-filled
/// GitHub "new issue" page in the browser (or copies the report). Nothing is sent from the app.</summary>
public partial class ReportWindow : Window
{
    readonly Func<Config> _config;
    readonly Logger _log;
    readonly Action _enableVerbose;
    bool _settingDetails, _detailsEdited;

    internal ReportWindow(Func<Config> config, Logger log, Action enableVerbose)
    {
        Wpf.EnsureInitialized();
        InitializeComponent();
        Wpf.EnableKeyboard(this);
        Icon = Wpf.ToImageSource(TrayContext.MakeIcon(true));

        _config = config;
        _log = log;
        _enableVerbose = enableVerbose;

        DetailsBox.TextChanged += (_, _) => { if (!_settingDetails) _detailsEdited = true; };
        HideTitlesBox.Checked += (_, _) => FillDetails(askFirst: true);
        HideTitlesBox.Unchecked += (_, _) => FillDetails(askFirst: true);
        RefreshButton.Click += (_, _) => FillDetails(askFirst: true);
        VerboseButton.Click += (_, _) => TurnOnVerbose();
        OpenIssueButton.Click += (_, _) => OpenIssue();
        CopyButton.Click += (_, _) => Copy();
        CloseButton.Click += (_, _) => Close();

        FillDetails(askFirst: false);
        Loaded += (_, _) => TitleBox.Focus();
    }

    void FillDetails(bool askFirst)
    {
        var cfg = _config();
        // After "Turn on verbose logging" the card stays, showing what to do next.
        if (VerboseButton.IsEnabled) VerboseCard.Visibility = cfg.VerboseLog ? Visibility.Collapsed : Visibility.Visible;

        if (askFirst && _detailsEdited &&
            MessageBox.Show(this, "This replaces the details, including your edits to them. Continue?", "LaunchHere",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        _settingDetails = true;
        DetailsBox.Text = ProblemReport.Details(cfg, _log, HideTitlesBox.IsChecked == true);
        _settingDetails = false;
        _detailsEdited = false;
        DetailsBox.ScrollToEnd();
    }

    void TurnOnVerbose()
    {
        _enableVerbose();
        VerboseButton.IsEnabled = false;
        VerboseTitle.Text = "Verbose logging is on";
        VerboseText.Text = "Now make the problem happen again, then come back and press Refresh. You can turn verbose logging off again in Settings > Troubleshooting.";
    }

    string? ReadTitle()
    {
        string title = TitleBox.Text.Trim();
        if (title.Length > 0) return title;
        MessageBox.Show(this, "Give the problem a short title first.", "LaunchHere", MessageBoxButton.OK, MessageBoxImage.Information);
        TitleBox.Focus();
        return null;
    }

    void OpenIssue()
    {
        if (ReadTitle() is not { } title) return;
        string body = ProblemReport.Body(WhatBox.Text, DetailsBox.Text);
        string url = ProblemReport.IssueUrl(title, body);
        if (url.Length > ProblemReport.MaxUrlLength)
        {
            // Too long for a URL: the description goes in the URL, the details go on the clipboard.
            string details = $"````text\n{DetailsBox.Text.Trim()}\n````\n";
            if (!SetClipboard(details)) return;
            url = ProblemReport.IssueUrl(title, ProblemReport.Body(WhatBox.Text, "")
                .Replace("````text\n\n````", "**Paste the details here: they are on your clipboard (Ctrl+V).**"));
            MessageBox.Show(this,
                "The details are too long to fill in automatically, so they were copied to your clipboard.\n\n" +
                "On the GitHub page, click at the end of the description and press Ctrl+V to paste them.",
                "LaunchHere", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            _log.Always("report a problem: opened GitHub issue page");
            StatusText.Text = "Opened GitHub in your browser. Check the issue there, then press Create.";
        }
        catch (Exception ex)
        {
            _log.Error($"report a problem: could not open the browser: {ex.Message}");
            MessageBox.Show(this, $"Could not open the browser:\n{ex.Message}\n\nUse Copy to clipboard and paste the report at\n{ProblemReport.NewIssueUrl}",
                "LaunchHere", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void Copy()
    {
        string title = TitleBox.Text.Trim();
        string text = (title.Length > 0 ? $"# {title}\n\n" : "") + ProblemReport.Body(WhatBox.Text, DetailsBox.Text);
        if (SetClipboard(text)) StatusText.Text = "Copied. Paste it into a new issue on GitHub, or wherever you need it.";
    }

    bool SetClipboard(string text)
    {
        // The clipboard can be briefly locked by another app.
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text.Replace("\r\n", "\n").Replace("\n", "\r\n")); return true; }
            catch (System.Runtime.InteropServices.COMException) { Thread.Sleep(50); }
        }
        MessageBox.Show(this, "Could not copy to the clipboard (another app is using it). Try again.", "LaunchHere",
            MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }
}
