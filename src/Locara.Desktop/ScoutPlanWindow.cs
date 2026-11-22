using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Locara.Core;

namespace Locara.Desktop;

internal sealed class ScoutPlanWindow : Window
{
    private sealed class ScoutRow : INotifyPropertyChanged
    {
        private bool selected = true;
        public event PropertyChangedEventHandler? PropertyChanged;

        public string ClusterId { get; }
        public string GroupSummary { get; }
        public string ActionLabel { get; }
        public Brush ActionBrush { get; }
        public string FieldsSummary { get; }
        public string ConfidenceLabel { get; }
        public string Reason { get; }

        public bool Selected
        {
            get => selected;
            set
            {
                if (selected == value) return;
                selected = value;
                PropertyChanged?.Invoke(this, new(nameof(Selected)));
            }
        }

        public ScoutRow(ScoutClusterRecommendation rec, FileSchemaCluster cluster)
        {
            ClusterId = rec.ClusterId;
            GroupSummary = $"{cluster.DirectoryGroup} [{cluster.Format}] · {cluster.FileCount:N0} ملف ({cluster.TotalUnits:N0} نص)";
            (ActionLabel, ActionBrush) = rec.Action switch
            {
                "include_all" => ("تضمين الكل", new SolidColorBrush(Color.FromRgb(0x14, 0x63, 0x56))),
                "include_fields" => ("حقول محددة فقط", new SolidColorBrush(Color.FromRgb(0x1B, 0x4F, 0x72))),
                _ => ("استبعاد", new SolidColorBrush(Color.FromRgb(0xA9, 0x32, 0x26)))
            };
            FieldsSummary = rec.FieldPatterns.Count == 0 ? "—" : string.Join("، ", rec.FieldPatterns);
            ConfidenceLabel = rec.Confidence switch
            {
                "high" => "عالية",
                "low" => "منخفضة",
                _ => "متوسطة"
            };
            Reason = rec.Reason;
        }
    }

    private readonly List<ScoutRow> rows;
    public HashSet<string> SelectedClusterIds =>
        rows.Where(r => r.Selected).Select(r => r.ClusterId).ToHashSet(StringComparer.Ordinal);

    internal ScoutPlanWindow(ScoutPlan plan)
    {
        Title = "خطة كشّاف الذكاء الاصطناعي (AI Localization Scout)";
        Width = 960;
        Height = 560;
        MinWidth = 680;
        MinHeight = 380;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var byId = plan.AnalyzedClusters.ToDictionary(c => c.ClusterId, StringComparer.Ordinal);
        rows = plan.Recommendations
            .Where(r => byId.ContainsKey(r.ClusterId))
            .Select(r => new ScoutRow(r, byId[r.ClusterId]))
            .ToList();

        var layout = new DockPanel { Margin = new Thickness(18) };

        int totalFiles = plan.Recommendations.Where(r => byId.ContainsKey(r.ClusterId)).Sum(r => byId[r.ClusterId].FileCount);
        string tokenNote = plan.Tokens.TotalTokens > 0
            ? $" · استهلاك التحليل: {plan.Tokens.TotalTokens:N0} توكن ({plan.Provider} / {plan.Model})"
            : $" · المزوّد: {plan.Provider} / {plan.Model}";
        var header = new TextBlock
        {
            Text = $"راجع توصيات كشّاف الذكاء الاصطناعي لـ {rows.Count:N0} مجموعة هيكلية ({totalFiles:N0} ملف){tokenNote}\nلن تُطبّق أي قاعدة أو استبعاد إلا على الصفوف المحددة أدناه.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
            FontSize = 13
        };
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var apply = new Button
        {
            Content = "تطبيق التوصيات المحددة",
            IsDefault = true,
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x64, 0x5E)),
            Foreground = Brushes.White,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            IsEnabled = rows.Count > 0
        };
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(apply);

        var selectAll = new Button { Content = "تحديد الكل", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        selectAll.Click += (_, _) => { foreach (var r in rows) r.Selected = true; };
        buttons.Children.Add(selectAll);

        var clearAll = new Button { Content = "إلغاء الكل", Padding = new Thickness(10, 6, 10, 6), Margin = new Thickness(0, 0, 8, 0) };
        clearAll.Click += (_, _) => { foreach (var r in rows) r.Selected = false; };
        buttons.Children.Add(clearAll);

        buttons.Children.Add(new Button { Content = "إلغاء", IsCancel = true, Padding = new Thickness(12, 6, 12, 6) });
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            RowHeight = 40,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xD2, 0xDF, 0xDC)),
            FontSize = 12,
            ItemsSource = rows
        };

        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "تطبيق",
            Binding = new Binding(nameof(ScoutRow.Selected)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 58
        });

        var ltrStyle = new Style(typeof(TextBlock));
        ltrStyle.Setters.Add(new Setter(FrameworkElement.FlowDirectionProperty, FlowDirection.LeftToRight));
        ltrStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        ltrStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        ltrStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0)));

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "المجموعة الهيكلية",
            Binding = new Binding(nameof(ScoutRow.GroupSummary)),
            IsReadOnly = true,
            Width = new DataGridLength(1.4, DataGridLengthUnitType.Star),
            ElementStyle = ltrStyle
        });

        var actionStyle = new Style(typeof(TextBlock));
        actionStyle.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(nameof(ScoutRow.ActionBrush))));
        actionStyle.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        actionStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        actionStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0)));
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "التوصية",
            Binding = new Binding(nameof(ScoutRow.ActionLabel)),
            IsReadOnly = true,
            Width = 115,
            ElementStyle = actionStyle
        });

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الحقول المختارة",
            Binding = new Binding(nameof(ScoutRow.FieldsSummary)),
            IsReadOnly = true,
            Width = new DataGridLength(1.1, DataGridLengthUnitType.Star),
            ElementStyle = ltrStyle
        });

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الثقة",
            Binding = new Binding(nameof(ScoutRow.ConfidenceLabel)),
            IsReadOnly = true,
            Width = 65
        });

        var reasonStyle = new Style(typeof(TextBlock));
        reasonStyle.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        reasonStyle.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center));
        reasonStyle.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(6, 0, 6, 0)));
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "السبب",
            Binding = new Binding(nameof(ScoutRow.Reason)),
            IsReadOnly = true,
            Width = new DataGridLength(1.6, DataGridLengthUnitType.Star),
            ElementStyle = reasonStyle
        });

        layout.Children.Add(grid);
        Content = layout;
    }
}
