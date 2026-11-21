using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Locara.Core;

namespace Locara.Desktop;

public partial class ExtractionWindow : Window
{
    private readonly Func<string, CancellationToken, ExtractionPreview> inspect;
    private CancellationTokenSource? request;
    private ExtractionPreview? preview;

    public string Resource => (string)ResourceChoices.SelectedItem;
    public ExtractionRule? Rule { get; private set; }

    public ExtractionWindow(IReadOnlyList<string> resources, Func<string, CancellationToken, ExtractionPreview> inspect)
    {
        InitializeComponent();
        this.inspect = inspect;
        ResourceChoices.ItemsSource = resources;
        ResourceChoices.SelectedIndex = 0;
        Closed += (_, _) => request?.Cancel();
    }

    private async void LoadResource(object sender, SelectionChangedEventArgs e)
    {
        if (ResourceChoices.SelectedItem is not string resource) return;
        request?.Cancel();
        using var current = new CancellationTokenSource();
        request = current;
        preview = null;
        SaveRule.IsEnabled = ResetRule.IsEnabled = false;
        TableControls.Visibility = Sample.Visibility = Fields.Visibility = Visibility.Collapsed;
        Feedback.Text = "جارٍ قراءة المعاينة…";
        try
        {
            var result = await Task.Run(() => inspect(resource, current.Token), current.Token);
            if (current.IsCancellationRequested) return;
            preview = result;
            ResetRule.IsEnabled = true;
            if (result.Tables.Count > 0)
            {
                TableControls.Visibility = Sample.Visibility = Visibility.Visible;
                Sheets.ItemsSource = result.Tables;
                Sheets.DisplayMemberPath = nameof(TablePreview.Sheet);
                Sheets.IsEnabled = result.Tables.Count > 1;
                Sheets.SelectedItem = result.Tables.FirstOrDefault(table => table.Sheet == result.Rule?.Sheet) ?? result.Tables[0];
            }
            else
            {
                Fields.Visibility = Visibility.Visible;
                Fields.ItemsSource = result.Fields;
                var selected = result.Rule?.Fields?.ToHashSet(StringComparer.Ordinal);
                foreach (var field in result.Fields)
                    if (selected == null || selected.Contains(field.Locator)) Fields.SelectedItems.Add(field);
                Feedback.Text = "حدّد الحقول التي تريد استخراجها. استخدم Ctrl لاختيار عدة حقول.";
                SaveRule.IsEnabled = result.Fields.Count > 0;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!current.IsCancellationRequested) Feedback.Text = error.Message; }
        finally { if (request == current) request = null; }
    }

    private void ChangeSheet(object sender, SelectionChangedEventArgs e)
    {
        if (Sheets.SelectedItem is not TablePreview table) return;
        var headers = table.Headers.Where(header => !string.IsNullOrWhiteSpace(header)).Distinct().ToList();
        var rule = preview?.Rule;
        KeyColumn.ItemsSource = new[] { "" }.Concat(headers).ToList();
        KeyColumn.SelectedItem = rule?.Key ?? headers.FirstOrDefault(header => header.ToLowerInvariant() is "id" or "key" or "string_id") ?? "";
        SourceColumn.ItemsSource = headers;
        SourceColumn.SelectedItem = rule?.Source ?? headers.FirstOrDefault(header => header.ToLowerInvariant() is "en" or "english" or "source" or "text" or "source_text");
        TargetColumn.ItemsSource = headers;
        TargetColumn.Text = rule?.Target ?? headers.FirstOrDefault(header => header.ToLowerInvariant() is "ar" or "arabic" or "target") ?? "target";
        Sample.Columns.Clear();
        int width = Math.Max(table.Headers.Count, table.Rows.Select(row => row.Length).DefaultIfEmpty(0).Max());
        for (int i = 0; i < width; i++)
        {
            string header = i < table.Headers.Count ? table.Headers[i] : "";
            Sample.Columns.Add(new DataGridTextColumn
            {
                Header = header.Length == 0 ? $"عمود {i + 1} بلا عنوان" : header,
                Binding = new Binding($"[{i}]")
            });
        }
        Sample.ItemsSource = table.Rows.Select(row => row.Length == width
            ? row : row.Concat(Enumerable.Repeat("", width - row.Length)).ToArray()).ToList();
        Feedback.Text = table.RowsWiderThanHeader is { Count: > 0 } wider
            ? $"توجد قيم في أعمدة بلا عنوان، منها الصفوف {string.Join("، ", wider)}. سيُضاف العمود الجديد بعد آخر عمود مستخدم."
            : "";
        SaveRule.IsEnabled = headers.Count > 0;
    }

    private void Save(object sender, RoutedEventArgs e)
    {
        if (preview?.Tables.Count > 0)
        {
            if (Sheets.SelectedItem is not TablePreview table || SourceColumn.SelectedItem is not string source
                || string.IsNullOrWhiteSpace(TargetColumn.Text))
            {
                Feedback.Text = "اختر عمود المصدر واكتب اسم عمود الترجمة.";
                return;
            }
            string key = (string?)KeyColumn.SelectedItem ?? "";
            string target = TargetColumn.Text.Trim();
            if (source == target || source == key || key == target)
            {
                Feedback.Text = "اختر أسماء مختلفة لأعمدة المعرّف والمصدر والترجمة.";
                return;
            }
            if (new[] { source, key, target }.Any(name => name.Length > 0 && table.Headers.Count(header => header == name) > 1))
            {
                Feedback.Text = "توجد عناوين أعمدة مكررة. اختر عنواناً فريداً لكل عمود.";
                return;
            }
            Rule = new(key, source, target, table.Sheet);
        }
        else Rule = new(Fields: Fields.SelectedItems.Cast<TextSlot>().Select(field => field.Locator).ToArray());
        DialogResult = true;
    }

    private void Reset(object sender, RoutedEventArgs e)
    {
        Rule = null;
        DialogResult = true;
    }
}
