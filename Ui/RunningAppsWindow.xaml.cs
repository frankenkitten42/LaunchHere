using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LaunchHere;

public sealed class RunningAppItem : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Title { get; init; }
    public ImageSource? Icon { get; init; }

    bool _picked;
    public bool Picked
    {
        get => _picked;
        set { _picked = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Picked))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Picker listing the apps that have a window open right now.</summary>
public partial class RunningAppsWindow : Window
{
    readonly List<RunningAppItem> _items;

    public IReadOnlyList<RunningAppItem> Selected => _items.Where(i => i.Picked).ToList();

    public RunningAppsWindow(IEnumerable<string> existing)
    {
        Wpf.EnsureInitialized();
        InitializeComponent();
        Wpf.EnableKeyboard(this);
        Icon = Wpf.ToImageSource(TrayContext.MakeIcon(true));

        var have = new HashSet<string>(existing.Select(e => System.IO.Path.GetFileName(e)), StringComparer.OrdinalIgnoreCase);
        _items = RunningApps.List()
            .Where(a => !have.Contains(a.Name))
            .Select(a => new RunningAppItem { Name = a.Name, Path = a.Path, Title = a.Title, Icon = Wpf.IconOf(a.Path) })
            .ToList();
        foreach (var i in _items) i.PropertyChanged += (_, _) => AddButton.IsEnabled = _items.Any(x => x.Picked);
        AppsList.ItemsSource = _items;
        if (_items.Count == 0) HintText.Text = "No other apps with open windows found.";

        // Click or Space anywhere on a row ticks it.
        AppsList.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && ItemsControlFrom(d) is RunningAppItem item) item.Picked = !item.Picked;
        };
        AppsList.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Space && AppsList.SelectedItem is RunningAppItem item) { item.Picked = !item.Picked; e.Handled = true; }
        };
        AddButton.Click += (_, _) => DialogResult = true;
    }

    object? ItemsControlFrom(DependencyObject d)
    {
        while (d != null && d is not System.Windows.Controls.ListViewItem)
            d = VisualTreeHelper.GetParent(d);
        return (d as System.Windows.Controls.ListViewItem)?.DataContext;
    }
}
