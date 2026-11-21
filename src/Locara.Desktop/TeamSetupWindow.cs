using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Locara.Core;

namespace Locara.Desktop;

internal sealed class TeamSetupWindow : Window
{
    internal TeamPlan? Plan { get; private set; }

    internal TeamSetupWindow(TeamPlan initial, ProviderOptions options, Func<TeamSettings, Task<TeamPlan>> capture)
    {
        Title = "تشغيل فريق الترجمة";
        Width = 760;
        Height = 645;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;

        var layout = new DockPanel { Margin = new Thickness(20) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal };
        var start = new Button { Content = "ابدأ التعريب", IsDefault = true };
        actions.Children.Add(start);
        actions.Children.Add(new Button { Content = "إلغاء", IsCancel = true });
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);
        var content = new StackPanel();
        string endpoint = ApiProvider.ResolveEndpoint(options).Authority;
        var summary = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        content.Children.Add(summary);
        void UpdateSummary(TeamPlan plan) => summary.Text =
            $"{plan.Batches.Sum(batch => batch.Rows.Length)} نصاً · {plan.Batches.Select(batch => batch.Section).Distinct().Count()} أقسام · {plan.Workers} مترجم"
            + $"\n{options.Provider} · {options.Model} · {endpoint}"
            + $"\nسقف الفريق — {options.MaxRequests} طلباً / {options.MaxCharacters} حرفاً من مدخلات الطلبات"
            + "\nيراجع الفريق النتائج ويعيد تصحيح الملاحظات ضمن سقف التشغيل.";
        UpdateSummary(initial);

        var style = Field(content, "أسلوب المشروع", initial.Settings.Style, multiline: true);
        var advanced = new StackPanel { Margin = new Thickness(8) };
        var workers = new ComboBox { Margin = new Thickness(0, 4, 0, 4) };
        foreach (string name in new[] { "تلقائي", "مترجم واحد", "مترجمان", "3 مترجمين", "4 مترجمين" }) workers.Items.Add(name);
        workers.SelectedIndex = initial.Settings.Workers;
        advanced.Children.Add(new TextBlock { Text = "عدد المترجمين" });
        advanced.Children.Add(workers);
        var supervisor = Field(advanced, "تعليمات المشرف", initial.Settings.Supervisor, multiline: true);
        var translator = Field(advanced, "تعليمات المترجم", initial.Settings.Translator, multiline: true);
        var reviewer = Field(advanced, "تعليمات المراجع", initial.Settings.Reviewer, multiline: true);
        var supervisorModel = Field(advanced, "نموذج المشرف عند التخصيص", initial.Settings.SupervisorModel);
        var translatorModel = Field(advanced, "نموذج المترجم عند التخصيص", initial.Settings.TranslatorModel);
        var reviewerModel = Field(advanced, "نموذج المراجع عند التخصيص", initial.Settings.ReviewerModel);
        var reset = new Button { Content = "استعادة قوالب الأدوار", HorizontalAlignment = HorizontalAlignment.Right };
        reset.Click += (_, _) => { supervisor.Clear(); translator.Clear(); reviewer.Clear(); };
        advanced.Children.Add(reset);
        content.Children.Add(new Expander { Header = "الأدوار والنماذج", Content = advanced, Margin = new Thickness(0, 10, 0, 0) });
        var issue = new TextBlock { Foreground = Brushes.DarkRed, TextWrapping = TextWrapping.Wrap };
        content.Children.Add(issue);
        var preview = new Button { Content = "عرض تعليمات الأدوار", HorizontalAlignment = HorizontalAlignment.Right };
        content.Children.Add(preview);

        TeamSettings Settings() => new(style.Text.Trim(), supervisor.Text.Trim(), translator.Text.Trim(), reviewer.Text.Trim(),
            supervisorModel.Text.Trim(), translatorModel.Text.Trim(), reviewerModel.Text.Trim(), workers.SelectedIndex);
        int summaryRequest = 0;
        workers.SelectionChanged += async (_, _) =>
        {
            int request = ++summaryRequest;
            try
            {
                var plan = await capture(Settings());
                if (IsLoaded && request == summaryRequest) { UpdateSummary(plan); issue.Text = ""; }
            }
            catch (Exception error) { if (IsLoaded && request == summaryRequest) issue.Text = error.Message; }
        };
        preview.Click += (_, _) =>
        {
            string prompts = string.Join("\n\n", new[] { ("المشرف", "supervisor"), ("المترجم", "translator"), ("المراجع", "reviewer") }
                .Select(role => role.Item1 + "\n" + TranslationTeam.PromptPreview(role.Item2, Settings())));
            new Window
            {
                Owner = this, Title = "تعليمات النظام", Width = 715, Height = 525, FlowDirection = FlowDirection.RightToLeft,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBox { Text = prompts, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            }.ShowDialog();
        };
        start.Click += async (_, _) =>
        {
            summaryRequest++;
            start.IsEnabled = false;
            try
            {
                var plan = await capture(Settings());
                if (!IsLoaded) return;
                Plan = plan;
                DialogResult = true;
            }
            catch (Exception error) { if (IsLoaded) issue.Text = error.Message; }
            finally { if (IsLoaded) start.IsEnabled = true; }
        };
        layout.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = layout;
    }

    private static TextBox Field(Panel panel, string label, string text, bool multiline = false)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 5, 0, 0) });
        var field = new TextBox { Text = text, AcceptsReturn = multiline, TextWrapping = TextWrapping.Wrap,
            MinHeight = multiline ? 65 : 27, VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled };
        panel.Children.Add(field);
        return field;
    }
}
