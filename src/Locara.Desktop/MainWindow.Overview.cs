using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Locara.Core;

namespace Locara.Desktop;

public partial class MainWindow
{
    private enum NextAction { Source, Scan, Files, Templates, Issues, Drafts, Translate, ResumeTeam, ResumeBatch, Trial, Build }
    private NextAction nextAction;
    private SourceCapability? capability;
    private (string Path, string Scan)? capabilityKey;

    private int overviewRequest;
    private bool loadingOverview;
    private bool loadOverviewSections;

    private void NavigateWorkspace(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || openingProject || work != null || projectPath == null || e.Source != Tabs) return;
        if (e.RemovedItems.Contains(EditorPage) && !SaveEdits()) { Tabs.SelectedItem = EditorPage; return; }
        if (Overview.IsSelected || TranslationPage.IsSelected) UpdateOverview();
        if (FilesPage.IsSelected) RefreshScannedFiles();
    }

    private void SetCapabilities(SourceCapability result, string version)
    {
        if (projectPath == null) return;
        capabilityKey = (projectPath, version);
        capability = result;
        SetTrialVisibility(result.Launch != GameLaunch.None, result.Launch == GameLaunch.Steam, result.CanGenerateTemplates);
    }

    private void SetTrialVisibility(bool available, bool isSteam = false, bool canGenerateRenpy = false)
    {
        EditorTrial.Visibility = PackageTrial.Visibility = DashboardTrial.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        SteamRecoveryButton.Visibility = isSteam ? Visibility.Visible : Visibility.Collapsed;
        RenpyGenerateButton.Visibility = canGenerateRenpy ? Visibility.Visible : Visibility.Collapsed;
    }

    private sealed record OverviewData(ProjectInfo Info, string Version, string Engine, bool Scanned, bool Errors,
        IReadOnlyDictionary<string, int> Counts, string? Output, TeamPlan? Plan, TeamProgress? Progress,
        int NotesCount, string[]? Sections, bool ResumeBatch, List<ModelUsageEntry> Usage);

    private static OverviewData ReadOverview(string path, bool includeSections)
    {
        using var store = new ProjectStore(path);
        var svc = new ProjectService(store);
        svc.EnsureCatalogSelected();
        var info = store.Info;
        var version = store.Get("scan_version");
        var engine = store.Get("scanned_engine");
        var package = store.Get("scanned_package");
        bool scanned = info.Source.Length > 0 && info.Source == store.Get("scanned_source");
        bool errors = scanned && svc.HasBlockingScanErrors();
        var counts = store.ReviewCounts();
        string output = store.LastOutput;
        var team = new TranslationTeam(store);
        var plan = scanned ? team.Latest : null;
        if (plan != null && (plan.Package != package || plan.Engine != engine)) plan = null;
        var progress = plan == null ? null : team.Summary(plan);
        int notesCount = plan == null ? 0 : team.Notes(plan).Count;
        var sections = !includeSections || plan == null ? null
            : team.Sections(plan).Select(section => $"{section.Section}    {section.Completed} / {section.Total}").ToArray();
        var runner = new TranslationRunner(store);
        bool canResume = scanned && counts["pending"] > 0 && runner.LastJob is { } job && job.Package == package
            && runner.HasPending(job);
        return new(info, version, engine, scanned, errors, counts, Directory.Exists(output) ? output : null,
            plan, progress, notesCount, sections, canResume, store.ProjectUsage());
    }

    private async void UpdateOverview(bool includeSections = false)
    {
        overviewRequest++;
        loadOverviewSections = includeSections || SectionsPanel.IsExpanded;
        if (loadingOverview || projectPath == null) return;
        loadingOverview = true;
        try
        {
            while (projectPath is { } path && IsLoaded)
            {
                int request = overviewRequest;
                bool readSections = loadOverviewSections;
                try
                {
                    var data = await Task.Run(() => ReadOverview(path, readSections));
                    if (!IsLoaded) return;
                    if (request != overviewRequest || projectPath != path) continue;
                    ApplyOverview(path, data);
                }
                catch (Exception error)
                {
                    if (request != overviewRequest || projectPath != path) continue;
                    if (IsLoaded) Status.Text = error.Message;
                }
                return;
            }
        }
        finally { loadingOverview = false; }
    }

    private void ApplyOverview(string path, OverviewData data)
    {
        var key = (path, data.Version);
        bool loadCapability = capabilityKey != key;
        if (loadCapability) { capabilityKey = key; capability = null; }
        var counts = data.Counts;
        int total = counts["total"], approved = counts["approved"], issues = counts["issues"],
            drafts = counts["review"], pending = counts["pending"];
        DashboardName.Text = data.Info.Name;
        PendingCount.Content = $"بانتظار الترجمة  {pending:N0}";
        DraftCount.Content = $"للمراجعة  {drafts:N0}";
        IssueCount.Content = $"تحتاج انتباهاً  {issues:N0}";
        ApprovedCount.Content = $"معتمدة  {approved:N0}";

        double pct = total == 0 ? 0 : approved * 100.0 / total;
        ApprovalProgress.Value = pct;
        ApprovalPercent.Text = total == 0 ? "" : $"{approved:N0} / {total:N0} معتمدة ({pct:0.0}%)";

        DraftsActionPanel.Visibility = drafts > 0 ? Visibility.Visible : Visibility.Collapsed;
        DraftsActionTitle.Text = $"لديك {drafts:N0} مسودة بانتظار المراجعة والاعتماد";
        ApproveTeamDraftsButton.Visibility = drafts > 0 ? Visibility.Visible : Visibility.Collapsed;

        ScanSummary.Text = !data.Scanned ? "بانتظار فحص المصدر" : data.Errors ? "ظهرت أخطاء في الفحص. راجع تبويب الملفات."
            : $"اكتمل الفحص · {total:N0} نص";
        packagePath = data.Output;
        OpenLatest.IsEnabled = packagePath != null;
        DashboardPreview.IsEnabled = data.Scanned && !data.Errors && (drafts > 0 || approved > 0);
        OutputSummary.Text = packagePath == null ? "لم تُبنَ حزمة لهذا المصدر بعد." : "آخر حزمة محفوظة:  " + Path.GetFileName(packagePath);
        SectionsPanel.Visibility = data.Progress == null ? Visibility.Collapsed : Visibility.Visible;
        SectionList.ItemsSource = data.Sections ?? Array.Empty<string>();
        RefreshProviderSummary();
        OverviewTeam.Text = SavedTeamSummary.Text = data.Progress == null ? "" : FormatTeamStatus(data.Progress);
        ApplyProjectUsage(data.Usage);
        ScopeSummary.Text = $"نطاق المحرّر الحالي: {(Search.Text.Length == 0 ? "كل النصوص" : Search.Text)} · {((ComboBoxItem)ReviewFilter.SelectedItem).Content}";
        if (work == null) UpdateIdleMonitor(data.Progress, data.NotesCount, data.Plan);

        nextAction = data.Info.Source.Length == 0 ? NextAction.Source : !data.Scanned ? NextAction.Scan : data.Errors ? NextAction.Files
            : issues > 0 ? NextAction.Issues : data.Progress is { } progress && progress.Completed < progress.Total ? NextAction.ResumeTeam
            : pending > 0 ? NextAction.Translate : drafts > 0 ? NextAction.Drafts : data.ResumeBatch ? NextAction.ResumeBatch
            : approved > 0 ? NextAction.Build : NextAction.Files;
        Primary.IsEnabled = true;
        PresentCapability(data.Engine);
        if (loadCapability && data.Scanned) LoadCapabilities(key, ZipPassword.Password);
    }

    private async void LoadCapabilities((string Path, string Version) key, string password)
    {
        try
        {
            var token = recoveryLifetime.Token;
            var report = await Task.Run(() =>
            {
                using var store = new ProjectStore(key.Path);
                return new ProjectService(store).Capabilities(password, token);
            }, token);
            if (!IsLoaded || capabilityKey != key || projectPath != key.Path) return;
            capability = report;
            PresentCapability(report.Engine);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (IsLoaded && capabilityKey == key && projectPath == key.Path) DeploymentSummary.Text = error.Message;
        }
    }

    private void PresentCapability(string engine)
    {
        bool playable = capability?.Launch is GameLaunch.Steam or GameLaunch.GodotPack or GameLaunch.RenpyCopy;
        SetTrialVisibility(playable, capability?.Launch == GameLaunch.Steam, capability?.CanGenerateTemplates == true);
        EngineSummary.Text = capability != null ? $"{capability.Engine}  {capability.Version}".Trim()
            : engine.Length == 0 ? "المصدر بانتظار التعرّف" : engine;
        DeploymentSummary.Text = capability == null ? "" : capability.Deployment + (capability.Arabic.Length > 0 ? "\n" + capability.Arabic : "");
        if (nextAction == NextAction.Files && capability?.CanGenerateTemplates == true) nextAction = NextAction.Templates;
        if (nextAction == NextAction.Build && playable) nextAction = NextAction.Trial;
        Primary.Content = nextAction switch
        {
            NextAction.Source => "اختيار مصدر",
            NextAction.Scan => "فحص المصدر",
            NextAction.Files => "مراجعة الملفات",
            NextAction.Templates => "توليد قوالب العربية",
            NextAction.Issues => "راجع النصوص التي تحتاج انتباهاً",
            NextAction.Drafts => "مراجعة المسودات بالذكاء الاصطناعي",
            NextAction.Translate => "ابدأ تعريب النصوص المتبقية",
            NextAction.ResumeTeam => "استأنف التشغيل المحفوظ",
            NextAction.ResumeBatch => "استأنف الدفعة",
            NextAction.Trial => "جرّب داخل اللعبة",
            _ => "بناء حزمة نهائية"
        };
    }

    private async void PrimaryAction(object sender, RoutedEventArgs e)
    {
        switch (nextAction)
        {
            case NextAction.Scan: await ScanCurrent(); break;
            case NextAction.Templates: GenerateRenpy(sender, e); break;
            case NextAction.Issues: await OpenEditorFilter("issues"); break;
            case NextAction.Drafts: ReviewDraftsWithAi(sender, e); break;
            case NextAction.Translate:
                Tabs.SelectedItem = TranslationPage;
                if (!File.Exists(ProviderSettingsPath) && Model.Text.Length == 0 && ApiKey.Password.Length == 0)
                    ConnectionPanel.IsExpanded = true;
                else
                {
                    TranslationMode.SelectedValue = "all-pending";
                    await StartUnifiedTranslationCore();
                }
                break;
            case NextAction.ResumeTeam: ResumeTeamQuick(sender, e); break;
            case NextAction.ResumeBatch: ResumeTranslation(sender, e); break;
            case NextAction.Trial: TryGame(sender, e); break;
            case NextAction.Build: Tabs.SelectedItem = PackagesPage; await Build(true); break;
            default: Tabs.SelectedItem = FilesPage; break;
        }
    }

    private sealed record EditorView(string Search, string Filter, int Offset);
    private static string ViewKey(ProjectStore project) => "editor-view:" + project.Get("source_scope") + ":" + project.Get("scanned_package");

    private static EditorView ReadEditorView(ProjectStore project, string source)
    {
        var fallback = new EditorView(ProjectService.InitialSearch(source), "", 0);
        string saved = project.Get(ViewKey(project));
        if (saved.Length == 0) return fallback;
        try
        {
            var view = JsonSerializer.Deserialize<EditorView>(saved);
            if (view == null) return fallback;
            string search = view.Search ?? fallback.Search;
            string filter = view.Filter is "pending" or "translated" or "conflict" or "approved" or "review" or "issues" ? view.Filter : "";
            int pageOffset = Math.Max(0, view.Offset);
            if (pageOffset > 0 && project.Units(search, pageOffset, 1, filter).Count == 0) pageOffset = 0;
            return new(search, filter, pageOffset);
        }
        catch (JsonException) { return fallback; }
    }

    private void ApplyEditorView(EditorView view)
    {
        settingProject = true;
        try
        {
            Search.Text = view.Search;
            ReviewFilter.SelectedValue = view.Filter;
            offset = view.Offset;
        }
        finally { settingProject = false; }
    }

    private async Task<bool> OpenEditorFilter(string filter, string search = "")
    {
        if (!SaveEdits()) return false;
        ApplyEditorView(new(search, filter, 0));
        Tabs.SelectedItem = EditorPage;
        await RefreshRows(rememberView: true);
        if (Rows.IsEnabled && Rows.Items.Count > 0 && Tabs.SelectedItem == EditorPage) Rows.SelectedIndex = 0;
        return true;
    }

    private void ShowSections(object sender, RoutedEventArgs e) => UpdateOverview(includeSections: true);

    private async void OpenStatus(object sender, RoutedEventArgs e) => await OpenEditorFilter((string)((Button)sender).Tag);
    private void GoEditor(object sender, RoutedEventArgs e) => Tabs.SelectedItem = EditorPage;
    private void GoFiles(object sender, RoutedEventArgs e) => Tabs.SelectedItem = FilesPage;
    private void GoTranslation(object sender, RoutedEventArgs e) => Tabs.SelectedItem = TranslationPage;
    private async void PreviewFromProject(object sender, RoutedEventArgs e)
    {
        Tabs.SelectedItem = PackagesPage;
        await Build(false);
    }
    private void GoPackages(object sender, RoutedEventArgs e) => Tabs.SelectedItem = PackagesPage;
    private async void ClearScope(object sender, RoutedEventArgs e)
    {
        if (await OpenEditorFilter("") && Tabs.SelectedItem == EditorPage) Tabs.SelectedItem = TranslationPage;
    }
    private void SearchKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; SearchRows(sender, e); }
    }
    private async void RecentKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || RecentProjects.SelectedItem is not RecentProject recent) return;
        e.Handled = true;
        await SetProject(recent.Path);
    }
}
