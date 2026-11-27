using System.Collections.ObjectModel;
using System.Diagnostics;
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

    private sealed class BotTermRow(string source, string target, string note, bool canMutate) : System.ComponentModel.INotifyPropertyChanged
    {
        private bool canMutate = canMutate;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Source { get; } = source;
        public string Target { get; } = target;
        public string Note { get; } = note;
        public string OriginLabel => Note == "lore-bot" ? "[بوت الكشّاف]" : "[بوت المشهد]";

        public bool CanMutate
        {
            get => canMutate;
            set
            {
                if (canMutate == value) return;
                canMutate = value;
                PropertyChanged?.Invoke(this, new(nameof(CanMutate)));
            }
        }
    }

    private sealed record TokenChipItem(string Token, string Label, bool IsSatisfied);

    private sealed record NeighborRowItem(string Source, string TargetPreview, bool IsCurrent);

    private readonly ObservableCollection<LiveSampleItem> liveSamples = [];
    private readonly ObservableCollection<BotTermRow> botTermRows = [];
    private Dictionary<string, string> cachedManualGlossary = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, GlossaryUnitViolation> glossaryViolationsById = new(StringComparer.Ordinal);
    private GlossaryAuditReport? lastGlossaryAudit;
    private bool updatingInspector;
    private bool syncingEditorChips;
    private bool loadingSharedMemoryPref;
    private bool loadingUpdatePref;
    private bool checkingUpdates;
    private ReleaseCheckResult? latestUpdateResult;

    private Brush ResBrush(string key) =>
        (Application.Current?.Resources[key] as Brush) ?? Brushes.Gray;

    private void InitializeStudioShell()
    {
        ThemeManager.AttachWindow(this);
        ThemeManager.ThemeChanged += OnStudioThemeChanged;
        Closed += (_, _) => ThemeManager.ThemeChanged -= OnStudioThemeChanged;
        LiveTranslationFeed.ItemsSource = liveSamples;
        BotTermsList.ItemsSource = botTermRows;
        UpdateThemeToggleVisual();
        SyncSettingsProjectScope();
        UpdateBudgetCapBadge();
        InitializeUpdateCheckerUi();
        _ = RefreshSharedMemoryBadgeAsync();
    }

    private void OnStudioThemeChanged(bool _)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnStudioThemeChanged(ThemeManager.IsDark));
            return;
        }
        UpdateThemeToggleVisual();
        RefreshScannedFiles();
        RefreshDirectInstallStatus();
    }

    private void UpdateThemeToggleVisual()
    {
        if (ThemeToggleButton == null) return;
        bool isDark = ThemeManager.IsDark;
        if (ThemeToggleText != null)
            ThemeToggleText.Text = isDark ? "فاتح" : "داكن";
        if (ThemeSunIcon != null)
            ThemeSunIcon.Visibility = isDark ? Visibility.Visible : Visibility.Collapsed;
        if (ThemeMoonIcon != null)
            ThemeMoonIcon.Visibility = isDark ? Visibility.Collapsed : Visibility.Visible;
        ThemeToggleButton.ToolTip = isDark ? "التبديل إلى المظهر الفاتح" : "التبديل إلى المظهر الداكن";
    }

    private void ToggleThemeClick(object sender, RoutedEventArgs e)
    {
        ThemeManager.ToggleTheme();
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

        if (current == TranslationPage && BotTabLive?.IsChecked != true && BotTabLore?.IsChecked != true && BotTabSections?.IsChecked != true && BotTabLive != null)
            BotTabLive.IsChecked = true;
        if (current == SettingsPage && SettingsTabApi?.IsChecked != true && SettingsTabRouting?.IsChecked != true && SettingsTabUsage?.IsChecked != true && SettingsTabApi != null)
            SettingsTabApi.IsChecked = true;

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
        bool filesCtaCoversTranslate = current == FilesPage
            && ContinueToBotsBtn?.Visibility == Visibility.Visible
            && nextAction is NextAction.Translate or NextAction.ResumeTeam;
        bool ownedByCurrentStage =
            (current == FilesPage && nextAction is NextAction.Scan or NextAction.Files or NextAction.Templates)
            || filesCtaCoversTranslate
            || (current == TranslationPage && nextAction is NextAction.Translate or NextAction.ResumeTeam or NextAction.Drafts)
            || (current == EditorPage && nextAction is NextAction.Issues)
            || (current == PackagesPage && nextAction is NextAction.Deploy or NextAction.Trial);

        Primary.Visibility = ownedByCurrentStage ? Visibility.Collapsed : Visibility.Visible;

        bool stageHasOwnPrimaryCta =
            (current == FilesPage && ContinueToBotsBtn?.Visibility == Visibility.Visible)
            || current == TranslationPage
            || current == EditorPage
            || current == PackagesPage
            || current == SettingsPage;

        string targetStyleKey = stageHasOwnPrimaryCta ? "OutlineAccentButtonStyle" : "PrimaryButtonStyle";
        if (TryFindResource(targetStyleKey) is Style targetStyle)
            Primary.Style = targetStyle;
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
        if (SyncSharedMemoryBtn != null) SyncSharedMemoryBtn.IsEnabled = hasProject && work == null;
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

        if (BotTabLore.IsChecked == true)
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

        bool canMutate = work == null;
        botTermRows.Clear();
        foreach (var entry in botEntries)
            botTermRows.Add(new BotTermRow(entry.Source, entry.Target, entry.Note, canMutate));

        BotTermsEmptyText.Visibility = botTermRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ClearBotTermsBtn.IsEnabled = botTermRows.Count > 0 && canMutate;
        cachedManualGlossary = manual.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        Terms.ItemsSource = manual;
        if (Rows?.SelectedItem is Unit selectedRow)
            UpdateInspectorTokenAndLengthState(selectedRow, selectedRow.Source, selectedRow.Target, selectedRow.Warning);

        if (LoreBotBadge != null && work == null)
        {
            bool hasLore = !string.IsNullOrWhiteSpace(brief) || botTermRows.Count > 0;
            LoreBotBadge.Text = hasLore ? $"جاهز ({botTermRows.Count:N0} مصطلح)" : "في الانتظار";
            LoreBotBadge.SetResourceReference(TextBlock.ForegroundProperty, hasLore ? "SuccessFg" : "TextMuted");
            if (LoreBotDetail != null)
            {
                LoreBotDetail.Text = hasLore
                    ? (string.IsNullOrWhiteSpace(brief)
                        ? $"اكتشف البوت {botTermRows.Count:N0} مصطلحاً — انقر للمراجعة أو التثبيت"
                        : brief)
                    : "يبني بطاقة عالم اللعبة ويكتشف الأسماء والمصطلحات تلقائياً";
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
        if (UnlimitedBudget.IsChecked != true)
        {
            if (!int.TryParse(CharacterBudget.Text, out int c) || c <= 0
                || !int.TryParse(RequestBudget.Text, out int r) || r <= 0)
            {
                Status.Text = "أدخل حدّاً موجباً للأحرف والطلبات أو فعّل خيار «بلا سقف».";
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
            if (EditorChipGlossary != null)
                EditorChipGlossary.IsChecked = filter == "glossary";
            if (EditorChipLength != null)
                EditorChipLength.IsChecked = filter == "length";
            if (GlossaryAuditBanner != null)
                GlossaryAuditBanner.Visibility = filter == "glossary" && lastGlossaryAudit is { ViolatingUnitCount: > 0 }
                    ? Visibility.Visible
                    : Visibility.Collapsed;
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
                if (InspectorGlossaryAlertBorder != null) InspectorGlossaryAlertBorder.Visibility = Visibility.Collapsed;
                if (InspectorLengthGuardPanel != null) InspectorLengthGuardPanel.Visibility = Visibility.Collapsed;
                InspectorWarningText.Visibility = Visibility.Collapsed;
                InspectorNeighborsList.ItemsSource = null;
                return;
            }

            InspectorTargetBox.IsEnabled = work == null;
            string contextPart = string.IsNullOrWhiteSpace(row.Context) ? "" : $" · {row.Context}";
            InspectorMetaText.Text = $"{row.Path} · {row.Locator}{contextPart}";
            InspectorSourceText.Text = row.Source;
            InspectorTargetBox.Text = row.Target;
            UpdateInspectorTokenAndLengthState(row, row.Source, row.Target, row.Warning);

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
                    neighbors.Add(new NeighborRowItem(u.Source, tgt, isCurrent));
                }
            }
            InspectorNeighborsList.ItemsSource = neighbors;
        }
        finally
        {
            updatingInspector = false;
            RefreshEditorLivePreview();
        }
    }

    private void UpdateInspectorTokenAndLengthState(Unit? row, string source, string target, string savedWarning)
    {
        var lengthEval = UiLengthGuard.Evaluate(source, target, row?.Context, row?.Locator, scannedEngine);
        InspectorLengthText.Text = lengthEval.Severity != UiLengthSeverity.NotApplicable && lengthEval.RecommendedMaxLength > 0
            ? $"{lengthEval.VisibleTargetLength:N0} / {lengthEval.VisibleSourceLength:N0} حرف (UI ≤{lengthEval.RecommendedMaxLength:N0})"
            : $"{target.Length:N0} / {source.Length:N0} حرف";

        if (InspectorLengthGuardPanel != null)
        {
            if (lengthEval.Severity == UiLengthSeverity.NotApplicable || string.IsNullOrWhiteSpace(target))
            {
                InspectorLengthGuardPanel.Visibility = Visibility.Collapsed;
            }
            else
            {
                InspectorLengthGuardPanel.Visibility = Visibility.Visible;
                InspectorLengthBar.Value = lengthEval.FillPercent;
                InspectorLengthGuardStatusText.Text = lengthEval.Summary;
                InspectorLengthGuardBudgetText.Text = $"{lengthEval.VisibleTargetLength:N0} / ≤{lengthEval.RecommendedMaxLength:N0}";

                string fgKey = lengthEval.Severity switch
                {
                    UiLengthSeverity.Overflow => "DangerFg",
                    UiLengthSeverity.Warning => "WarningFg",
                    _ => "SuccessFg"
                };
                string bgKey = lengthEval.Severity switch
                {
                    UiLengthSeverity.Overflow => "DangerBg",
                    UiLengthSeverity.Warning => "WarningBg",
                    _ => "BgSubtle"
                };
                string borderKey = lengthEval.Severity switch
                {
                    UiLengthSeverity.Overflow => "DangerFg",
                    UiLengthSeverity.Warning => "WarningFg",
                    _ => "BorderSubtle"
                };
                InspectorLengthGuardPanel.SetResourceReference(Border.BackgroundProperty, bgKey);
                InspectorLengthGuardPanel.SetResourceReference(Border.BorderBrushProperty, borderKey);
                InspectorLengthGuardStatusText.SetResourceReference(TextBlock.ForegroundProperty, fgKey);
                InspectorLengthBar.SetResourceReference(ProgressBar.ForegroundProperty, fgKey);
            }
        }

        if (InspectorGlossaryAlertBorder != null)
        {
            var glossaryViolations = GlossaryConsistencyChecker.CheckUnit(source, target, cachedManualGlossary, scannedEngine);
            if (glossaryViolations.Count == 0)
            {
                InspectorGlossaryAlertBorder.Visibility = Visibility.Collapsed;
            }
            else
            {
                InspectorGlossaryAlertText.Text = "مخالفة معجم يدوي: " + string.Join(" · ",
                    glossaryViolations.Select(v => $"«{v.TermSource}» ← المطلوب «{v.ExpectedArabic}»"));
                InspectorGlossaryAlertBorder.Visibility = Visibility.Visible;
            }
        }

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
                return new TokenChipItem(t.Token, t.Token + countBadge, t.IsSatisfied);
            }).ToList();
            InspectorTokensList.ItemsSource = chips;

            string liveValidation = Tokens.Validate(source, target, scannedEngine);
            bool allOk = liveValidation.Length == 0;
            InspectorTokenStatusText.Text = allOk ? "✓ الرموز سليمة" : liveValidation;
            InspectorTokenStatusText.SetResourceReference(TextBlock.ForegroundProperty, allOk ? "SuccessFg" : "WarningFg");
        }

        string effectiveWarning = Tokens.Validate(source, target, scannedEngine);
        if (effectiveWarning.Length == 0 && !string.IsNullOrWhiteSpace(savedWarning))
            effectiveWarning = savedWarning;

        InspectorWarningText.Text = effectiveWarning;
        InspectorWarningText.Visibility = effectiveWarning.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InspectorTargetChanged(object sender, TextChangedEventArgs e)
    {
        if (updatingInspector || Rows.SelectedItem is not Unit row) return;
        row.Target = InspectorTargetBox.Text;
        UpdateInspectorTokenAndLengthState(row, row.Source, row.Target, row.Warning);
        RefreshEditorLivePreview();
    }

    private void InspectorTargetLostFocus(object sender, RoutedEventArgs e)
    {
        if (work == null && Rows.SelectedItem is Unit row && savedTargets.GetValueOrDefault(row.Id) != row.Target)
        {
            if (SaveEdits(commit: false))
            {
                Status.Text = "حُفظ التعديل تلقائياً.";
                PopulateEditorInspector(row);
                UpdateOverview();
            }
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

    // ==================== SHARED TRANSLATION MEMORY ====================

    private async Task RefreshSharedMemoryBadgeAsync()
    {
        try
        {
            var stats = await Task.Run(() =>
            {
                using var tm = SharedTranslationMemory.OpenDefault();
                return tm.GetStats();
            });
            if (!IsLoaded) return;
            if (SharedMemoryBadge != null)
            {
                SharedMemoryBadge.Text = stats.TotalEntries > 0
                    ? $"الذاكرة المشتركة: {stats.TotalEntries:N0} نص جاهز"
                    : "الذاكرة المشتركة: فارغة بعد";
                SharedMemoryBadge.Visibility = Visibility.Visible;
            }
            if (SharedMemorySettingsStats != null)
            {
                SharedMemorySettingsStats.Text = $"المحفوظ حالياً: {stats.TotalEntries:N0} نص معتمد · إجمالي الاستخدام: {stats.TotalReuses:N0} مرة";
            }
        }
        catch { }
    }

    private void UseSharedMemoryPrefillChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || loadingSharedMemoryPref || projectPath is not { } path) return;
        bool enabled = UseSharedMemoryPrefill?.IsChecked == true;
        try
        {
            using var store = new ProjectStore(path);
            new ProjectSettings(store).UseSharedMemoryPrefill = enabled;
        }
        catch { }
    }

    private void RecordApprovedInSharedMemoryBackground(IReadOnlyList<(string Source, string Target)> pairs, string engine)
    {
        if (pairs.Count == 0) return;
        _ = Task.Run(() =>
        {
            try
            {
                using var tm = SharedTranslationMemory.OpenDefault();
                int recorded = tm.RecordApproved(pairs, engine);
                if (recorded > 0)
                    Dispatcher.BeginInvoke(() => _ = RefreshSharedMemoryBadgeAsync());
            }
            catch { }
        });
    }

    private void SyncProjectApprovedToSharedMemoryBackground(string path)
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var store = new ProjectStore(path);
                using var tm = SharedTranslationMemory.OpenDefault();
                int recorded = tm.SyncApprovedFromProject(store);
                if (recorded > 0)
                    Dispatcher.BeginInvoke(() => _ = RefreshSharedMemoryBadgeAsync());
            }
            catch { }
        });
    }

    private async void SyncSharedMemoryClick(object sender, RoutedEventArgs e)
    {
        if (work != null || projectPath is not { } path) return;
        try
        {
            int count = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                using var tm = SharedTranslationMemory.OpenDefault();
                return tm.SyncApprovedFromProject(store);
            });
            Status.Text = count == 0
                ? "لا توجد نصوص معتمدة قابلة للمزامنة في المشروع الحالي."
                : $"تمت مزامنة {count:N0} ترجمة معتمدة إلى الذاكرة المشتركة.";
            await RefreshSharedMemoryBadgeAsync();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void ClearSharedMemoryClick(object sender, RoutedEventArgs e)
    {
        if (work != null) return;
        if (MessageBox.Show(this,
            "سيتم مسح جميع النصوص المحفوظة في الذاكرة المشتركة عابرة للمشاريع دون المساس بمشاريعك الحالية. هل تريد المتابعة؟",
            "مسح الذاكرة المشتركة", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            await Task.Run(() =>
            {
                using var tm = SharedTranslationMemory.OpenDefault();
                tm.Clear();
            });
            Status.Text = "مُسحت الذاكرة المشتركة بنجاح.";
            await RefreshSharedMemoryBadgeAsync();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    // ==================== GLOSSARY CONSISTENCY CHECK ====================

    private void ResetGlossaryAuditState()
    {
        lastGlossaryAudit = null;
        glossaryViolationsById.Clear();
        if (BotGlossaryAuditBanner != null) BotGlossaryAuditBanner.Visibility = Visibility.Collapsed;
        if (GlossaryAuditBanner != null) GlossaryAuditBanner.Visibility = Visibility.Collapsed;
        if (EditorChipGlossary != null)
        {
            EditorChipGlossary.Visibility = Visibility.Collapsed;
            EditorChipGlossary.Content = "مخالفات المعجم";
        }
        if (ReviewStatus == "glossary" && ReviewFilter != null)
            ReviewFilter.SelectedValue = "";
    }

    private async void RunGlossaryAuditClick(object sender, RoutedEventArgs e)
    {
        bool autoFilterInEditor = Tabs.SelectedItem == EditorPage;
        await RunGlossaryAuditCoreAsync(autoFilterInEditor);
    }

    private async Task RunGlossaryAuditCoreAsync(bool autoFilterInEditor = false)
    {
        if (work != null || projectPath is not { } path || !SaveEdits()) return;
        try
        {
            Status.Text = "جارٍ فحص اتّساق السطور المترجمة مع المعجم اليدوي…";
            var report = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return GlossaryConsistencyChecker.AuditProject(store);
            });
            if (!IsLoaded || projectPath != path) return;

            ApplyGlossaryAuditReport(report);
            if (report.ManualTermCount == 0)
            {
                Status.Text = "المعجم اليدوي فارغ — أضف أو ثبّت مصطلحات أولاً ثم أعد الفحص.";
                return;
            }
            if (report.ViolatingUnitCount == 0)
            {
                Status.Text = $"✓ ممتاز! جميع السطور المفحوصة ({report.ScannedUnits:N0} سطر) متّسقة مع المعجم اليدوي ({report.ManualTermCount:N0} مصطلح).";
                if (ReviewStatus == "glossary")
                {
                    ReviewFilter.SelectedValue = "";
                    offset = 0;
                    await RefreshRows();
                }
                return;
            }

            Status.Text = $"عُثر على {report.ViolatingUnitCount:N0} سطر يخالف المعجم اليدوي ({report.TotalTermViolations:N0} مخالفة).";
            if (autoFilterInEditor || ReviewStatus == "glossary")
            {
                ReviewFilter.SelectedValue = "glossary";
                offset = 0;
                await RefreshRows();
            }
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void ApplyGlossaryAuditReport(GlossaryAuditReport report)
    {
        lastGlossaryAudit = report;
        glossaryViolationsById = report.Items.ToDictionary(item => item.Unit.Id, StringComparer.Ordinal);

        bool hasViolations = report.ViolatingUnitCount > 0;
        string title = $"عُثر على {report.ViolatingUnitCount:N0} سطر يخالف المعجم اليدوي ({report.TotalTermViolations:N0} مخالفة)";
        string exactPart = report.ExactFixableCount > 0
            ? $" · يمكن تحديث {report.ExactFixableCount:N0} سطر متطابق 100% فوراً"
            : "";
        string detail = $"فُحص {report.ScannedUnits:N0} سطر مترجم مقابل {report.ManualTermCount:N0} مصطلحاً معتمداً{exactPart}.";

        if (BotGlossaryAuditBanner != null)
        {
            BotGlossaryAuditBanner.Visibility = hasViolations ? Visibility.Visible : Visibility.Collapsed;
            BotGlossaryAuditTitle.Text = title;
            BotGlossaryAuditDetail.Text = detail;
            BotGlossaryExactFixBtn.Visibility = report.ExactFixableCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            BotGlossaryExactFixBtn.Content = $"تحديث المطابقات التامة ({report.ExactFixableCount:N0})";
        }

        if (GlossaryAuditBanner != null)
        {
            GlossaryAuditBannerTitle.Text = title;
            GlossaryAuditBannerDetail.Text = detail;
            EditorGlossaryExactFixBtn.Visibility = report.ExactFixableCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            EditorGlossaryExactFixBtn.Content = $"تحديث المطابقات التامة ({report.ExactFixableCount:N0})";
            GlossaryAuditBanner.Visibility = hasViolations && ReviewStatus == "glossary" ? Visibility.Visible : Visibility.Collapsed;
        }

        if (EditorChipGlossary != null)
        {
            EditorChipGlossary.Visibility = hasViolations ? Visibility.Visible : Visibility.Collapsed;
            EditorChipGlossary.Content = hasViolations ? $"مخالفات المعجم ({report.ViolatingUnitCount:N0})" : "مخالفات المعجم";
        }
    }

    private async void FilterGlossaryViolationsClick(object sender, RoutedEventArgs e)
    {
        if (lastGlossaryAudit is not { ViolatingUnitCount: > 0 }) return;
        SelectStage(EditorPage);
        Search.Clear();
        offset = 0;
        ReviewFilter.SelectedValue = "glossary";
        await RefreshRows();
    }

    private void DismissBotGlossaryBannerClick(object sender, RoutedEventArgs e)
    {
        if (BotGlossaryAuditBanner != null)
            BotGlossaryAuditBanner.Visibility = Visibility.Collapsed;
    }

    private async void ExitGlossaryFilterClick(object sender, RoutedEventArgs e)
    {
        offset = 0;
        ReviewFilter.SelectedValue = "";
        await RefreshRows();
    }

    private async void ApplyExactGlossaryFixesClick(object sender, RoutedEventArgs e)
    {
        if (work != null || projectPath is not { } path || lastGlossaryAudit is not { ExactFixableCount: > 0 } audit || !SaveEdits()) return;
        try
        {
            int fixedCount = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return GlossaryConsistencyChecker.ApplyExactTermFixes(store, audit.Items);
            });
            if (!IsLoaded || projectPath != path) return;
            if (fixedCount > 0)
                SyncProjectApprovedToSharedMemoryBackground(path);
            Status.Text = fixedCount == 0
                ? "لم تتبقَ مطابقات تامة للتحديث."
                : $"تم تحديث {fixedCount:N0} سطر متطابق 100% وفق المعجم اليدوي.";
            await RunGlossaryAuditCoreAsync(autoFilterInEditor: ReviewStatus == "glossary");
            await RefreshRows();
            UpdateOverview();
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    // ==================== QUIET GITHUB RELEASES UPDATE CHECKER ====================

    private void InitializeUpdateCheckerUi()
    {
        var current = AppUpdateChecker.GetCurrentVersion();
        if (AppCurrentVersionText != null)
            AppCurrentVersionText.Text = current.ToTagString();

        bool enabled = AppUpdateChecker.ReadAutoCheckEnabled();
        loadingUpdatePref = true;
        try
        {
            if (AutoCheckUpdatesCheckBox != null)
                AutoCheckUpdatesCheckBox.IsChecked = enabled;
        }
        finally { loadingUpdatePref = false; }

        if (UpdateCheckStatusText != null)
        {
            UpdateCheckStatusText.Text = enabled
                ? $"الإصدار الحالي {current.ToTagString()} — سيتحقّق البرنامج بهدوء عند التشغيل."
                : $"التحقق التلقائي متوقف — يمكنك التحقق يدوياً في أي وقت (الإصدار الحالي {current.ToTagString()}).";
        }
    }

    private async Task CheckForUpdatesOnStartupAsync()
    {
        if (!AppUpdateChecker.ReadAutoCheckEnabled()) return;
        await CheckForUpdatesCoreAsync(manual: false, recoveryLifetime.Token);
    }

    private void AutoCheckUpdatesChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || loadingUpdatePref) return;
        bool enabled = AutoCheckUpdatesCheckBox?.IsChecked == true;
        try { AppUpdateChecker.WriteAutoCheckEnabled(enabled); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        if (!enabled)
        {
            if (UpdateAvailableBadge != null) UpdateAvailableBadge.Visibility = Visibility.Collapsed;
            if (UpdateCheckStatusText != null)
                UpdateCheckStatusText.Text = "أُوقف التحقق التلقائي من التحديثات — يمكنك التحقق يدوياً في أي وقت.";
        }
        else
        {
            if (UpdateCheckStatusText != null)
                UpdateCheckStatusText.Text = "فُعّل التحقق الهادئ من التحديثات عند التشغيل.";
            if (latestUpdateResult?.IsUpdateAvailable == true && UpdateAvailableBadge != null)
                UpdateAvailableBadge.Visibility = Visibility.Visible;
        }
    }

    private async void CheckUpdatesNowClick(object sender, RoutedEventArgs e)
    {
        await CheckForUpdatesCoreAsync(manual: true, recoveryLifetime.Token);
    }

    private async Task CheckForUpdatesCoreAsync(bool manual, CancellationToken cancellation)
    {
        if (checkingUpdates) return;
        checkingUpdates = true;
        if (manual)
        {
            if (CheckUpdatesNowButton != null) CheckUpdatesNowButton.IsEnabled = false;
            if (UpdateCheckStatusText != null) UpdateCheckStatusText.Text = "جارٍ التحقق من أحدث إصدار رسمي على GitHub…";
        }

        try
        {
            var result = await AppUpdateChecker.CheckForUpdatesAsync(cancellation: cancellation);
            if (!IsLoaded || cancellation.IsCancellationRequested) return;

            latestUpdateResult = result;
            ApplySelfUpdateUi();
            if (result.IsUpdateAvailable)
            {
                string tag = result.LatestVersion!.Value.ToTagString();
                if (UpdateAvailableBadgeText != null)
                    UpdateAvailableBadgeText.Text = $"تحديث متاح · {tag}";
                if (UpdateAvailableBadge != null)
                {
                    UpdateAvailableBadge.ToolTip = $"يتوفر إصدار رسمي أحدث ({tag}) — اضغط لفتح إعدادات التحديث (تحديث تلقائي موثّق أو صفحة GitHub)";
                    UpdateAvailableBadge.Visibility = Visibility.Visible;
                }
                if (UpdateCheckStatusText != null)
                    UpdateCheckStatusText.Text = $"يتوفر إصدار رسمي أحدث: {tag} (الإصدار الحالي: {result.CurrentVersion.ToTagString()}).";
                if (OpenUpdateReleaseButton != null)
                    OpenUpdateReleaseButton.Visibility = Visibility.Visible;
                if (manual)
                    Status.Text = $"يتوفر إصدار رسمي أحدث من Locara ({tag}).";
            }
            else if (result.Status == ReleaseCheckStatus.UpToDate)
            {
                if (UpdateAvailableBadge != null) UpdateAvailableBadge.Visibility = Visibility.Collapsed;
                if (OpenUpdateReleaseButton != null) OpenUpdateReleaseButton.Visibility = Visibility.Collapsed;
                if (UpdateCheckStatusText != null)
                    UpdateCheckStatusText.Text = $"أنت تستخدم أحدث إصدار رسمي ({result.CurrentVersion.ToTagString()}).";
                if (manual)
                    Status.Text = $"Locara محدّث إلى أحدث إصدار ({result.CurrentVersion.ToTagString()}).";
            }
            else if (manual)
            {
                if (UpdateCheckStatusText != null)
                    UpdateCheckStatusText.Text = "تعذّر الوصول إلى GitHub Releases حالياً — تحقق من الاتصال أو حاول لاحقاً.";
                Status.Text = "تعذّر التحقق من التحديثات حالياً.";
            }
        }
        finally
        {
            checkingUpdates = false;
            if (manual && IsLoaded && CheckUpdatesNowButton != null)
                CheckUpdatesNowButton.IsEnabled = true;
        }
    }

    private void OpenAvailableUpdateClick(object sender, RoutedEventArgs e)
    {
        Uri? url = latestUpdateResult?.ReleaseUrl;
        if (!AppUpdateChecker.IsTrustedReleaseUrl(url))
            url = AppUpdateChecker.OfficialReleasesPageUri;
        try
        {
            Process.Start(new ProcessStartInfo(url!.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
        }
    }

    private async void RetranslateGlossaryViolationsClick(object sender, RoutedEventArgs e)
    {
        if (work != null || lastGlossaryAudit is not { ViolatingUnitCount: > 0 } audit) return;
        var violatingUnits = audit.Items.Select(i => i.Unit).ToList();
        await StartTargetedGlossaryReviewAsync(violatingUnits);
    }
}
