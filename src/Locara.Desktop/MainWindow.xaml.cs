using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Locara.Core;
using Microsoft.Win32;

namespace Locara.Desktop;
public partial class MainWindow:Window
{
    private string? projectPath;
    private string? packagePath;
    private CancellationTokenSource? work;
    private int offset;
    private bool recoveringTrial;
    private bool openingProject;
    private int editorRequest;
    private readonly SemaphoreSlim editorReads = new(1);
    private readonly CancellationTokenSource recoveryLifetime = new();
    private Dictionary<string,string> savedTargets = [];
    private string scannedEngine = "";
    private static readonly string UserData = LocaraPaths.UserDataDirectory;
    private readonly string recentPath = Path.Combine(UserData, "recent.json");
    private static readonly string WorkspaceSetting = Path.Combine(UserData, "workspace.txt");
    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        Provider.SelectionChanged += (_, _) =>
        {
            ModelPicker.Visibility = Selected(Provider) == "azure" ? Visibility.Collapsed : Visibility.Visible;
            CertificateOptions.Visibility = Selected(Provider) == "openai-compatible" ? Visibility.Visible : Visibility.Collapsed;
            JsonMode.Visibility = Selected(Provider) == "openai-compatible" ? Visibility.Visible : Visibility.Collapsed;
            if (loadingProfile) return;
            ApiKey.Clear();
            ApiUrl.Clear();
            Model.Text = "";
            Region.Clear();
            ResetModelCatalog();
            CertificateSha256.Clear();
            JsonMode.IsChecked = false;
            RefreshSavedKeyIndicator();
            RefreshProviderSummary();
        };
        ApiUrl.TextChanged += (_, _) =>
        {
            if (loadingProfile) return;
            ApiKey.Clear();
            CertificateSha256.Text = ReadSavedCertificate();
            ResetModelCatalog();
            RefreshSavedKeyIndicator();
        };
        CertificateSha256.TextChanged += (_, _) => { if (!loadingProfile) ResetModelCatalog(); };
        ApiKey.PasswordChanged += (_, _) => { if (!loadingProfile) ResetModelCatalog(); };
        LoadCustomPricing();
        LoadRecent();
        LoadProviderSettings();
        InitializeLiveMonitor();
        Loaded += async (_, _) => await RecoverTrial();
        Closed += (_, _) => recoveryLifetime.Cancel();
    }
    private static string Selected(ComboBox box)=>((ComboBoxItem)box.SelectedItem).Content.ToString()!;
    private sealed record RecentProject(string Path, string Name);
    private IEnumerable<string> RecentPaths => RecentProjects.Items.OfType<RecentProject>().Select(item => item.Path);
    private async void LoadRecent()
    {
        try
        {
            var recent = await Task.Run(() =>
            {
                if (!File.Exists(recentPath)) return new List<RecentProject>();
                var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(recentPath)) ?? [];
                return paths.Take(15).Select(path => new RecentProject(path, ProjectStore.ReadName(path))).ToList();
            });
            if (projectPath == null && !openingProject) RecentProjects.ItemsSource = recent;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { Status.Text = "تعذرت قراءة قائمة المشاريع الحديثة."; }
    }
    private async Task<bool> SetProject(string path)
    {
        if (openingProject || work != null || !SaveEdits()) return false;
        openingProject = true;
        Tabs.IsEnabled = false;
        Status.Text = "جارٍ فتح المشروع…";
        try
        {
            var opened = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                new ProjectService(project).EnsureCatalogSelected();
                var info = project.Info;
                var view = ReadEditorView(project, info.Source);
                return (project.DirectoryPath, Info: info, View: view, Page: ReadEditorPage(project, view),
                    Terms: project.Glossary().OrderBy(pair => pair.Key).ToList(),
                    TeamSettings: new TranslationTeam(project).Settings,
                    DisplayFont: project.Get("display_font"),
                    UnityWrap: project.Get("unity_arabic_wrap"),
                    UnityScale: project.Get("unity_arabic_font_scale"),
                    UnityShaping: project.Get("unity_arabic_shaping"),
                    UnityContrast: project.Get("unity_arabic_contrast"));
            });
            if (!IsLoaded) return false;

            projectPath = opened.DirectoryPath;
            editorRequest++;
            packagePath = null;
            capabilityKey = null;
            capability = null;
            SetTrialVisibility(false);
            ProjectTitle.Text = DashboardName.Text = opened.Info.Name;
            SourcePath.Text = opened.Info.Source;
            ScanSummary.Text = "جارٍ تحميل حالة المشروع…";
            Primary.IsEnabled = false;
            OpenLatest.IsEnabled = DashboardPreview.IsEnabled = false;
            PendingCount.Content = DraftCount.Content = IssueCount.Content = ApprovedCount.Content = "…";
            ApprovalPercent.Text = "";
            DraftsActionPanel.Visibility = Visibility.Collapsed;
            OverviewTeam.Text = SavedTeamSummary.Text = OutputSummary.Text = DeploymentSummary.Text = EngineSummary.Text = "";
            ApprovalProgress.Value = 0;
            SectionsPanel.Visibility = Visibility.Collapsed;
            liveAgents.Clear();
            agentByWorker.Clear();
            liveEvents.Clear();
            LoadTeamRoleSettings(opened.TeamSettings);
            LoadDisplayFontSelector(opened.DisplayFont, opened.UnityWrap, opened.UnityScale, opened.UnityShaping, opened.UnityContrast);
            RefreshDirectInstallStatus();
            RefreshScannedFiles();
            PackageChoices.ItemsSource = null;
            ZipPassword.Clear();
            ScanLog.Clear();
            BuildLog.Clear();
            TeamStatus.Text = "";
            Engine.SelectedItem = Engine.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Content, opened.Info.Engine))
                ?? Engine.Items[0];
            ApplyEditorView(opened.View);
            ApplyEditorPage(opened.Page, opened.View.Offset);
            TermSource.Clear();
            TermTarget.Clear();
            Terms.ItemsSource = opened.Terms;
            Overview.IsEnabled = FilesPage.IsEnabled = EditorPage.IsEnabled = TranslationPage.IsEnabled = PackagesPage.IsEnabled = true;
            Tabs.SelectedItem = Overview;
            Status.Text = "فُتح المشروع.";
            UpdateOverview();

            var recent = new[] { new RecentProject(opened.DirectoryPath, opened.Info.Name) }
                .Concat(RecentProjects.Items.OfType<RecentProject>())
                .DistinctBy(item => item.Path, StringComparer.OrdinalIgnoreCase).Take(15).ToList();
            RecentProjects.ItemsSource = recent;
            SaveRecent(recent);
            return true;
        }
        catch (Exception error) { Status.Text = error.Message; return false; }
        finally { openingProject = false; Tabs.IsEnabled = true; }
    }

    private async void SaveRecent(List<RecentProject> recent)
    {
        Volatile.Write(ref latestRecent, recent);
        try
        {
            await Task.Run(() =>
            {
                lock (recentLock)
                {
                    if (!ReferenceEquals(recent, Volatile.Read(ref latestRecent))) return;
                    Directory.CreateDirectory(UserData);
                    File.WriteAllText(recentPath, JsonSerializer.Serialize(recent.Select(item => item.Path)));
                }
            });
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Status.Text = "تعذّر حفظ قائمة المشاريع الأخيرة."; }
    }

    private readonly object recentLock = new();
    private List<RecentProject>? latestRecent;

    private sealed record EditorPageData(List<Unit> Rows, string Engine);
    private static EditorPageData ReadEditorPage(ProjectStore project, EditorView view) =>
        new(project.Units(view.Search, view.Offset, status: view.Filter), project.Get("scanned_engine"));

    private async Task RefreshRows(bool rememberView = false)
    {
        if (projectPath == null) return;
        string path = projectPath;
        var view = new EditorView(Search.Text, ReviewStatus, offset);
        int request = ++editorRequest;
        Rows.IsEnabled = false;
        PageInfo.Text = "جارٍ تحميل النصوص…";
        await editorReads.WaitAsync();
        try
        {
            if (request != editorRequest) return;
            var page = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                var data = ReadEditorPage(project, view);
                if (rememberView) project.Set(ViewKey(project), JsonSerializer.Serialize(view));
                return data;
            });
            if (request == editorRequest) ApplyEditorPage(page, view.Offset);
        }
        catch (Exception error)
        {
            if (request == editorRequest)
            {
                PageInfo.Text = "تعذّر تحميل النصوص.";
                Status.Text = error.Message;
            }
        }
        finally
        {
            if (request == editorRequest) Rows.IsEnabled = true;
            editorReads.Release();
        }
    }

    private void ApplyEditorPage(EditorPageData page, int pageOffset)
    {
        var rows = page.Rows;
        Rows.ItemsSource = rows;
        savedTargets = rows.ToDictionary(row => row.Id, row => row.Target);
        scannedEngine = page.Engine;
        Rows.IsEnabled = true;
        PageInfo.Text = rows.Count == 0 ? "لا توجد نصوص" : $"الصفوف {pageOffset + 1}–{pageOffset + rows.Count}";
    }

    private bool SaveEdits(Unit? selected = null, bool approve = false, bool approvePage = false, bool commit = true,
        IReadOnlySet<Unit>? approveUnits = null)
    {
        if (projectPath == null) return true;
        if (commit)
        {
            Rows.CommitEdit();
            Rows.CommitEdit();
        }
        var edits = new List<(Unit Unit, bool Approve)>();
        foreach (Unit row in Rows.Items)
        {
            bool approveRow = (row == selected && approve) || (approveUnits?.Contains(row) == true)
                || (approvePage && row.Status != "conflict"
                    && !string.IsNullOrWhiteSpace(row.Target) && Tokens.Validate(row.Source, row.Target, scannedEngine) == "");
            if (row == selected || approveRow || savedTargets.GetValueOrDefault(row.Id) != row.Target)
                edits.Add((row, approveRow));
        }
        if (edits.Count == 0) return true;
        try
        {
            using var project = new ProjectStore(projectPath, commandTimeout: 1);
            project.SavePage(edits);
            foreach (var edit in edits) savedTargets[edit.Unit.Id] = edit.Unit.Target;
            return true;
        }
        catch (EditConflictException error)
        {
            // Keep every draft on screen. Only this row is reconciled with the database.
            var draft = error.Draft;
            var current = error.Current;
            Rows.SelectedItem = draft;
            Rows.ScrollIntoView(draft);
            if (current == null)
            {
                if (MessageBox.Show(this, error.Message + "\nهل تريد إسقاط مسودة هذا السطر بعد نسخ ما تحتاجه؟",
                    "السطر لم يعد متاحاً", MessageBoxButton.YesNo) == MessageBoxResult.Yes)
                {
                    var rows = Rows.Items.Cast<Unit>().Where(row => row != draft).ToList();
                    Rows.ItemsSource = rows;
                    savedTargets.Remove(draft.Id);
                }
                return false;
            }
            string message = $"{current.Source}\nالمحفوظ: {current.Target}\nمسودتك: {draft.Target}\nنعم: احتفظ بمسودتك للمراجعة. لا: استخدم المحفوظ.";
            var choice = MessageBox.Show(this, message, "تعديل متزامن", MessageBoxButton.YesNoCancel);
            if (choice != MessageBoxResult.Cancel)
            {
                savedTargets[current.Id] = current.Target;
                if (choice == MessageBoxResult.Yes) current.Target = draft.Target;
                Rows.ItemsSource = Rows.Items.Cast<Unit>().Select(row => row == draft ? current : row).ToList();
                Rows.SelectedItem = current;
            }
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        return false;
    }
    private async Task Run(Func<CancellationToken, Task> action, bool keepTranslationInteractive = false)
    {
        if (work != null) { Status.Text = "انتظر اكتمال العملية أو أوقفها."; return; }
        if (!SaveEdits()) return;
        work = new();
        Busy.Visibility = Visibility.Visible;
        if (keepTranslationInteractive)
        {
            SetTranslationRunLock(running: true);
        }
        else
        {
            Tabs.IsEnabled = false;
        }
        string previousStatus = Status.Text;
        try
        {
            await action(work.Token);
            if (Status.Text == previousStatus || string.IsNullOrWhiteSpace(Status.Text))
                Status.Text = "اكتملت العملية";
        }
        catch (OperationCanceledException)
        {
            Status.Text = "توقّفت العملية مع حفظ كل ما أُنجز.";
        }
        catch (Exception ex)
        {
            Status.Text = ex.Message;
            MessageBox.Show(this, ex.Message, "Locara", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            work.Dispose();
            work = null;
            Busy.Visibility = Visibility.Collapsed;
            if (keepTranslationInteractive) SetTranslationRunLock(running: false);
            else Tabs.IsEnabled = true;
            await RefreshRows();
            UpdateOverview();
        }
    }

    private void SetTranslationRunLock(bool running)
    {
        TranslationControlsPanel.IsEnabled = !running;
        ConnectionPanel.IsEnabled = !running;
        RoutingPanel.IsEnabled = !running;
        RolesPanel.IsEnabled = !running;
        BudgetPanel.IsEnabled = !running;
        GlossaryPanel.IsEnabled = !running;
        HeaderNewButton.IsEnabled = !running;
        HeaderOpenButton.IsEnabled = !running;
        StopTranslationButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        foreach (TabItem tab in Tabs.Items)
        {
            if (tab != TranslationPage) tab.IsEnabled = !running && (tab == Tabs.Items[0] || projectPath != null);
        }
    }
    private async void CreateProject(object sender,RoutedEventArgs e)
    {
        if (work != null || openingProject) return;
        var dest=new SaveFileDialog{Title="اختر اسم ومكان مجلد المشروع الجديد",Filter="Locara project|*.locara",FileName="مشروع جديد.locara"};
        if(dest.ShowDialog(this)!=true)return;
        try
        {
            string path = dest.FileName;
            bool created = false;
            await Run(async cancellation =>
            {
                await Task.Run(() =>
                {
                    using var project = ProjectStore.Create(path, Path.GetFileNameWithoutExtension(path));
                }, cancellation);
                created = true;
            });
            if (created) await SetProject(path);
        }
        catch(Exception ex){Status.Text=ex.Message;}
    }
    private async void OpenProject(object sender, RoutedEventArgs e)
    {
        if (work != null || openingProject) return;
        var dialog = new OpenFolderDialog { Title = "اختر مجلد مشروع Locara" };
        if (dialog.ShowDialog(this) == true) await SetProject(dialog.FolderName);
    }

    private async void OpenRecent(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (RecentProjects.SelectedItem is RecentProject item) await SetProject(item.Path);
    }
    private static string Workspace() => File.Exists(WorkspaceSetting)
        ? File.ReadAllText(WorkspaceSetting).Trim()
        : Path.Combine(UserData, "Projects");

    private void ChooseWorkspace(object sender, RoutedEventArgs e)
    {
        if (work != null) return;
        try
        {
            string current = Workspace();
            var dialog = new OpenFolderDialog { Title = "اختر مكان حفظ المشاريع الجديدة" };
            if (Directory.Exists(current)) dialog.InitialDirectory = current;
            if (dialog.ShowDialog(this) != true) return;
            Directory.CreateDirectory(UserData);
            File.WriteAllText(WorkspaceSetting, dialog.FolderName);
            Status.Text = "مجلد المشاريع الجديدة: " + dialog.FolderName;
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void StartGame(object sender, RoutedEventArgs e)
    {
        if (work != null) return;
        var dialog = new OpenFolderDialog { Title = "اختر مجلد اللعبة" };
        if (dialog.ShowDialog(this) == true) await StartSource(dialog.FolderName);
    }

    private async void StartFile(object sender, RoutedEventArgs e)
    {
        if (work != null) return;
        var dialog = new OpenFileDialog { Title = "اختر ملف اللغة أو الحزمة" };
        if (dialog.ShowDialog(this) == true) await StartSource(dialog.FileName);
    }

    private async Task StartSource(string source)
    {
        if (openingProject || work != null || !SaveEdits()) return;
        var recent = RecentPaths.ToArray();
        openingProject = true;
        Tabs.IsEnabled = false;
        Status.Text = "جارٍ تجهيز مصدر المشروع…";
        (string Path, bool Created) prepared;
        try
        {
            prepared = await Task.Run(() => PrepareSource(source, recent));
        }
        catch (Exception error) { Status.Text = error.Message; return; }
        finally { openingProject = false; Tabs.IsEnabled = true; }
        if (!IsLoaded || !await SetProject(prepared.Path)) return;
        if (prepared.Created) await SelectLanguage(showWhenUnavailable: false);
        await ScanCurrent(openEditor: true);
    }

    private static (string Path, bool Created) PrepareSource(string source, string[] recent)
    {
        source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
        string name = Path.GetFileName(source);
        string key = Identity.Hash(Encoding.UTF8.GetBytes(source.ToUpperInvariant()));
        string destination = Path.Combine(Workspace(), key + ".locara");
        foreach (string candidate in recent.Prepend(destination).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(candidate, "project.db"))) continue;
            using var existing = new ProjectStore(candidate);
            if (MatchesSource(existing, source)) return (candidate, false);
        }
        if (File.Exists(Path.Combine(destination, "project.db")))
            throw new InvalidOperationException("مجلد المشروع مرتبط بمصدر مختلف. انقل المشروع أو اختر مجلد مشاريع آخر.");
        using var project = ProjectStore.Create(destination, name.Length == 0 ? "مشروع تعريب" : name, source);
        project.Set("automatic_output", "true");
        return (destination, true);
    }

    private void SaveRowAfterEdit(object sender, DataGridRowEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not Unit row) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (work == null && Rows.Items.Contains(row) && savedTargets.GetValueOrDefault(row.Id) != row.Target)
                SaveEdits(commit: false);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private async void ChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog();
        if (dialog.ShowDialog(this) == true && await ChangeSource(dialog.FolderName)) await LoadPackages();
    }
    private async void ChooseFile(object sender,RoutedEventArgs e){var d=new OpenFileDialog{Filter="Resources|*.json;*.csv;*.tsv;*.ini;*.xml;*.yaml;*.yml;*.po;*.pot;*.xlsx;*.xlf;*.xliff;*.rpy;*.rpym;*.bundle;*.zip;*.pck|All files|*.*"};if(d.ShowDialog()==true)await ChangeSource(d.FileName);}
    private async Task<bool> ChangeSource(string source)
    {
        if (projectPath is not { } path) { Status.Text = "أنشئ مشروعاً أولاً."; return false; }
        string current = SourcePath.Text;
        bool relocated = false;
        if (current.Length > 0 && !StringComparer.OrdinalIgnoreCase.Equals(current, source))
        {
            var choice = MessageBox.Show(this,
                "هل نقلت اللعبة نفسها إلى هذا المسار؟\nنعم: احتفظ بالترجمات المرتبطة بها.\nلا: افتح المصدر بهوية مستقلة.",
                "اختيار مصدر", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel) return false;
            relocated = choice == MessageBoxResult.Yes;
        }
        bool changed = false;
        await Run(async cancellation =>
        {
            await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                if (!StringComparer.OrdinalIgnoreCase.Equals(current, project.Info.Source))
                    throw new InvalidOperationException("تغيّر مصدر المشروع. افتحه مجدداً قبل اختيار مصدر آخر.");
                project.ChangeSource(source, relocated);
            }, cancellation);
            packagePath = null;
            PackageChoices.ItemsSource = null;
            SourcePath.Text = source;
            ScanLog.Clear();
            RefreshScannedFiles();
            Search.Clear();
            offset = 0;
            changed = true;
        });
        return changed;
    }
    private sealed record PackageChoice(string? Path, string Label);

    private async void FindPackages(object sender, RoutedEventArgs e) => await LoadPackages();

    private async Task LoadPackages()
    {
        if (projectPath == null || !Directory.Exists(SourcePath.Text))
        {
            Status.Text = "اختر مجلد اللعبة لعرض الحزم الموجودة فيه.";
            return;
        }
        string path = projectPath, password = ZipPassword.Password;
        await Run(async cancellation =>
        {
            var result = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                var service = new ProjectService(project);
                return (Packages: service.DiscoverPackages(password, cancellation), Selected: service.SelectedPackage);
            }, cancellation);
            var choices = new List<PackageChoice>();
            bool hasGameCatalog = result.Packages.Any(p => p.Path.EndsWith("/Strings/en.xml", StringComparison.OrdinalIgnoreCase));
            if (!hasGameCatalog)
                choices.Add(new(null, "الملفات الموجودة مباشرة في المجلد"));
            choices.AddRange(result.Packages.Select(package => new PackageChoice(package.Path, package.Path + " — " + package.Status)));
            PackageChoices.ItemsSource = choices;
            PackageChoices.SelectedItem = choices.FirstOrDefault(choice => StringComparer.OrdinalIgnoreCase.Equals(choice.Path, result.Selected))
                ?? choices.FirstOrDefault();
            ScanLog.Text = result.Packages.Count == 0 ? "لا توجد حزم ZIP أو PCK في المجلد."
                : "اختر حزمة واضغط «استخدام المحدد»، ثم افحص الملفات.";
        });
    }

    private async void UsePackage(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path || PackageChoices.SelectedItem is not PackageChoice choice) return;
        await Run(async cancellation =>
        {
            await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                new ProjectService(project).SelectPackage(choice.Path);
            }, cancellation);
            packagePath = null;
            offset = 0;
            ScanLog.Text = "المصدر المحدد — " + (choice.Path ?? "ملفات المجلد") + "\nاضغط «فحص الملفات».";
            RefreshScannedFiles();
        });
    }

    private async void ConfigureExtraction(object sender, RoutedEventArgs e)
    {
        if (projectPath == null) return;
        string path = projectPath, password = ZipPassword.Password;
        IReadOnlyList<string> resources = [];
        await Run(async cancellation => resources = await Task.Run(() =>
        {
            using var project = new ProjectStore(path);
            return new ProjectService(project).ListConfigurableResources(password, cancellation);
        }, cancellation));
        if (resources.Count == 0) { Status.Text = "لا توجد موارد تدعم ضبط الاستخراج في المصدر المحدد."; return; }
        var dialog = new ExtractionWindow(resources, (resource, cancellation) =>
        {
            using var project = new ProjectStore(path);
            return new ProjectService(project).InspectResource(resource, password, cancellation);
        }) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try
        {
            using var project = new ProjectStore(path);
            new ProjectService(project).ConfigureResource(dialog.Resource, dialog.Rule);
            ScanLog.Text = "حُفظ إعداد الاستخراج. أعد فحص المصدر لتطبيقه.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void Scan(object sender, RoutedEventArgs e) => await ScanCurrent();

    private async Task ScanCurrent(bool openEditor = false)
    {
        if (projectPath == null) return;
        if (string.IsNullOrWhiteSpace(SourcePath.Text))
        {
            Status.Text = "اختر مصدراً للفحص أولاً.";
            return;
        }
        string path = projectPath, password = ZipPassword.Password, engine = Selected(Engine);
        await Run(async cancellation =>
        {
            var result = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                project.Set("engine", engine);
                var service = new ProjectService(project);
                var scan = service.Scan(password, cancellation);
                return (Scan: scan, Capability: service.Capabilities(password, cancellation),
                    Version: project.Get("scan_version"), View: openEditor ? ReadEditorView(project, project.Info.Source) : null);
            }, cancellation);
            var scan = result.Scan;
            SetCapabilities(result.Capability, result.Version);
            ScanLog.Text = (scan.Package.Length == 0 ? "" : scan.Package + "\n")
                + $"{scan.Files} ملفات / {scan.Units} نصوص\n"
                + DescribeCapability(result.Capability) + "\n"
                + string.Join('\n', scan.Diagnostics.Select(item => item.Path + " — " + item.Message));
            ScanLogExpander.IsExpanded = scan.Diagnostics.Count > 0 || scan.Units == 0;
            RefreshScannedFiles();
            if (openEditor && scan.Units > 0)
            {
                ApplyEditorView(result.View!);
                Tabs.SelectedItem = File.Exists(SourcePath.Text) && result.Capability.Launch == GameLaunch.None ? EditorPage : Overview;
            }
            else if (openEditor && scan.Files > 0 && scan.Units == 0)
            {
                Tabs.SelectedItem = FilesPage;
            }
        });
    }
    private async void SearchRows(object sender,RoutedEventArgs e){if (!SaveEdits()) return;offset=0;await RefreshRows(rememberView: true);}
    private async void Previous(object sender,RoutedEventArgs e){if (!Rows.IsEnabled || !SaveEdits()) return;offset=Math.Max(0,offset-100);await RefreshRows(rememberView: true);}
    private async void Next(object sender,RoutedEventArgs e){if(Rows.IsEnabled && Rows.Items.Count==100 && SaveEdits()){offset+=100;await RefreshRows(rememberView: true);}}
    private void SaveSelected(object sender,RoutedEventArgs e)
    {
        if (Rows.IsEnabled && Rows.SelectedItem is Unit row && SaveEdits(row)) Status.Text = "حُفظت الترجمة";
    }
    private void ApproveSelected(object sender, RoutedEventArgs e)
    {
        var selected = Rows.SelectedItems.OfType<Unit>().ToHashSet();
        if (!Rows.IsEnabled || projectPath == null || selected.Count == 0) return;
        if (SaveEdits(approveUnits: selected)) Status.Text = $"النصوص المعتمدة — {selected.Count}";
    }
    private void ApprovePage(object sender,RoutedEventArgs e)
    {
        if (Rows.IsEnabled && SaveEdits(approvePage: true)) Status.Text = "حُفظت تعديلات الصفحة واعتُمدت النصوص السليمة";
    }
    private async Task Build(bool final)
    {
        packagePath = null;
        if (projectPath == null || !SaveEdits()) return;
        if (!await EnsureOriginalsBeforeActionAsync(final ? "بناء حزمة نهائية" : "بناء حزمة تجربة")) return;
        string path = projectPath, password = ZipPassword.Password;
        await Run(async cancellation =>
        {
            var plan = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                return new ProjectService(project).Preview(final, password, cancellation);
            }, cancellation);
            int eligible = plan.Resources.Sum(resource => resource.Translations);
            BuildLog.Text = $"النصوص المؤهلة — {eligible}\nالملفات التي ستتغير — {plan.ChangedFiles.Count}";
            if (plan.ChangedFiles.Count > 0) BuildLog.Text += "\n" + string.Join('\n', plan.ChangedFiles);
            if (plan.Notice.Length > 0) BuildLog.Text += "\n" + plan.Notice;
            if (plan.Blocked > 0) BuildLog.Text += $"\nالنصوص المستبعدة من البناء — {plan.Blocked}";
            if (plan.ChangedFiles.Count == 0)
            {
                BuildLog.Text += eligible == 0 ? "\nلا توجد ترجمات مؤهلة لهذا البناء." : "\nتطابق الترجمات المؤهلة محتوى الملفات الحالي.";
                return;
            }
            string destination;
            using (var project = new ProjectStore(path))
            {
                destination = project.Get("automatic_output") == "true"
                    ? Path.Combine(path, "outputs", (final ? "final-" : "preview-") + Guid.NewGuid().ToString("N"))
                    : "";
            }
            var review = new BuildReviewWindow(plan, destination.Length > 0) { Owner = this };
            if (review.ShowDialog() != true) throw new OperationCanceledException();
            if (destination.Length == 0)
            {
                var dialog = new SaveFileDialog
                {
                    Title = "اختر اسم مجلد مخرجات جديد", Filter = "Output folder|*.output", FileName = "ترجمة.output"
                };
                if (dialog.ShowDialog(this) != true) throw new OperationCanceledException();
                destination = dialog.FileName;
            }
            var result = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                var build = new ProjectService(project).Build(destination, final, password, cancellation, plan);
                project.LastOutput = build.Directory;
                return build;
            }, cancellation);
            packagePath = result.Directory;
            BuildLog.Text = $"{result.Translations} ترجمة / {result.Files} ملفات\n{result.Directory}";
            if (plan.Notice.Length > 0) BuildLog.Text += "\n" + plan.Notice;
        });
    }
    private void OpenOutput(object sender, RoutedEventArgs e)
    {
        if (packagePath == null || !Directory.Exists(packagePath))
        {
            Status.Text = "ابنِ حزمة أولاً.";
            return;
        }
        try { Process.Start(new ProcessStartInfo(packagePath) { UseShellExecute = true }); }
        catch (Exception error) { Status.Text = error.Message; }
    }
    private async void BuildPreview(object sender,RoutedEventArgs e)=>await Build(false);
    private async void BuildFinal(object sender,RoutedEventArgs e)=>await Build(true);
    private async void ExportXUnity(object sender, RoutedEventArgs e)
    {
        if (projectPath == null) return;
        string path = projectPath;
        await Run(async cancellation =>
        {
            Status.Text = "جارٍ تصدير حزمة XUnity.AutoTranslator…";
            string destination = Path.Combine(path, "outputs", "xunity-" + Guid.NewGuid().ToString("N")[..8]);
            var result = await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                using var project = new ProjectStore(path);
                var exported = new ProjectService(project).ExportXUnityAutoTranslator(destination, final: false);
                project.LastOutput = exported.OutputDirectory;
                return exported;
            }, cancellation);
            packagePath = result.OutputDirectory;
            string shapingText = result.ShapedArabic ? "تشكيل مسبق (Visual RTL)" : "عربي منطقي (UTF-8)";
            BuildLog.Text = $"تم تصدير {result.ExportedCount} نصاً بصيغة XUnity.AutoTranslator (BepInEx)\n" +
                $"التشكيل: {shapingText} · الخط المرفق: {result.FontFileName}\n" +
                $"المجلد: {result.OutputDirectory}";
        });
    }
    private async void RetryTrialRecovery(object sender, RoutedEventArgs e) => await RecoverTrial();

    private async Task RecoverTrial()
    {
        if (recoveringTrial)
        {
            Status.Text = "تجري متابعة تجربة Steam السابقة.";
            return;
        }
        recoveringTrial = true;
        var token = recoveryLifetime.Token;
        var progress = new Progress<string>(message => TrialRecoveryStatus.Text = message);
        try
        {
            await Task.Run(async () =>
            {
                await StarTruckerTrial.RecoverPending(progress, token);
                await SteamGameTrial.RecoverPending(progress, token);
            }, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { TrialRecoveryStatus.Text = error.Message; }
        finally { recoveringTrial = false; }
    }

    private async void Install(object sender,RoutedEventArgs e)
    {
        if (projectPath == null) return;
        string path = projectPath;
        var output = new OpenFolderDialog { Title = "اختر حزمة الترجمة التي تريد تثبيتها" };
        if (packagePath != null && Directory.Exists(packagePath)) output.InitialDirectory = packagePath;
        if (output.ShowDialog(this) != true) return;
        string package = output.FolderName;
        var copy = new OpenFolderDialog { Title = "اختر مجلد نسخة اللعبة التي ستُعدّل" };
        if (copy.ShowDialog(this) != true) return;
        string manifest = Path.Combine(package, "manifest.json");
        string kind = "نوع الحزمة غير محدّد";
        try
        {
            if (File.Exists(manifest))
            {
                using var stream = File.OpenRead(manifest);
                using var metadata = JsonDocument.Parse(stream);
                if (metadata.RootElement.TryGetProperty("mode", out var mode))
                    kind = mode.GetString() switch { "final" => "حزمة نهائية", "preview" => "حزمة تجربة", _ => kind };
            }
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            Status.Text = "تعذّرت قراءة بيانات الحزمة — " + error.Message;
            return;
        }
        string message = $"{kind}\n{package}\n\nستُثبّت الترجمة على نسخة اللعبة في\n{copy.FolderName}";
        if (MessageBox.Show(this, message, "تأكيد تثبيت الترجمة", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await Run(async ct =>
        {
            var result = await Task.Run(() => { using var p = new ProjectStore(path); return PackageInstaller.Install(p, package, copy.FolderName, ct); }, ct);
            BuildLog.Text = $"ثُبّتت الترجمة في {result.Files} ملفات. مجلد الاستعادة:\n{result.ReceiptDirectory}";
        });
    }
    private async void Restore(object sender,RoutedEventArgs e)
    {
        var receipt = new OpenFolderDialog { Title = "اختر مجلد الاستعادة الناتج عن التثبيت" };
        if (receipt.ShowDialog(this) != true) return;
        await Run(async ct =>
        {
            int restored = await Task.Run(() => PackageInstaller.Restore(receipt.FolderName, ct), ct);
            BuildLog.Text = restored == 0 ? "الملفات مطابقة للنسخة الأصلية." : $"استُعيدت النسخة الأصلية لـ {restored} ملفات.";
        });
    }
    private void CancelWork(object sender,RoutedEventArgs e)=>work?.Cancel();
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e){if(work!=null){work.Cancel();e.Cancel=true;Status.Text="جارٍ إلغاء العملية؛ أعد إغلاق التطبيق بعد اكتمالها.";}else if (!SaveEdits()) e.Cancel = true;base.OnClosing(e);}
}
