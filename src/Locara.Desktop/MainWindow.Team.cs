using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Locara.Core;

namespace Locara.Desktop;

public partial class MainWindow
{
    private sealed class AgentRow : INotifyPropertyChanged
    {
        private string role = "";
        private string modelLabel = "";
        private string stage = "";
        private string batchLabel = "—";
        private int completedCount;
        private string detail = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        public int WorkerId { get; init; }
        public string AgentName { get; init; } = "";

        public string Role
        {
            get => role;
            set { if (role != value) { role = value; PropertyChanged?.Invoke(this, new(nameof(Role))); } }
        }
        public string ModelLabel
        {
            get => modelLabel;
            set { if (modelLabel != value) { modelLabel = value; PropertyChanged?.Invoke(this, new(nameof(ModelLabel))); } }
        }
        public string Stage
        {
            get => stage;
            set { if (stage != value) { stage = value; PropertyChanged?.Invoke(this, new(nameof(Stage))); } }
        }
        public string BatchLabel
        {
            get => batchLabel;
            set { if (batchLabel != value) { batchLabel = value; PropertyChanged?.Invoke(this, new(nameof(BatchLabel))); } }
        }
        public int CompletedCount
        {
            get => completedCount;
            set { if (completedCount != value) { completedCount = value; PropertyChanged?.Invoke(this, new(nameof(CompletedCount))); } }
        }
        public string Detail
        {
            get => detail;
            set { if (detail != value) { detail = value; PropertyChanged?.Invoke(this, new(nameof(Detail))); } }
        }
    }

    private sealed record ProfileChoice(string Key, string Label);

    private sealed class WorkerRoutingRow : INotifyPropertyChanged
    {
        private readonly Func<string?, IReadOnlyList<string>> modelsForProfile;
        private readonly Action onChanged;
        private IReadOnlyList<ProfileChoice> profileOptions = [];
        private string selectedProfile = "";
        private IReadOnlyList<string> availableModels = [];
        private string selectedModel = "";

        public WorkerRoutingRow(int workerId, Func<string?, IReadOnlyList<string>> modelsForProfile, Action onChanged)
        {
            WorkerId = workerId;
            AgentName = $"وكيل #{workerId}";
            this.modelsForProfile = modelsForProfile;
            this.onChanged = onChanged;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public int WorkerId { get; }
        public string AgentName { get; }

        public IReadOnlyList<ProfileChoice> ProfileOptions
        {
            get => profileOptions;
            set { profileOptions = value; PropertyChanged?.Invoke(this, new(nameof(ProfileOptions))); }
        }

        public string SelectedProfile
        {
            get => selectedProfile;
            set
            {
                string next = value ?? "";
                if (selectedProfile == next) return;
                selectedProfile = next;
                PropertyChanged?.Invoke(this, new(nameof(SelectedProfile)));
                RefreshModels(preserveModel: false);
                onChanged();
            }
        }

        public IReadOnlyList<string> AvailableModels
        {
            get => availableModels;
            private set { availableModels = value; PropertyChanged?.Invoke(this, new(nameof(AvailableModels))); }
        }

        public string SelectedModel
        {
            get => selectedModel;
            set
            {
                string next = value ?? "";
                if (selectedModel == next) return;
                selectedModel = next;
                PropertyChanged?.Invoke(this, new(nameof(SelectedModel)));
                onChanged();
            }
        }

        public void SetInitial(string profile, string model, IReadOnlyList<ProfileChoice> options)
        {
            profileOptions = options;
            selectedProfile = profile ?? "";
            availableModels = modelsForProfile(selectedProfile);
            selectedModel = model ?? "";
            PropertyChanged?.Invoke(this, new(nameof(ProfileOptions)));
            PropertyChanged?.Invoke(this, new(nameof(SelectedProfile)));
            PropertyChanged?.Invoke(this, new(nameof(AvailableModels)));
            PropertyChanged?.Invoke(this, new(nameof(SelectedModel)));
        }

        public void RefreshModels(bool preserveModel)
        {
            string current = selectedModel;
            AvailableModels = modelsForProfile(selectedProfile);
            if (!preserveModel)
            {
                selectedModel = "";
                PropertyChanged?.Invoke(this, new(nameof(SelectedModel)));
            }
            else if (selectedModel != current)
            {
                selectedModel = current;
                PropertyChanged?.Invoke(this, new(nameof(SelectedModel)));
            }
        }
    }

    private readonly ObservableCollection<AgentRow> liveAgents = [];
    private readonly ObservableCollection<string> liveEvents = [];
    private readonly Dictionary<int, AgentRow> agentByWorker = [];
    private readonly ObservableCollection<WorkerRoutingRow> workerRoutingRows = [];
    private readonly Dictionary<int, AgentTarget> workerTargetCache = [];
    private bool loadingRouting;

    private void InitializeLiveMonitor()
    {
        AgentsGrid.ItemsSource = liveAgents;
        ActivityLog.ItemsSource = liveEvents;
        WorkerRoutingList.ItemsSource = workerRoutingRows;
        if (PromptPresetSelector != null)
        {
            PromptPresetSelector.ItemsSource = ArabicPromptPresets.All;
            PromptPresetSelector.SelectedIndex = 0;
        }
        RefreshRoutingOptions();
    }

    private static string FormatModelBadge(string? profile, string? model)
    {
        string p = (profile ?? "").Trim();
        string m = (model ?? "").Trim();
        if (p.Length > 0 && m.Length > 0) return $"{p} · {m}";
        return m.Length > 0 ? m : p;
    }

    private List<ProfileChoice> BuildRoleProfileChoices(string defaultLabel)
    {
        var choices = new List<ProfileChoice> { new("", defaultLabel) };
        foreach (var profile in savedProfiles)
        {
            string subtitle = string.IsNullOrWhiteSpace(profile.Model) ? profile.Provider : $"{profile.Provider} · {profile.Model}";
            choices.Add(new(profile.Name, $"{profile.Name} ({subtitle})"));
        }
        return choices;
    }

    private void RefreshRoutingOptions()
    {
        if (TranslatorProfileInput == null) return;
        loadingRouting = true;
        try
        {
            var roleChoices = BuildRoleProfileChoices("الافتراضي (الاتصال النشط)");
            var workerChoices = BuildRoleProfileChoices("حسب الدور (افتراضي)");

            SyncRoleCombo(TranslatorProfileInput, TranslatorModelInput, roleChoices);
            SyncRoleCombo(ReviewerProfileInput, ReviewerModelInput, roleChoices);
            SyncRoleCombo(SupervisorProfileInput, SupervisorModelInput, roleChoices);
            SyncWorkerRoutingRows(workerChoices);
        }
        finally { loadingRouting = false; }
        RefreshProviderSummary();
    }

    private void SyncRoleCombo(ComboBox profileBox, ComboBox modelBox, List<ProfileChoice> choices)
    {
        string currentProfile = profileBox.SelectedValue as string ?? "";
        string currentModel = modelBox.Text ?? "";
        profileBox.ItemsSource = choices;
        profileBox.SelectedValue = choices.Any(c => c.Key.Equals(currentProfile, StringComparison.OrdinalIgnoreCase))
            ? choices.First(c => c.Key.Equals(currentProfile, StringComparison.OrdinalIgnoreCase)).Key
            : "";
        string activeProfile = profileBox.SelectedValue as string ?? "";
        modelBox.ItemsSource = GetModelsForProfile(activeProfile);
        modelBox.Text = currentModel;
    }

    private void SyncWorkerRoutingRows(List<ProfileChoice>? workerChoices = null)
    {
        workerChoices ??= BuildRoleProfileChoices("حسب الدور (افتراضي)");
        foreach (var existing in workerRoutingRows)
            workerTargetCache[existing.WorkerId] = new AgentTarget(existing.SelectedProfile.Trim(), existing.SelectedModel.Trim());

        int count = SelectedWorkers();
        workerRoutingRows.Clear();
        for (int i = 1; i <= count; i++)
        {
            var cached = workerTargetCache.GetValueOrDefault(i) ?? new AgentTarget();
            string matchedProfile = workerChoices.FirstOrDefault(c => c.Key.Equals(cached.Profile, StringComparison.OrdinalIgnoreCase))?.Key ?? "";
            var row = new WorkerRoutingRow(i, GetModelsForProfile, OnWorkerRowChanged);
            row.SetInitial(matchedProfile, cached.Model, workerChoices);
            workerRoutingRows.Add(row);
        }
    }

    private void OnWorkerRowChanged()
    {
        if (loadingRouting) return;
        foreach (var row in workerRoutingRows)
            workerTargetCache[row.WorkerId] = new AgentTarget(row.SelectedProfile.Trim(), row.SelectedModel.Trim());
        RefreshProviderSummary();
    }

    private void RoleProfileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingRouting) return;
        if (sender == TranslatorProfileInput) UpdateRoleModelDropdown(TranslatorProfileInput, TranslatorModelInput);
        else if (sender == ReviewerProfileInput) UpdateRoleModelDropdown(ReviewerProfileInput, ReviewerModelInput);
        else if (sender == SupervisorProfileInput) UpdateRoleModelDropdown(SupervisorProfileInput, SupervisorModelInput);
        RefreshProviderSummary();
    }

    private void UpdateRoleModelDropdown(ComboBox profileBox, ComboBox modelBox)
    {
        string selectedProfile = profileBox.SelectedValue as string ?? "";
        var models = GetModelsForProfile(selectedProfile);
        loadingRouting = true;
        try
        {
            modelBox.ItemsSource = models;
            if (!string.IsNullOrEmpty(selectedProfile) && !models.Contains(modelBox.Text, StringComparer.OrdinalIgnoreCase))
                modelBox.Text = "";
        }
        finally { loadingRouting = false; }
    }

    private void RoutingFieldChanged(object sender, RoutedEventArgs e)
    {
        if (!loadingRouting) RefreshProviderSummary();
    }

    private void TogglePerWorkerRouting(object sender, RoutedEventArgs e)
    {
        if (PerWorkerRoutingContainer == null) return;
        bool enabled = EnablePerWorkerRouting.IsChecked == true;
        PerWorkerRoutingContainer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled && workerRoutingRows.Count != SelectedWorkers())
        {
            loadingRouting = true;
            try { SyncWorkerRoutingRows(); }
            finally { loadingRouting = false; }
        }
        if (!loadingRouting) RefreshProviderSummary();
    }

    private void WorkersCountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || loadingRouting) return;
        loadingRouting = true;
        try { SyncWorkerRoutingRows(); }
        finally { loadingRouting = false; }
        RefreshProviderSummary();
    }

    private void RefreshProviderSummary()
    {
        if (ProviderSummary == null || Provider == null || Provider.SelectedItem == null) return;
        string providerName = Selected(Provider);
        string activeName = savedProfiles.Count > 0 ? ActiveProfile.Name : "الافتراضي";
        string baseSummary = string.IsNullOrWhiteSpace(Model.Text) && providerName != "azure"
            ? $"⚠ إعداد الـ API: {activeName} ({providerName})"
            : $"{activeName} · {Model.Text.Trim()}";
        if (NavSettings != null)
            NavSettings.Tag = string.IsNullOrWhiteSpace(Model.Text) && providerName != "azure" ? "!" : "";

        var overrides = new List<string>();
        string trP = TranslatorProfileInput?.SelectedValue as string ?? "";
        string trM = TranslatorModelInput?.Text?.Trim() ?? "";
        if (trP.Length > 0 || trM.Length > 0) overrides.Add($"مترجم: {FormatModelBadge(trP, trM)}");

        string rvP = ReviewerProfileInput?.SelectedValue as string ?? "";
        string rvM = ReviewerModelInput?.Text?.Trim() ?? "";
        if (rvP.Length > 0 || rvM.Length > 0) overrides.Add($"مراجع: {FormatModelBadge(rvP, rvM)}");

        string supP = SupervisorProfileInput?.SelectedValue as string ?? "";
        string supM = SupervisorModelInput?.Text?.Trim() ?? "";
        if (supP.Length > 0 || supM.Length > 0) overrides.Add($"مشرف: {FormatModelBadge(supP, supM)}");

        if (EnablePerWorkerRouting?.IsChecked == true)
        {
            int customWorkers = workerRoutingRows.Count(r => !string.IsNullOrWhiteSpace(r.SelectedProfile) || !string.IsNullOrWhiteSpace(r.SelectedModel));
            if (customWorkers > 0) overrides.Add($"تخصيص فردي ({customWorkers} وكيل)");
        }

        ProviderSummary.Text = overrides.Count == 0
            ? baseSummary
            : $"{baseSummary}  |  {string.Join(" · ", overrides)}";
    }

    private void LoadTeamRoleSettings(TeamSettings settings)
    {
        loadingRouting = true;
        try
        {
            TeamStyleInput.Text = settings.Style;
            SupervisorPromptInput.Text = settings.Supervisor;
            TranslatorPromptInput.Text = settings.Translator;
            ReviewerPromptInput.Text = settings.Reviewer;

            string targetWorkers = (settings.Workers > 0 ? settings.Workers : 4).ToString();
            WorkersCount.SelectedValue = WorkersCount.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Tag, targetWorkers))
                ? targetWorkers : "4";
            string targetBatch = (settings.BatchSize > 0 ? settings.BatchSize : 30).ToString();
            BatchSizeChoice.SelectedValue = BatchSizeChoice.Items.OfType<ComboBoxItem>().Any(i => Equals(i.Tag, targetBatch))
                ? targetBatch : "30";
            SpeedMode.SelectedValue = settings.FastMode ? "fast" : settings.SmartReview ? "smart" : "team";

            var roleChoices = BuildRoleProfileChoices("الافتراضي (الاتصال النشط)");
            SetRoleRoutingUi(TranslatorProfileInput, TranslatorModelInput, settings.TranslatorProfile, settings.TranslatorModel, roleChoices);
            SetRoleRoutingUi(ReviewerProfileInput, ReviewerModelInput, settings.ReviewerProfile, settings.ReviewerModel, roleChoices);
            SetRoleRoutingUi(SupervisorProfileInput, SupervisorModelInput, settings.SupervisorProfile, settings.SupervisorModel, roleChoices);

            workerTargetCache.Clear();
            bool hasWorkerTargets = false;
            if (settings.WorkerTargets != null)
            {
                foreach (var kv in settings.WorkerTargets)
                {
                    if (kv.Key is < 1 or > 16 || kv.Value == null) continue;
                    string p = (kv.Value.Profile ?? "").Trim();
                    string m = (kv.Value.Model ?? "").Trim();
                    if (p.Length == 0 && m.Length == 0) continue;
                    workerTargetCache[kv.Key] = new AgentTarget(p, m);
                    hasWorkerTargets = true;
                }
            }
            EnablePerWorkerRouting.IsChecked = hasWorkerTargets;
            PerWorkerRoutingContainer.Visibility = hasWorkerTargets ? Visibility.Visible : Visibility.Collapsed;
            SyncWorkerRoutingRows();
        }
        finally { loadingRouting = false; }
        RefreshProviderSummary();
    }

    private void SetRoleRoutingUi(ComboBox profileBox, ComboBox modelBox, string? profile, string? model, List<ProfileChoice> choices)
    {
        profileBox.ItemsSource = choices;
        string p = (profile ?? "").Trim();
        var match = choices.FirstOrDefault(c => c.Key.Equals(p, StringComparison.OrdinalIgnoreCase));
        profileBox.SelectedValue = match?.Key ?? "";
        modelBox.ItemsSource = GetModelsForProfile(profileBox.SelectedValue as string);
        modelBox.Text = (model ?? "").Trim();
    }

    private int SelectedWorkers() => int.TryParse(WorkersCount.SelectedValue as string, out int n) ? Math.Clamp(n, 1, 16) : 4;
    private int SelectedBatchSize() => int.TryParse(BatchSizeChoice.SelectedValue as string, out int n) ? Math.Clamp(n, 1, 80) : 30;
    private bool SelectedFastMode() => (SpeedMode.SelectedValue as string) == "fast";
    private bool SelectedSmartMode() => (SpeedMode.SelectedValue as string) is null or "smart";

    private Dictionary<int, AgentTarget>? ReadWorkerTargets()
    {
        if (EnablePerWorkerRouting.IsChecked != true) return null;
        var map = new Dictionary<int, AgentTarget>();
        foreach (var row in workerRoutingRows)
        {
            string p = (row.SelectedProfile ?? "").Trim();
            string m = (row.SelectedModel ?? "").Trim();
            if (p.Length > 0 || m.Length > 0) map[row.WorkerId] = new AgentTarget(p, m);
        }
        return map.Count > 0 ? map : null;
    }

    private TeamSettings ReadInlineTeamSettings(bool reviewDrafts) => new(
        Style: TeamStyleInput.Text.Trim(),
        Supervisor: SupervisorPromptInput.Text.Trim(),
        Translator: TranslatorPromptInput.Text.Trim(),
        Reviewer: ReviewerPromptInput.Text.Trim(),
        SupervisorModel: (SupervisorModelInput.Text ?? "").Trim(),
        TranslatorModel: (TranslatorModelInput.Text ?? "").Trim(),
        ReviewerModel: (ReviewerModelInput.Text ?? "").Trim(),
        Workers: SelectedWorkers(),
        BatchSize: SelectedBatchSize(),
        FastMode: SelectedFastMode() && !reviewDrafts,
        ReviewDrafts: reviewDrafts,
        SupervisorProfile: (SupervisorProfileInput.SelectedValue as string ?? "").Trim(),
        TranslatorProfile: (TranslatorProfileInput.SelectedValue as string ?? "").Trim(),
        ReviewerProfile: (ReviewerProfileInput.SelectedValue as string ?? "").Trim(),
        WorkerTargets: ReadWorkerTargets(),
        SmartReview: SelectedSmartMode() && !SelectedFastMode() && !reviewDrafts);

    private void ToggleConnectionPanel(object sender, RoutedEventArgs e) => OpenConnectionSettings();

    private void ToggleRoutingPanel(object sender, RoutedEventArgs e) => OpenRoutingSettings();

    private void SaveTeamRoleSettings(object sender, RoutedEventArgs e)
    {
        if (projectPath == null) return;
        try
        {
            using var project = new ProjectStore(projectPath);
            new TranslationTeam(project).Settings = ReadInlineTeamSettings(reviewDrafts: false);
            RefreshProviderSummary();
            Status.Text = "حُفظ توزيع النماذج وإعدادات الأدوار لهذا المشروع.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void ResetRoutingSettings(object sender, RoutedEventArgs e)
    {
        loadingRouting = true;
        try
        {
            var roleChoices = BuildRoleProfileChoices("الافتراضي (الاتصال النشط)");
            SetRoleRoutingUi(TranslatorProfileInput, TranslatorModelInput, "", "", roleChoices);
            SetRoleRoutingUi(ReviewerProfileInput, ReviewerModelInput, "", "", roleChoices);
            SetRoleRoutingUi(SupervisorProfileInput, SupervisorModelInput, "", "", roleChoices);
            workerTargetCache.Clear();
            EnablePerWorkerRouting.IsChecked = false;
            PerWorkerRoutingContainer.Visibility = Visibility.Collapsed;
            SyncWorkerRoutingRows();
        }
        finally { loadingRouting = false; }
        RefreshProviderSummary();
        Status.Text = "أُعيد توزيع النماذج إلى الاتصال الافتراضي للجميع.";
    }

    private void ResetTeamRoleSettings(object sender, RoutedEventArgs e)
    {
        SupervisorPromptInput.Clear();
        TranslatorPromptInput.Clear();
        ReviewerPromptInput.Clear();
        Status.Text = "أُعيدت قوالب الأدوار الافتراضية. اضغط «حفظ إعدادات الأدوار» لتثبيتها.";
    }

    private void ApplySelectedPromptPreset(object sender, RoutedEventArgs e)
    {
        if (PromptPresetSelector?.SelectedItem is not ArabicPromptPreset preset) return;
        TeamStyleInput.Text = preset.Style;
        SupervisorPromptInput.Text = preset.Supervisor;
        TranslatorPromptInput.Text = preset.Translator;
        ReviewerPromptInput.Text = preset.Reviewer;
        if (projectPath != null)
        {
            try
            {
                using var project = new ProjectStore(projectPath);
                new TranslationTeam(project).Settings = ReadInlineTeamSettings(reviewDrafts: false);
                Status.Text = $"طُبّق وحُفظ قالب التوجيهات «{preset.Name}» لهذا المشروع.";
                return;
            }
            catch (Exception error) { Status.Text = error.Message; return; }
        }
        Status.Text = $"طُبّق قالب «{preset.Name}» في حقول التوجيهات.";
    }

    private void PreviewTeamPrompts(object sender, RoutedEventArgs e)
    {
        var settings = ReadInlineTeamSettings(reviewDrafts: false);
        string prompts = string.Join("\n\n", new[] { ("المشرف", "supervisor"), ("المترجم", "translator"), ("المراجع", "reviewer") }
            .Select(role => role.Item1 + "\n" + TranslationTeam.PromptPreview(role.Item2, settings)));
        var win = new Window
        {
            Owner = this, Title = "تعليمات النظام الكاملة", Width = 740, Height = 540,
            FlowDirection = FlowDirection.RightToLeft, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new Border
            {
                Margin = new Thickness(18),
                Child = new TextBox { Text = prompts, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
            }
        };
        ThemeManager.AttachWindow(win);
        win.ShowDialog();
    }

    private async void EstimateTeamBudget(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path) return;
        try
        {
            var estimate = await Task.Run(() => ReadLatestTeam(path, (team, plan) => team.EstimateResumeBudget(plan)));
            if (path != projectPath || openingProject || work != null || !IsLoaded) return;
            CharacterBudget.Text = estimate.Characters.ToString();
            RequestBudget.Text = estimate.Requests.ToString();
            UnlimitedBudget.IsChecked = false;
            UpdateBudgetCapBadge();
            PersistProfilesFile();
            Status.Text = "حُسبت الحدود التقديرية للاستئناف. يمكنك تعديلها أو تفعيل «بلا سقف».";
        }
        catch (Exception error) { if (path == projectPath) Status.Text = error.Message; }
    }

    private async void ReviewDraftsWithAi(object sender, RoutedEventArgs e) => await ReviewDraftsWithAiCore();

    private async Task ReviewDraftsWithAiCore()
    {
        TranslationMode.SelectedValue = "review-drafts";
        AutoApproveReviewed.IsChecked = true;
        Tabs.SelectedItem = TranslationPage;
        await StartUnifiedTranslationCore();
    }

    private async void ApproveAllDrafts(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path || work != null || !SaveEdits()) return;
        if (MessageBox.Show(this,
            "سيتم اعتماد جميع المسودات السليمة (الخالية من أخطاء الرموز والتعارضات) في المشروع دفعة واحدة. هل تريد المتابعة؟",
            "اعتماد جميع المسودات السليمة", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;
        await Run(async cancellation =>
        {
            int count = await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                using var project = new ProjectStore(path);
                return project.ApproveAllValidDrafts();
            }, cancellation);
            if (count > 0) SyncProjectApprovedToSharedMemoryBackground(path);
            Status.Text = count == 0
                ? "لا توجد مسودات سليمة جاهزة للاعتماد."
                : $"تم اعتماد {count:N0} مسودة سليمة دفعة واحدة.";
        });
    }

    private async void StartUnifiedTranslation(object sender, RoutedEventArgs e) => await StartUnifiedTranslationCore();

    private async Task StartUnifiedTranslationCore()
    {
        if (projectPath is not { } path || work != null || !SaveEdits()) return;
        ProviderOptions options;
        try { options = ReadProvider(); }
        catch (Exception error)
        {
            OpenConnectionSettings();
            Status.Text = error.Message;
            return;
        }

        string mode = TranslationMode.SelectedValue as string ?? "all-pending";
        bool reviewDrafts = mode == "review-drafts";
        bool editorScope = mode == "editor-scope";
        bool persistSharedPrefill = UseSharedMemoryPrefill?.IsChecked == true;
        bool useSharedPrefill = !reviewDrafts && persistSharedPrefill;
        bool approveReviewed = AutoApproveReviewed?.IsChecked == true;
        var settings = ReadInlineTeamSettings(reviewDrafts);
        var profiles = BuildProviderProfiles(options);
        string search = Search.Text, filter = ReviewStatus;

        TeamPlan? plan;
        SharedMemoryPrefillResult? prefillResult = null;
        try
        {
            (plan, prefillResult) = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                new ProjectSettings(project).UseSharedMemoryPrefill = persistSharedPrefill;
                var team = new TranslationTeam(project);
                List<Unit> rows = reviewDrafts
                    ? project.Units(limit: -1, status: "review")
                    : editorScope
                        ? project.Units(search, limit: -1, status: filter)
                        : project.Units(limit: -1, status: "pending");

                SharedMemoryPrefillResult? prefill = null;
                if (useSharedPrefill && rows.Any(r => r.Status == "pending"))
                {
                    using var tm = SharedTranslationMemory.OpenDefault();
                    prefill = tm.PrefillProject(project, rows, autoApprove: approveReviewed);
                    if (prefill.MatchedUnits > 0)
                    {
                        rows = editorScope
                            ? project.Units(search, limit: -1, status: filter)
                            : project.Units(limit: -1, status: "pending");
                    }
                }

                if (!reviewDrafts && !rows.Any(r => r.Status == "pending") && prefill is { MatchedUnits: > 0 })
                    return ((TeamPlan?)null, prefill);

                return ((TeamPlan?)team.Capture(rows, options, settings, profiles), prefill);
            });
        }
        catch (Exception error) { Status.Text = error.Message; return; }

        if (path != projectPath || openingProject || work != null || !IsLoaded) return;

        if (prefillResult is { MatchedUnits: > 0 })
        {
            _ = RefreshSharedMemoryBadgeAsync();
            string conflictNote = prefillResult.SkippedGlossaryConflicts > 0
                ? $" (تُرك {prefillResult.SkippedGlossaryConflicts:N0} سطر لاحترام المعجم اليدوي)"
                : "";
            string prefillMsg = $"الذاكرة المشتركة: عُبّئ {prefillResult.MatchedUnits:N0} نصاً متطابقاً 100% (توفير ~{prefillResult.SavedCharacters:N0} حرف){conflictNote}.";
            AppendLiveEvent(prefillMsg);
            Status.Text = prefillMsg;
        }

        if (plan == null)
        {
            await RefreshRows();
            UpdateOverview();
            return;
        }

        await RunTeamLive(path, plan, options, profiles, saveSettings: true,
            overrideWorkers: settings.Workers, overrideFastMode: settings.FastMode, overrideSettings: settings);
    }

    private async Task StartTargetedGlossaryReviewAsync(IReadOnlyList<Unit> violatingUnits)
    {
        if (projectPath is not { } path || work != null || violatingUnits.Count == 0 || !SaveEdits()) return;
        ProviderOptions options;
        try { options = ReadProvider(); }
        catch (Exception error)
        {
            OpenConnectionSettings();
            Status.Text = error.Message;
            return;
        }

        var baseSettings = ReadInlineTeamSettings(reviewDrafts: true) with
        {
            FastMode = false,
            SmartReview = false,
            ReviewDrafts = true,
            IncludeApproved = true
        };
        var profiles = BuildProviderProfiles(options);

        TeamPlan plan;
        try
        {
            plan = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                var freshIds = violatingUnits.Select(u => u.Id).ToHashSet(StringComparer.Ordinal);
                var freshRows = project.UnitsByIds(freshIds, limit: -1);
                return new TranslationTeam(project).Capture(freshRows, options, baseSettings, profiles);
            });
        }
        catch (Exception error) { Status.Text = error.Message; return; }

        if (path != projectPath || openingProject || work != null || !IsLoaded) return;
        SelectStage(TranslationPage);
        await RunTeamLive(path, plan, options, profiles, saveSettings: false,
            overrideWorkers: baseSettings.Workers, overrideFastMode: false, overrideSettings: baseSettings);
        if (IsLoaded && projectPath == path && work == null)
            await RunGlossaryAuditCoreAsync(autoFilterInEditor: ReviewStatus == "glossary");
    }

    private async void ResumeTeamQuick(object sender, RoutedEventArgs e) => await ResumeTeamQuickCore();

    private async Task ResumeTeamQuickCore()
    {
        if (projectPath is not { } path || work != null || !SaveEdits()) return;
        ProviderOptions options;
        try { options = ReadProvider(); }
        catch (Exception error)
        {
            OpenConnectionSettings();
            Status.Text = error.Message;
            return;
        }
        TeamPlan plan;
        try
        {
            plan = await Task.Run(() => ReadLatestTeam(path, (_, active) => active));
        }
        catch (Exception error) { Status.Text = error.Message; return; }
        if (path != projectPath || openingProject || work != null || !IsLoaded) return;
        Tabs.SelectedItem = TranslationPage;
        var currentSettings = ReadInlineTeamSettings(plan.Settings.ReviewDrafts);
        var profiles = BuildProviderProfiles(options);
        bool fastMode = SelectedFastMode() && !plan.Settings.ReviewDrafts;
        await RunTeamLive(path, plan, options, profiles, saveSettings: true,
            overrideWorkers: SelectedWorkers(), overrideFastMode: fastMode, overrideSettings: currentSettings);
    }

    private async void StartTeam(object sender, RoutedEventArgs e) => await StartUnifiedTranslationCore();
    private async void ResumeTeam(object sender, RoutedEventArgs e) => await ResumeTeamQuickCore();

    private void PrepareLiveAgentsGrid(TeamPlan plan, ProviderOptions options, IReadOnlyDictionary<string, ProviderOptions> profiles,
        TeamSettings activeSettings, int workers, bool fastMode)
    {
        liveAgents.Clear();
        agentByWorker.Clear();
        liveEvents.Clear();
        liveSamples.Clear();
        LiveSamplesEmptyText.Visibility = Visibility.Visible;
        if (NavTranslation != null) NavTranslation.Tag = "نشط ●";
        if (LoreBotBadge != null)
        {
            LoreBotBadge.Text = !fastMode && plan.UseSupervisor ? "يحلّل الآن ●" : "مسرد محفوظ";
            LoreBotBadge.SetResourceReference(TextBlock.ForegroundProperty, "AccentIndicator");
        }

        if (!fastMode && plan.UseSupervisor)
        {
            var supTarget = TranslationTeam.ResolveAssignment(activeSettings, options, profiles, 0, "supervisor");
            var sup = new AgentRow
            {
                WorkerId = 0, AgentName = "بوت الكشّاف", Role = "بناء العالم والمسرد",
                ModelLabel = FormatModelBadge(supTarget.Profile, supTarget.Model),
                Stage = "جاهز", Detail = "ينتظر تحليل مصطلحات اللعبة…"
            };
            liveAgents.Add(sup);
            agentByWorker[0] = sup;
        }
        string defaultRole = plan.Settings.ReviewDrafts ? "مدقق مسودات"
            : fastMode ? "بوت مترجم متسلسل"
            : activeSettings.SmartReview ? "بوت مشهدي + مدقق ذكي" : "مترجم + مدقق شامل";
        string initialRoleKey = plan.Settings.ReviewDrafts ? "reviewer" : "translator";
        for (int i = 1; i <= workers; i++)
        {
            var target = TranslationTeam.ResolveAssignment(activeSettings, options, profiles, i, initialRoleKey);
            var row = new AgentRow
            {
                WorkerId = i, AgentName = $"وكيل #{i}", Role = defaultRole,
                ModelLabel = FormatModelBadge(target.Profile, target.Model),
                Stage = "جاهز", Detail = "ينتظر استلام مسار مشهدي متصل…"
            };
            liveAgents.Add(row);
            agentByWorker[i] = row;
        }

        LiveMonitorTitle.Text = plan.Settings.ReviewDrafts
            ? "الوكلاء يراجعون المسودات الآن…"
            : fastMode ? "البوتات تعمل الآن (وضع سريع متسلسل)…"
            : activeSettings.SmartReview ? "البوتات الذكية المترابطة تعمل الآن…" : "فريق الترجمة والمراجعة الشاملة يعمل الآن…";
        LiveModeBadge.Text = plan.Settings.ReviewDrafts ? "مراجعة مسودات"
            : fastMode ? "وضع سريع"
            : activeSettings.SmartReview ? "بوتات ذكية مترابطة" : "مراجعة شاملة";
        LiveActiveAgentsText.Text = $"{workers} / {workers}";
        LiveProgressBar.Value = 0;
        LiveProgressText.Text = $"0 / {plan.Batches.Length:N0} دفعة (0.0%)";
        LiveEtaText.Text = "";
        LiveSpeedText.Text = "—";
        LiveTokensTotal.Text = "0";
        LiveTokensBreakdown.Text = "دخل: 0 · خرج: 0";
        LiveRequestsText.Text = "0 طلب";
        LiveSavedText.Text = "0";
        SavedTeamSummary.Text = "جارٍ تشغيل الوكلاء…";
        LiveNotesCountText.Text = "ملاحظات: 0";
        TeamNotesButton.Content = "ملاحظات الفريق";
        ActivityFeedExpander.IsExpanded = true;
    }

    private void OpenLiveLog(object sender, RoutedEventArgs e)
    {
        if (projectPath == null) return;
        string logFile = Path.Combine(projectPath, "team-live.log");
        if (!File.Exists(logFile))
        {
            Status.Text = "لم يُنشأ ملف السجل بعد — سيُنشأ تلقائياً عند بدء التشغيل.";
            return;
        }
        try { Process.Start(new ProcessStartInfo(logFile) { UseShellExecute = true }); }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void AppendLiveEvent(string text)
    {
        liveEvents.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {text}");
        while (liveEvents.Count > 60) liveEvents.RemoveAt(liveEvents.Count - 1);
    }

    private async Task RunTeamLive(string path, TeamPlan plan, ProviderOptions options,
        IReadOnlyDictionary<string, ProviderOptions> profiles, bool saveSettings,
        int overrideWorkers, bool overrideFastMode, TeamSettings overrideSettings)
    {
        string password = ZipPassword.Password;
        bool approveReviewed = AutoApproveReviewed.IsChecked == true;
        int workers = Math.Clamp(overrideWorkers, 1, Math.Max(1, Math.Min(16, plan.Batches.Length)));
        PrepareLiveAgentsGrid(plan, options, profiles, overrideSettings, workers, overrideFastMode);

        var clock = Stopwatch.StartNew();
        int initialSaved = -1;
        int initialCompleted = -1;

        var progress = new Progress<TeamProgress>(value =>
        {
            if (initialSaved < 0) { initialSaved = value.Saved; initialCompleted = value.Completed; }
            UpdateLiveTelemetry(value, workers, clock.Elapsed, initialSaved, initialCompleted);
        });

        bool finishedNormally = false;
        await Run(async cancellation =>
        {
            var result = await Task.Run(async () =>
            {
                using var project = new ProjectStore(path);
                var team = new TranslationTeam(project);
                if (saveSettings && !plan.Settings.ReviewDrafts)
                    team.Settings = overrideSettings with { ReviewDrafts = false };
                TeamProgress status = await team.Run(plan, options, progress, cancellation,
                    approveReviewed: approveReviewed, overrideWorkers: workers, overrideFastMode: overrideFastMode,
                    profiles: profiles, overrideSettings: overrideSettings);
                string? output = null;
                if (status.Saved > 0 && status.Completed >= status.Total)
                {
                    var service = new ProjectService(project);
                    var preview = service.Preview(password: password, cancellation: cancellation);
                    if (preview.ChangedFiles.Count > 0)
                    {
                        output = service.Build(Path.Combine(path, "outputs", "team-" + Guid.NewGuid().ToString("N")),
                            password: password, cancellation: cancellation, reviewed: preview, overlayOnly: true).Directory;
                        project.LastOutput = output;
                    }
                }
                return (status, output, notes: team.Notes(plan).Count);
            }, cancellation);

            finishedNormally = true;
            LiveMonitorTitle.Text = result.status.Completed >= result.status.Total
                ? "اكتمل التشغيل بنجاح"
                : "توقّف التشغيل مع حفظ جميع الدفعات المكتملة";
            LiveActiveAgentsText.Text = $"0 / {workers}";
            LiveNotesCountText.Text = $"ملاحظات التدقيق: {result.notes}";
            TeamNotesButton.Content = result.notes > 0 ? $"ملاحظات المدقق ({result.notes})" : "ملاحظات المدقق";
            TeamStatus.Text = FormatTeamStatus(result.status) + (result.notes > 0 ? $" · ملاحظات {result.notes}" : "");
            Status.Text = result.status.Completed >= result.status.Total
                ? $"اكتمل التشغيل — حُفظت {result.status.Saved:N0} ترجمة."
                : "توقّف التشغيل وحُفظت النتائج المكتملة؛ يمكنك الاستئناف في أي وقت.";
            if (result.output != null)
            {
                packagePath = result.output;
                BuildLog.Text = "معاينة الفريق جاهزة\n" + result.output;
            }
            if (approveReviewed && result.status.Saved > 0)
                SyncProjectApprovedToSharedMemoryBackground(path);
            _ = ReloadLoreAndGlossariesAsync();
        }, keepTranslationInteractive: true);

        if (!finishedNormally && IsLoaded && projectPath == path)
        {
            LiveMonitorTitle.Text = "توقّف التشغيل مع حفظ جميع الدفعات المكتملة";
            LiveActiveAgentsText.Text = $"0 / {workers}";
            foreach (var agent in liveAgents)
            {
                if (agent.Stage is not ("انتهى" or "مكتمل"))
                    agent.Stage = "متوقف";
            }
            _ = ReloadLoreAndGlossariesAsync();
        }
    }

    private void UpdateLiveTelemetry(TeamProgress value, int workers, TimeSpan elapsed, int initialSaved, int initialCompleted)
    {
        double percent = value.Total == 0 ? 0 : value.Completed * 100.0 / value.Total;
        LiveProgressBar.Value = percent;
        LiveProgressText.Text = $"{value.Completed:N0} / {value.Total:N0} دفعة ({percent:0.0}%)";

        var usage = value.Usage;
        LiveTokensTotal.Text = usage.TotalTokens > 0 ? usage.TotalTokens.ToString("N0") : $"{usage.Characters:N0} حرف";
        if (usage.TotalTokens > 0)
        {
            long thinking = Math.Max(0, usage.TotalTokens - usage.InputTokens - usage.OutputTokens);
            LiveTokensBreakdown.Text = thinking > 0
                ? $"دخل: {usage.InputTokens:N0} · خرج: {usage.OutputTokens:N0} · تفكير: {thinking:N0}"
                : $"دخل: {usage.InputTokens:N0} · خرج: {usage.OutputTokens:N0}";
        }
        else LiveTokensBreakdown.Text = "المزوّد لم يُرجع عدّاد توكنز — يُعرض عدد الأحرف";
        SavedTeamSummary.Text = FormatTeamStatus(value);

        LiveRequestsText.Text = $"{usage.Requests:N0} طلب";
        LiveSavedText.Text = value.Saved.ToString("N0");

        int sessionSaved = Math.Max(0, value.Saved - initialSaved);
        int sessionBatches = Math.Max(0, value.Completed - initialCompleted);
        if (elapsed.TotalSeconds >= 3 && sessionSaved > 0)
        {
            double rowsPerMin = sessionSaved / elapsed.TotalMinutes;
            LiveSpeedText.Text = $"{rowsPerMin:0} نص/د";
        }
        if (elapsed.TotalSeconds >= 3 && sessionBatches > 0 && value.Completed < value.Total)
        {
            double remainingMin = (value.Total - value.Completed) * (elapsed.TotalMinutes / sessionBatches);
            LiveEtaText.Text = remainingMin < 1 ? "المتبقي: أقل من دقيقة"
                : remainingMin < 60 ? $"المتبقي ~ {remainingMin:0} د"
                : $"المتبقي ~ {remainingMin / 60.0:0.0} س";
        }
        else if (value.Completed >= value.Total && value.Total > 0)
        {
            LiveEtaText.Text = "مكتمل";
        }

        if (value.Activity is { } act)
        {
            if (!agentByWorker.TryGetValue(act.WorkerId, out var row))
            {
                row = new AgentRow { WorkerId = act.WorkerId, AgentName = act.WorkerId == 0 ? "المشرف" : $"وكيل #{act.WorkerId}" };
                liveAgents.Add(row);
                agentByWorker[act.WorkerId] = row;
            }
            row.Role = act.Role;
            if (!string.IsNullOrWhiteSpace(act.Model) || !string.IsNullOrWhiteSpace(act.Profile))
                row.ModelLabel = FormatModelBadge(act.Profile, act.Model);
            row.Stage = act.Stage;
            row.BatchLabel = act.BatchId > 0 ? $"#{act.BatchId}" : "—";
            if (act.Stage == "مكتمل") row.CompletedCount += act.RowCount;
            row.Detail = act.Detail;

            int activeNow = liveAgents.Count(a => a.Stage is not ("انتهى" or "جاهز"));
            LiveActiveAgentsText.Text = $"{activeNow} / {workers}";
            if (act.WorkerId == 0 && LoreBotBadge != null)
            {
                bool scoutDone = act.Stage is "انتهى" or "مكتمل";
                LoreBotBadge.Text = scoutDone ? "جاهز ✓" : act.Stage;
                LoreBotBadge.SetResourceReference(TextBlock.ForegroundProperty, scoutDone ? "SuccessFg" : "AccentIndicator");
                if (LoreBotDetail != null) LoreBotDetail.Text = act.Detail;
                if (scoutDone) _ = ReloadLoreAndGlossariesAsync();
            }
            AppendLiveSample(act.Section, act.SampleSource, act.SampleTarget);
            string modelTag = string.IsNullOrWhiteSpace(row.ModelLabel) ? "" : $" [{row.ModelLabel}]";
            AppendLiveEvent($"{act.Role}{modelTag} ← {act.Detail}");
        }

        TeamStatus.Text = FormatTeamStatus(value);
    }

    private void UpdateIdleMonitor(TeamProgress? progress, int notesCount, TeamPlan? plan)
    {
        if (progress == null)
        {
            LiveMonitorTitle.Text = "لوحة مراقبة الوكلاء والتوكنز";
            LiveProgressBar.Value = 0;
            LiveProgressText.Text = "لا يوجد تشغيل محفوظ حالياً — اختر الإعدادات واضغط «ابدأ الترجمة الآن»";
            LiveEtaText.Text = "";
            LiveTokensTotal.Text = "0";
            LiveTokensBreakdown.Text = "دخل: 0 · خرج: 0";
            LiveSpeedText.Text = "—";
            LiveRequestsText.Text = "0 طلب";
            LiveSavedText.Text = "0";
            LiveNotesCountText.Text = "ملاحظات: 0";
            LiveActiveAgentsText.Text = "0";
            LiveModeBadge.Text = "في وضع الانتظار";
            ResumeTeamPrimary.Visibility = Visibility.Collapsed;
            TeamNotesButton.Content = "ملاحظات الفريق";
            return;
        }

        double percent = progress.Total == 0 ? 0 : progress.Completed * 100.0 / progress.Total;
        LiveMonitorTitle.Text = progress.Completed < progress.Total ? "تشغيل محفوظ جاهز للاستئناف" : "ملخّص آخر تشغيل مكتمل";
        LiveProgressBar.Value = percent;
        LiveProgressText.Text = $"{progress.Completed:N0} / {progress.Total:N0} دفعة ({percent:0.0}%)";
        LiveEtaText.Text = progress.Completed < progress.Total ? $"متبقي {progress.Total - progress.Completed:N0} دفعة" : "مكتمل";
        LiveTokensTotal.Text = progress.Usage.TotalTokens > 0
            ? progress.Usage.TotalTokens.ToString("N0")
            : $"{progress.Usage.Characters:N0} حرف";
        if (progress.Usage.TotalTokens > 0)
        {
            long thinking = Math.Max(0, progress.Usage.TotalTokens - progress.Usage.InputTokens - progress.Usage.OutputTokens);
            LiveTokensBreakdown.Text = thinking > 0
                ? $"دخل: {progress.Usage.InputTokens:N0} · خرج: {progress.Usage.OutputTokens:N0} · تفكير: {thinking:N0}"
                : $"دخل: {progress.Usage.InputTokens:N0} · خرج: {progress.Usage.OutputTokens:N0}";
        }
        else LiveTokensBreakdown.Text = "الاستهلاك السابق مسجّل بالأحرف";
        LiveRequestsText.Text = $"{progress.Usage.Requests:N0} طلب";
        LiveSavedText.Text = progress.Saved.ToString("N0");
        LiveNotesCountText.Text = $"ملاحظات: {notesCount}";
        LiveActiveAgentsText.Text = "0";
        LiveModeBadge.Text = plan == null ? "—"
            : plan.Settings.ReviewDrafts ? "مراجعة مسودات"
            : plan.Settings.FastMode ? "وضع سريع"
            : plan.Settings.SmartReview ? "بوتات ذكية مترابطة" : "مراجعة شاملة";
        TeamNotesButton.Content = notesCount > 0 ? $"ملاحظات الفريق ({notesCount})" : "ملاحظات الفريق";

        bool canResume = progress.Completed < progress.Total;
        ResumeTeamPrimary.Visibility = canResume ? Visibility.Visible : Visibility.Collapsed;
        StartTranslationPrimary.Content = canResume ? "بدء جلسة جديدة" : "تشغيل البوتات الآن";
        StartTranslationPrimary.Style = (Style)FindResource(canResume ? typeof(Button) : "PrimaryButtonStyle");
    }

    private static string FormatTeamStatus(TeamProgress value)
    {
        string tokenText = value.Usage.TotalTokens > 0
            ? $"{value.Usage.TotalTokens:N0} توكن"
            : $"{value.Usage.Characters:N0} حرفاً";
        return value.Section.Length > 0 && value.Activity != null
            ? $"الفريق — {value.Completed}/{value.Total} دفعة · {value.Saved} محفوظة · {tokenText}"
            : $"الفريق — {value.Completed}/{value.Total} دفعة · {value.Saved} ترجمة محفوظة · {value.Usage.Requests} طلباً / {tokenText}"
                + (value.Unknown > 0 ? $" · {value.Unknown} طلب منقطع يُستعاد عند الاستئناف"
                    : value.PauseReason.Length > 0 ? " · " + value.PauseReason
                    : value.Completed < value.Total ? " · جاهز للاستئناف." : "");
    }

    private async void ShowTeamNotes(object sender, RoutedEventArgs e)
    {
        string? path = projectPath;
        var snapshot = await WithLatestTeam((team, plan) => (Notes: team.Notes(plan), Progress: team.Summary(plan)));
        if (snapshot == null || path != projectPath || openingProject) return;
        var (notes, progress) = snapshot.Value;
        TeamStatus.Text = FormatTeamStatus(progress);
        if (notes.Count == 0) { Status.Text = "لا توجد ملاحظات محفوظة من الفريق."; return; }
        var changes = notes.Select(note => new ReviewChange(null, note.Source, note.Draft, note.Suggestion, note.Issue)).ToArray();
        var dialog = new ReviewExchangeWindow(changes, notes: true) { Title = "ملاحظات فريق الترجمة", Owner = this };
        if (dialog.ShowDialog() == true && dialog.SelectedIndex >= 0)
        {
            string id = notes[dialog.SelectedIndex].Id;
            if (id.Length == 0) { Status.Text = "تخص الملاحظة القسم بأكمله."; return; }
            await OpenEditorFilter("", id);
            if (Rows.Items.Count == 0) Status.Text = "هذا النص خارج المصدر الحالي.";
        }
    }

    private async void ApproveTeam(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path) return;
        await Run(async cancellation =>
        {
            int count = await Task.Run(() => ReadLatestTeam(path, (team, plan) => team.ApproveReady(plan)), cancellation);
            TeamStatus.Text = $"مسودات الفريق المعتمدة — {count}";
        });
    }

    private static T ReadLatestTeam<T>(string path, Func<TranslationTeam, TeamPlan, T> read)
    {
        using var project = new ProjectStore(path);
        var team = new TranslationTeam(project);
        var plan = team.Latest ?? throw new InvalidOperationException("لا يوجد تشغيل فريق محفوظ لهذا المصدر.");
        var projSettings = new ProjectSettings(project);
        if (plan.Package != projSettings.ScannedPackage || plan.Engine != projSettings.ScannedEngine)
            throw new InvalidOperationException("اختر الحزمة التي تخص تشغيل الفريق.");
        return read(team, plan);
    }

    private async Task<T?> WithLatestTeam<T>(Func<TranslationTeam, TeamPlan, T> read) where T : struct
    {
        if (projectPath is not { } path || !SaveEdits()) return null;
        try
        {
            return await Task.Run(() => ReadLatestTeam(path, read));
        }
        catch (Exception error) { if (projectPath == path) Status.Text = error.Message; return null; }
    }
}
