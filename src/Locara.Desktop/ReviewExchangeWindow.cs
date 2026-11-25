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
        Title = notes ? "ملاحظات التدقيق" : "مراجعة تغييرات CSV";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        Width = 1080;
        Height = 620;
        MinWidth = 680;
        ThemeManager.AttachWindow(this);

        var layout = new DockPanel { Margin = new Thickness(20, 18, 20, 18) };
        int blocked = changes.Count(change => change.Issue.Length > 0);
        var summaryCard = new Border
        {
            Style = (Style)Application.Current.FindResource("SubtleCardBorderStyle"),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = notes
                    ? $"ملاحظات البوت المدقق — {changes.Count:N0} سطر (اختر سطراً لفتحه في المحرّر)"
                    : $"تغييرات قابلة للتطبيق: {changes.Count - blocked:N0}   ·   تحتاج إلى مراجعة: {blocked:N0}",
                FontWeight = FontWeights.SemiBold
            }
        };
        DockPanel.SetDock(summaryCard, Dock.Top);
        layout.Children.Add(summaryCard);

        var rows = new DataGrid { AutoGenerateColumns = false, CanUserAddRows = false, RowHeight = 40, ItemsSource = changes };
        var apply = new Button
        {
            Content = notes ? "فتح السطر في المحرّر" : "حفظ المحدد كمسودات",
            IsEnabled = !notes && blocked < changes.Count,
            Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
        };
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
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(apply);
        actions.Children.Add(new Button { Content = "إلغاء", IsCancel = true });
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);

        if (!notes) rows.Columns.Add(new DataGridCheckBoxColumn { Header = "تطبيق", Binding = new Binding(nameof(ReviewChange.Include)), Width = 62 });
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
