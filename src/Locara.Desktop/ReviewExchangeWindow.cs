using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Locara.Core;

namespace Locara.Desktop;

internal sealed class ReviewExchangeWindow : Window
{
    internal int SelectedIndex { get; private set; } = -1;

    internal ReviewExchangeWindow(IReadOnlyList<ReviewChange> changes, bool notes = false)
    {
        Title = "مراجعة تغييرات CSV";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        Width = 1080;
        Height = 620;
        MinWidth = 680;
        var layout = new DockPanel { Margin = new Thickness(16) };
        int blocked = changes.Count(change => change.Issue.Length > 0);
        var summary = new TextBlock { Text = notes ? $"ملاحظات الفريق — {changes.Count}" : $"تغييرات قابلة للتطبيق — {changes.Count - blocked}    تحتاج إلى مراجعة — {blocked}" };
        DockPanel.SetDock(summary, Dock.Top);
        layout.Children.Add(summary);
        var rows = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, ItemsSource = changes };
        var apply = new Button { Content = notes ? "فتح النص في المحرّر" : "حفظ المحدد كمسودات", IsEnabled = !notes && blocked < changes.Count };
        if (notes)
        {
            rows.SelectionMode = DataGridSelectionMode.Single;
            rows.CanUserSortColumns = false;
            rows.SelectionChanged += (_, _) => apply.IsEnabled = rows.SelectedIndex >= 0;
        }
        apply.Click += (_, _) =>
        {
            rows.CommitEdit(DataGridEditingUnit.Cell, true);
            rows.CommitEdit(DataGridEditingUnit.Row, true);
            SelectedIndex = rows.SelectedIndex;
            DialogResult = true;
        };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        actions.Children.Add(apply);
        actions.Children.Add(new Button { Content = "إلغاء", IsCancel = true });
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        if (!notes) rows.Columns.Add(new DataGridCheckBoxColumn { Header = "تطبيق", Binding = new Binding(nameof(ReviewChange.Include)) });
        foreach (var (title, property) in new[]
        {
            ("الأصل", nameof(ReviewChange.Source)), ("المسودة الحالية", nameof(ReviewChange.Current)),
            ("المقترح", nameof(ReviewChange.Proposed)), ("التنبيه", nameof(ReviewChange.Issue))
        })
            rows.Columns.Add(new DataGridTextColumn
            {
                Header = title, Binding = new Binding(property), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
        layout.Children.Add(rows);
        Content = layout;
    }
}
