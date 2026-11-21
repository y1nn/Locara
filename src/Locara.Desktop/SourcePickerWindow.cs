using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Locara.Desktop;

internal sealed class SourcePickerWindow : Window
{
    internal object? Selected => items.SelectedItem;
    private readonly ListBox items = new();

    internal SourcePickerWindow(string title, System.Collections.IEnumerable choices, string displayProperty, object? initial = null)
    {
        Title = title;
        Width = 780;
        Height = 510;
        MinWidth = 470;
        MinHeight = 310;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new DockPanel { Margin = new Thickness(18) };
        var search = new TextBox { ToolTip = "بحث" };
        System.Windows.Automation.AutomationProperties.SetName(search, "بحث");
        DockPanel.SetDock(search, Dock.Top);
        layout.Children.Add(search);

        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var confirm = new Button { Content = "اختيار المحدد", IsDefault = true, IsEnabled = false };
        confirm.Click += (_, _) => DialogResult = true;
        actions.Children.Add(confirm);
        actions.Children.Add(new Button { Content = "إلغاء", IsCancel = true });
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);

        items.DisplayMemberPath = displayProperty;
        items.ItemsSource = choices;
        items.SelectionChanged += (_, _) => confirm.IsEnabled = items.SelectedItem != null;
        items.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(items, source) is ListBoxItem)
                DialogResult = true;
        };
        var view = CollectionViewSource.GetDefaultView(choices);
        search.TextChanged += (_, _) =>
        {
            string query = search.Text.Trim();
            view.Filter = query.Length == 0 ? null : item => DisplayText(item).Contains(query, StringComparison.CurrentCultureIgnoreCase);
            if (items.SelectedIndex < 0 && items.Items.Count == 1) items.SelectedIndex = 0;
        };
        items.SelectedItem = initial;
        layout.Children.Add(items);
        Content = layout;
        Loaded += (_, _) => search.Focus();
    }

    private static string DisplayText(object item) => item switch
    {
        Core.SteamGame game => game.Display,
        Core.LanguageSource source => source.Display,
        _ => item.ToString() ?? ""
    };
}
