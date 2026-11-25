using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Locara.Core;

namespace Locara.Desktop;

public partial class MainWindow
{
    private sealed record LiveSampleItem(string Section, string Source, string Target);

    private sealed record BotTermRow(string Source, string Target, string Note)
    {
        public string OriginLabel => Note == "lore-bot" ? "[بوت الكشّاف]" : "[بوت المشهد]";
    }

    private sealed record TokenChipItem(string Token, string Label, Brush StatusBrush);

    private sealed record NeighborRowItem(string Source, string TargetPreview, Brush RowBg, Brush RowFg);

    private readonly ObservableCollection<LiveSampleItem> liveSamples = [];
    private readonly ObservableCollection<BotTermRow> botTermRows = [];
    private bool updatingInspector;
    private bool syncingEditorChips;

    private Brush ResBrush(string key) =>
        (Application.Current?.Resources[key] as Brush) ?? Brushes.Gray;

    private void InitializeStudioShell()
    {
        ThemeManager.AttachWindow(this);
        LiveTranslationFeed.ItemsSource = liveSamples;
        BotTermsList.ItemsSource = botTermRows;
        UpdateThemeToggleVisual();
        SyncSettingsProjectScope();
        UpdateBudgetCapBadge();
    }

    private void UpdateThemeToggleVisual()
    {
        if (ThemeToggleButton == null) return;
        ThemeToggleButton.Content = ThemeManager.IsDark ? "☀ فاتح" : "☾ داكن";
        ThemeToggleButton.ToolTip = ThemeManager.IsDark ? "التبديل إلى المظهر الفاتح" : "التبديل إلى المظهر الداكن";
    }

    private void ToggleThemeClick(object sender, RoutedEventArgs e)
    {
        ThemeManager.ToggleTheme();
        UpdateThemeToggleVisual();
        RefreshScannedFiles();
        RefreshDirectInstallStatus();
        if (Rows.SelectedItem is Unit selected)
            PopulateEditorInspector(selected);
    }

    private void NavLibraryClick(object sender, RoutedEventArgs e) => SelectStage(LibraryPage);
    private void NavFilesClick(object sender, RoutedEventArgs e) => SelectStage(FilesPage);
    private void NavTranslationClick(object sender, RoutedEventArgs e) => SelectStage(TranslationPage);
    private void NavEditorClick(object sender, RoutedEventArgs e) => SelectStage(EditorPage);
    private void NavPackagesClick(object sender, RoutedEventArgs e) => SelectStage(PackagesPage);
    private void NavSettingsClick(object sender, RoutedEventArgs e) => SelectStage(SettingsPage);

    private void SelectStage(TabItem page)
    {
        if (!page.IsEnabled)
        {
            SyncNavRailSelection();
            return;
        }
        Tabs.SelectedItem = page;
        SyncNavRailSelection();
    }

    private void SyncNavRailSelection()
    {
        if (!IsLoaded) return;
        var current = Tabs.SelectedItem as TabItem;
        NavLibrary.IsChecked = current == LibraryPage;
        NavFiles.IsChecked = current == FilesPage;
        NavTranslation.IsChecked = current == TranslationPage;
        NavEditor.IsChecked = current == EditorPage;
        NavPackages.IsChecked = current == PackagesPage;
        NavSettings.IsChecked = current == SettingsPage;

        bool showHeader = projectPath != null && current != LibraryPage;
        ProjectCommandBar.Visibility = showHeader ? Visibility.Visible : Visibility.Collapsed;
        SyncSettingsProjectScope();
        UpdateHeaderPrimaryVisibility();
    }

    private void UpdateHeaderPrimaryVisibility()
    {
        if (Primary == null) return;
        if (projectPath == null || Tabs.SelectedItem == LibraryPage)
        {
            Primary.Visibility = Visibility.Collapsed;
            return;
        }

        var current = Tabs.SelectedItem as TabItem;
        bool ownedByCurrentStage =
            (current == FilesPage && nextAction is NextAction.Scan or NextAction.Files or NextAction.Templates)
            || (current == TranslationPage && nextAction is NextAction.Translate or NextAction.ResumeTeam or NextAction.Drafts)
            || (current == EditorPage && nextAction is NextAction.Issues)
            || (current == PackagesPage && nextAction is NextAction.Deploy or NextAction.Trial);

        Primary.Visibility = ownedByCurrentStage ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SyncSettingsProjectScope()
    {
        bool hasProject = projectPath != null;
        if (RoutingNoProjectNotice != null)
            RoutingNoProjectNotice.Visibility = hasProject ? Visibility.Collapsed : Visibility.Visible;
        if (ProjectRoutingAndStyleCard != null)
            ProjectRoutingAndStyleCard.IsEnabled = hasProject && work == null;
        if (EstimateResumeBtn != null) EstimateResumeBtn.IsEnabled = hasProject && work == null;
        if (EstimateScopeBtn != null) EstimateScopeBtn.IsEnabled = hasProject && work == null;
        if (ResumeSingleBatchBtn != null) ResumeSingleBatchBtn.IsEnabled = hasProject && work == null;
    }

    private void OpenConnectionSettings()
    {
        SelectStage(SettingsPage);
        SettingsTabApi.IsChecked = true;
    }

    private void OpenRoutingSettings()
    {
        SelectStage(SettingsPage);
        SettingsTabRouting.IsChecked = true;
    }

    private void OpenUsageSettings()
    {
        SelectStage(SettingsPage);
        SettingsTabUsage.IsChecked = true;
    }

    private void SettingsSubTabChanged(object sender, RoutedEventArgs e)
    {
        if (ConnectionPanel == null || RoutingPanel == null || UsageCostPanel == null) return;
        ConnectionPanel.Visibility = SettingsTabApi.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RoutingPanel.Visibility = SettingsTabRouting.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        UsageCostPanel.Visibility = SettingsTabUsage.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BotSubTabChanged(object sender, RoutedEventArgs e)
    {
        if (BotLiveSubPanel == null || BotLoreSubPanel == null || BotSectionsSubPanel == null) return;
        BotLiveSubPanel.Visibility = BotTabLive.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BotLoreSubPanel.Visibility = BotTabLore.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        BotSectionsSubPanel.Visibility = BotTabSections.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (BotTabLore.IsChecked == true && work == null)
            _ = ReloadLoreAndGlossariesAsync();
        else if (BotTabSections.IsChecked == true)
            UpdateOverview(includeSections: true);
    }

    private void OpenLoreSubTabClick(object sender, MouseButtonEventArgs e)
    {
        BotTabLore.IsChecked = true;
    }

    private async Task ReloadLoreAndGlossariesAsync()
    {
        if (projectPath is not { } path) return;
        try
        {
            var data = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                var team = new TranslationTeam(store);
                string brief = team.Latest is { } plan ? team.WorldBrief(plan) : "";
                var botEntries = store.BotGlossaryEntries();
                var manual = store.Glossary().OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
                return (Brief: brief, BotEntries: botEntries, Manual: manual);
            });
            if (!IsLoaded || projectPath != path) return;

            ApplyLoreAndGlossaryData(data.Brief, data.BotEntries, data.Manual);
        }
        catch (Exception error)
        {
            if (IsLoaded && projectPath == path) Status.Text = error.Message;
        }
    }

    private void ApplyLoreAndGlossaryData(string brief, IReadOnlyList<BotGlossaryEntry> botEntries, List<KeyValuePair<string, string>> manual)
    {
        WorldBriefText.Text = string.IsNullOrWhiteSpace(brief)
            ? "تُبنى بطاقة عالم اللعبة تلقائياً عند بدء تشغيل البوتات لتوحيد النبرة وأسماء الشخصيات عبر كل المشاهد."
            : brief;

        botTermRows.Clear();
        foreach (var entry in botEntries)
            botTermRows.Add(new BotTermRow(entry.Source, entry.Target, entry.Note));

        BotTermsEmptyText.Visibility = botTermRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearBotTermsBtn.IsEnabled = botTermRows.Count > 0 && work == null;
        Terms.ItemsSource = manual;

        if (LoreBotBadge != null && work == null)
        {
            bool hasLore = !string.IsNullOrWhiteSpace(brief) || botTermRows.Count > 0;
            LoreBotBadge.Text = hasLore ? $"جاهز ({botTermRows.Count:N0} مصطلح)" : "في الانتظار";
            LoreBotBadge.Foreground = ResBrush(hasLore ? "SuccessFg" : "TextMuted");
            if (hasLore && LoreBotDetail != null)
            {
                LoreBotDetail.Text = string.IsNullOrWhiteSpace(brief)
                    ? $"اكتشف البوت {botTermRows.Count:N0} مصطلحاً — انقر للمراجعة أو التثبيت"
                    : brief;
            }
        }
    }

    private async void PromoteBotTermClick(object sender, RoutedEventArgs e)
    {
        if (work != null || projectPath is not { } path || sender is not Button { Tag: string termSource }) return;
        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                store.PromoteBotTerm(termSource);
            });
            Status.Text = $"ثُبّت المصطلح «{termSource}» في معجمك اليدوي المعتمد.";
            await ReloadLoreAndGlossariesAsync();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void RemoveBotTermClick(object sender, RoutedEventArgs e)
    {
        if (work != null || projectPath is not { } path || sender is not Button { Tag: string termSource }) return;
        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                store.RemoveBotTerm(termSource);
            });
            Status.Text = $"حُذف المصطلح التلقائي «{termSource}».";
            await ReloadLoreAndGlossariesAsync();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void ClearBotGlossaryClick(object sender, RoutedEventArgs e)
    {
        if (work != null || projectPath is not { } path || botTermRows.Count == 0) return;
        if (MessageBox.Show(this, "سيتم مسح المصطلحات المكتشفة تلقائياً فقط دون المساس بمعجمك اليدوي المعتمد. هل تريد المتابعة؟",
            "مسح المصطلحات التلقائية", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                store.ClearBotGlossary();
            });
            Status.Text = "مُسحت المصطلحات التلقائية.";
            await ReloadLoreAndGlossariesAsync();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void AppendLiveSample(string section, string? sampleSource, string? sampleTarget)
    {
        if (string.IsNullOrWhiteSpace(sampleSource) || string.IsNullOrWhiteSpace(sampleTarget)) return;
        string cleanSection = Path.GetFileName(section);
        if (string.IsNullOrWhiteSpace(cleanSection)) cleanSection = section;
        liveSamples.Insert(0, new LiveSampleItem(cleanSection, sampleSource.Trim(), sampleTarget.Trim()));
        while (liveSamples.Count > 24)
            liveSamples.RemoveAt(liveSamples.Count - 1);
        LiveSamplesEmptyText.Visibility = Visibility.Collapsed;
    }

    private void UnlimitedBudgetChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        UpdateBudgetCapBadge();
        PersistProfilesFile();
    }

    private void SaveBudgetSettingsClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _ = ReadProvider(useSavedKey: true);
        }
        catch (ArgumentException error)
        {
            if (UnlimitedBudget.IsChecked != true)
            {
                Status.Text = error.Message;
                return;
            }
        }
        PersistProfilesFile();
        UpdateBudgetCapBadge();
        Status.Text = UnlimitedBudget.IsChecked == true
            ? "حُفظ إعداد الميزانية: بلا سقف."
            : $"حُفظ سقف الميزانية ({CharacterBudget.Text} حرف / {RequestBudget.Text} طلب).";
    }

    private void UpdateBudgetCapBadge()
    {
        if (BudgetCapBadge == null || UnlimitedBudget == null) return;
        bool capped = UnlimitedBudget.IsChecked != true;
        BudgetCapBadge.Visibility = capped ? Visibility.Visible : Visibility.Collapsed;
        if (capped)
            BudgetCapBadge.Text = $"سقف نشط: {CharacterBudget.Text} حرف · {RequestBudget.Text} طلب";
        if (CharacterBudget != null) CharacterBudget.IsEnabled = capped;
        if (RequestBudget != null) RequestBudget.IsEnabled = capped;
    }

    // ==================== SPLIT STUDIO EDITOR INSPECTOR ====================

    private void EditorFilterChipClick(object sender, RoutedEventArgs e)
    {
        if (syncingEditorChips || sender is not RadioButton { Tag: string filterTag }) return;
        ReviewFilter.SelectedValue = filterTag;
    }

    private void SyncEditorFilterChips(string filter)
    {
        if (EditorChipAll == null) return;
        syncingEditorChips = true;
        try
        {
            EditorChipAll.IsChecked = filter == "";
            EditorChipPending.IsChecked = filter == "pending";
            EditorChipReview.IsChecked = filter is "review" or "translated";
            EditorChipIssues.IsChecked = filter is "issues" or "conflict";
            EditorChipApproved.IsChecked = filter == "approved";
        }
        finally { syncingEditorChips = false; }
    }

    private void SelectEditorRow(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        PopulateEditorInspector(Rows.SelectedItem as Unit);
    }

    private void PopulateEditorInspector(Unit? row)
    {
        if (InspectorSourceText == null) return;
        updatingInspector = true;
        try
        {
            if (row == null)
            {
                InspectorMetaText.Text = "اختر سطراً من الجدول لعرض سياقه وتحريره.";
                InspectorSourceText.Text = "";
                InspectorTargetBox.Text = "";
                InspectorTargetBox.IsEnabled = false;
                InspectorLengthText.Text = "";
                InspectorTokensContainer.Visibility = Visibility.Collapsed;
                InspectorWarningText.Visibility = Visibility.Collapsed;
                InspectorNeighborsList.ItemsSource = null;
                return;
            }

            InspectorTargetBox.IsEnabled = work == null;
            string contextPart = string.IsNullOrWhiteSpace(row.Context) ? "" : $" · {row.Context}";
            InspectorMetaText.Text = $"{row.Path} · {row.Locator}{contextPart}";
            InspectorSourceText.Text = row.Source;
            InspectorTargetBox.Text = row.Target;
            UpdateInspectorTokenAndLengthState(row.Source, row.Target, row.Warning);

            var pageUnits = Rows.Items.OfType<Unit>().ToList();
            int idx = pageUnits.IndexOf(row);
            var neighbors = new List<NeighborRowItem>();
            if (idx >= 0)
            {
                int start = Math.Max(0, idx - 2);
                int end = Math.Min(pageUnits.Count - 1, idx + 2);
                for (int i = start; i <= end; i++)
                {
                    var u = pageUnits[i];
                    if (i != idx && !string.Equals(u.Path, row.Path, StringComparison.Ordinal)) continue;
                    bool isCurrent = i == idx;
                    string tgt = string.IsNullOrWhiteSpace(u.Target) ? "(بانتظار الترجمة)" : u.Target;
                    neighbors.Add(new NeighborRowItem(
                        u.Source,
                        tgt,
                        ResBrush(isCurrent ? "AccentSoft" : "BgElevated"),
                        ResBrush(isCurrent ? "AccentIndicator" : "TextSecondary")));
                }
            }
            InspectorNeighborsList.ItemsSource = neighbors;
        }
        finally { updatingInspector = false; }
    }

    private void UpdateInspectorTokenAndLengthState(string source, string target, string savedWarning)
    {
        InspectorLengthText.Text = $"{target.Length:N0} / {source.Length:N0} حرف";

        var inspections = Tokens.Inspect(source, target, scannedEngine);
        if (inspections.Count == 0)
        {
            InspectorTokensContainer.Visibility = Visibility.Collapsed;
        }
        else
        {
            InspectorTokensContainer.Visibility = Visibility.Visible;
            var chips = inspections.Select(t =>
            {
                string countBadge = t.ExpectedCount > 1 ? $" ({t.PresentCount}/{t.ExpectedCount})" : (t.IsSatisfied ? " ✓" : "");
                Brush fg = ResBrush(t.IsSatisfied ? "SuccessFg" : "WarningFg");
                return new TokenChipItem(t.Token, t.Token + countBadge, fg);
            }).ToList();
            InspectorTokensList.ItemsSource = chips;

            string liveValidation = Tokens.Validate(source, target, scannedEngine);
            bool allOk = liveValidation.Length == 0;
            InspectorTokenStatusText.Text = allOk ? "✓ الرموز سليمة" : liveValidation;
            InspectorTokenStatusText.Foreground = ResBrush(allOk ? "SuccessFg" : "WarningFg");
        }

        string effectiveWarning = Tokens.Validate(source, target, scannedEngine);
        if (effectiveWarning.Length == 0 && !string.IsNullOrWhiteSpace(savedWarning) && savedWarning.Contains("تعارض"))
            effectiveWarning = savedWarning;

        InspectorWarningText.Text = effectiveWarning;
        InspectorWarningText.Visibility = effectiveWarning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InspectorTargetChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingInspector || Rows.SelectedItem is not Unit row) return;
        row.Target = InspectorTargetBox.Text;
        Rows.Items.Refresh();
        UpdateInspectorTokenAndLengthState(row.Source, row.Target, row.Warning);
    }

    private void InspectorTargetLostFocus(object sender, RoutedEventArgs e)
    {
        if (work == null && Rows.SelectedItem is Unit row && savedTargets.GetValueOrDefault(row.Id) != row.Target)
        {
            if (SaveEdits(commit: false))
                Status.Text = "حُفظ التعديل تلقائياً.";
        }
    }

    private void InsertTokenChipClick(object sender, RoutedEventArgs e)
    {
        if (work != null || Rows.SelectedItem is not Unit || sender is not Button { Tag: string token }) return;
        int caret = InspectorTargetBox.CaretIndex;
        int selLen = InspectorTargetBox.SelectionLength;
        string current = InspectorTargetBox.Text ?? "";
        if (selLen > 0 && InspectorTargetBox.SelectionStart >= 0 && InspectorTargetBox.SelectionStart + selLen <= current.Length)
        {
            int start = InspectorTargetBox.SelectionStart;
            InspectorTargetBox.Text = current.Remove(start, selLen).Insert(start, token);
            InspectorTargetBox.CaretIndex = start + token.Length;
        }
        else
        {
            caret = Math.Clamp(caret, 0, current.Length);
            InspectorTargetBox.Text = current.Insert(caret, token);
            InspectorTargetBox.CaretIndex = caret + token.Length;
        }
        InspectorTargetBox.Focus();
    }

    private void ApproveAndAdvanceClick(object sender, RoutedEventArgs e) => ApproveSelectedAndAdvance();

    private void InspectorTargetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            ApproveSelectedAndAdvance();
        }
    }

    private void ApproveSelectedAndAdvance()
    {
        if (!Rows.IsEnabled || projectPath == null || Rows.SelectedItem is not Unit current) return;
        var selectedSet = Rows.SelectedItems.OfType<Unit>().ToHashSet();
        if (selectedSet.Count == 0) selectedSet.Add(current);
        if (!SaveEdits(approveUnits: selectedSet)) return;

        Rows.Items.Refresh();
        Status.Text = $"اعتُمد النص ({selectedSet.Count}).";
        UpdateOverview();

        var items = Rows.Items.OfType<Unit>().ToList();
        int idx = items.IndexOf(current);
        for (int i = idx + 1; i < items.Count; i++)
        {
            if (items[i].Status != "approved")
            {
                Rows.SelectedItem = items[i];
                Rows.ScrollIntoView(items[i]);
                return;
            }
        }
        PopulateEditorInspector(current);
    }

    private async void QuickPinGlossaryClick(object sender, RoutedEventArgs e)
    {
        if (work != null || projectPath is not { } path) return;
        string src = (QuickTermSource.Text ?? "").Trim();
        string tgt = (QuickTermTarget.Text ?? "").Trim();
        if (src.Length == 0 || tgt.Length == 0)
        {
            Status.Text = "أدخل المصطلح الأصلي وترجمته العربية أولاً.";
            return;
        }
        try
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                store.Glossary(src, tgt);
            });
            QuickTermSource.Clear();
            QuickTermTarget.Clear();
            Status.Text = $"ثُبّت المصطلح «{src} ← {tgt}» في المعجم.";
            await ReloadLoreAndGlossariesAsync();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && Tabs.SelectedItem == EditorPage)
        {
            e.Handled = true;
            Search.Focus();
            Search.SelectAll();
        }
    }

    // ==================== LIBRARY RECENT CARDS ====================

    private async void OpenRecentCardClick(object sender, RoutedEventArgs e)
    {
        if (work != null || openingProject || sender is not Button { Tag: string path }) return;
        await SetProject(path);
    }

    private void RemoveRecentCardClick(object sender, RoutedEventArgs e)
    {
        if (work != null || openingProject || sender is not Button { Tag: string path }) return;
        var remaining = RecentProjects.Items.OfType<RecentProject>()
            .Where(item => !StringComparer.OrdinalIgnoreCase.Equals(item.Path, path))
            .ToList();
        RecentProjects.ItemsSource = remaining;
        RecentEmptyHint.Visibility = remaining.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SaveRecent(remaining);
        Status.Text = "أُزيل المشروع من القائمة الأخيرة.";
    }
}
