using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Locara.Core;
using Microsoft.Win32;

namespace Locara.Desktop;

public partial class MainWindow
{
    private static string ProviderSettingsPath => Path.Combine(UserData, "provider.json");
    private static string ProviderProfilesPath => Path.Combine(UserData, "provider-profiles.json");

    private sealed record ProviderSettings(string Provider, string Model, string? BaseUrl, string Region,
        int MaxCharacters, int MaxRequests, string? CertificateSha256 = null, bool JsonMode = false);

    private sealed record SavedProviderProfile(string Id, string Name, string Provider, string Model, string? BaseUrl,
        string Region, string? CertificateSha256 = null, bool JsonMode = false, string[]? CachedModels = null);

    private sealed record ProviderProfilesFile(string ActiveProfileId, int MaxCharacters, int MaxRequests,
        List<SavedProviderProfile> Profiles);

    private bool loadingProfile;
    private List<SavedProviderProfile> savedProfiles = [];
    private string activeProfileId = "default";

    private SavedProviderProfile ActiveProfile =>
        savedProfiles.FirstOrDefault(p => p.Id == activeProfileId) ?? savedProfiles[0];

    private void LoadProviderSettings()
    {
        try
        {
            ProviderProfilesFile? loaded = null;
            if (File.Exists(ProviderProfilesPath))
            {
                loaded = JsonSerializer.Deserialize<ProviderProfilesFile>(File.ReadAllText(ProviderProfilesPath));
            }
            else if (File.Exists(ProviderSettingsPath))
            {
                var legacy = JsonSerializer.Deserialize<ProviderSettings>(File.ReadAllText(ProviderSettingsPath));
                if (legacy != null)
                {
                    loaded = new ProviderProfilesFile("default", legacy.MaxCharacters, legacy.MaxRequests,
                    [
                        new SavedProviderProfile("default", "الافتراضي", legacy.Provider, legacy.Model,
                            legacy.BaseUrl, legacy.Region, legacy.CertificateSha256, legacy.JsonMode)
                    ]);
                }
            }

            if (loaded is { Profiles.Count: > 0 })
            {
                savedProfiles = loaded.Profiles;
                activeProfileId = savedProfiles.Any(p => p.Id == loaded.ActiveProfileId)
                    ? loaded.ActiveProfileId : savedProfiles[0].Id;
                CharacterBudget.Text = (loaded.MaxCharacters > 0 ? loaded.MaxCharacters : 500000).ToString();
                RequestBudget.Text = (loaded.MaxRequests > 0 ? loaded.MaxRequests : 500).ToString();
            }
            else
            {
                savedProfiles = [new SavedProviderProfile("default", "الافتراضي", "openai-compatible", "", null, "")];
                activeProfileId = "default";
            }

            BindProfileSelector();
            ApplyProfileToUi(ActiveProfile);
            RefreshRoutingOptions();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            savedProfiles = [new SavedProviderProfile("default", "الافتراضي", "openai-compatible", "", null, "")];
            activeProfileId = "default";
            BindProfileSelector();
            ApplyProfileToUi(ActiveProfile);
            RefreshRoutingOptions();
            Status.Text = "تعذّرت قراءة إعدادات المزوّد. " + error.Message;
        }
    }

    private void BindProfileSelector()
    {
        loadingProfile = true;
        try
        {
            ProfileSelector.ItemsSource = null;
            ProfileSelector.ItemsSource = savedProfiles.ToList();
            ProfileSelector.SelectedItem = savedProfiles.FirstOrDefault(p => p.Id == activeProfileId) ?? savedProfiles[0];
            DeleteProfileButton.IsEnabled = savedProfiles.Count > 1;
        }
        finally { loadingProfile = false; }
    }

    private void ApplyProfileToUi(SavedProviderProfile profile)
    {
        loadingProfile = true;
        try
        {
            activeProfileId = profile.Id;
            ProfileNameInput.Text = profile.Name;
            foreach (ComboBoxItem item in Provider.Items)
                if (item.Content?.ToString() == profile.Provider) Provider.SelectedItem = item;
            ApiUrl.Text = profile.BaseUrl ?? "";
            string cert = !string.IsNullOrWhiteSpace(profile.CertificateSha256) ? profile.CertificateSha256 : ReadSavedCertificate();
            CertificateSha256.Text = cert;
            CertificateOptions.IsExpanded = cert.Length > 0;
            JsonMode.IsChecked = profile.JsonMode;
            Region.Text = profile.Region;
            ApiKey.Clear();
            Model.ItemsSource = profile.CachedModels is { Length: > 0 } ? profile.CachedModels : null;
            Model.Text = profile.Model;
            if (profile.CachedModels is { Length: > 0 })
            {
                ModelListStatus.Text = $"النماذج المحفوظة لهذا الاتصال · {profile.CachedModels.Length}";
                ModelListStatus.Visibility = Visibility.Visible;
            }
            else
            {
                ModelListStatus.Visibility = Visibility.Collapsed;
            }
            DeleteProfileButton.IsEnabled = savedProfiles.Count > 1;
            RefreshSavedKeyIndicator();
        }
        finally { loadingProfile = false; }
        RefreshProviderSummary();
    }

    private void SelectProviderProfile(object sender, SelectionChangedEventArgs e)
    {
        if (loadingProfile || ProfileSelector.SelectedItem is not SavedProviderProfile selected) return;
        ApplyProfileToUi(selected);
        PersistProfilesFile();
        RefreshRoutingOptions();
        Status.Text = $"تم التبديل إلى اتصال «{selected.Name}».";
    }

    private void AddProviderProfile(object sender, RoutedEventArgs e)
    {
        int n = savedProfiles.Count + 1;
        string name = $"اتصال {n}";
        while (savedProfiles.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name = $"اتصال {++n}";
        var created = new SavedProviderProfile(Guid.NewGuid().ToString("N"), name, "openai-compatible", "", null, "");
        savedProfiles.Add(created);
        activeProfileId = created.Id;
        BindProfileSelector();
        ApplyProfileToUi(created);
        RefreshRoutingOptions();
        ProfileNameInput.Focus();
        ProfileNameInput.SelectAll();
        Status.Text = "أدخل اسم الاتصال الجديد وبياناته ثم اضغط «حفظ ملف الاتصال».";
    }

    private void DeleteProviderProfile(object sender, RoutedEventArgs e)
    {
        if (savedProfiles.Count <= 1)
        {
            Status.Text = "يجب الإبقاء على ملف اتصال واحد على الأقل.";
            return;
        }
        var current = ActiveProfile;
        if (MessageBox.Show(this, $"هل تريد حذف ملف الاتصال «{current.Name}» ومفتاحه المحفوظ؟",
            "حذف ملف الاتصال", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            try
            {
                var opts = new ProviderOptions(current.Provider, current.Model, "", current.BaseUrl, current.Region,
                    CertificateSha256: current.CertificateSha256, JsonMode: current.JsonMode);
                ProviderCredentials.Delete(opts, current.Id, deleteLegacy: false);
            }
            catch { }
            savedProfiles.RemoveAll(p => p.Id == current.Id);
            activeProfileId = savedProfiles[0].Id;
            PersistProfilesFile();
            BindProfileSelector();
            ApplyProfileToUi(ActiveProfile);
            RefreshRoutingOptions();
            Status.Text = $"تم حذف ملف الاتصال «{current.Name}».";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void PersistProfilesFile()
    {
        try
        {
            int cBudget = int.TryParse(CharacterBudget.Text, out int c) && c > 0 ? c : 500000;
            int rBudget = int.TryParse(RequestBudget.Text, out int r) && r > 0 ? r : 500;
            var file = new ProviderProfilesFile(activeProfileId, cBudget, rBudget, savedProfiles);
            WriteProviderSetting(ProviderProfilesPath, JsonSerializer.Serialize(file));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private Dictionary<string, ProviderOptions> BuildProviderProfiles(ProviderOptions primary)
    {
        var map = new Dictionary<string, ProviderOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in savedProfiles)
        {
            if (profile.Id == activeProfileId)
            {
                string activeName = string.IsNullOrWhiteSpace(ProfileNameInput.Text) ? profile.Name : ProfileNameInput.Text.Trim();
                map[activeName] = primary;
                if (!activeName.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                    map[profile.Name] = primary;
                continue;
            }
            var raw = new ProviderOptions(profile.Provider, profile.Model, "", profile.BaseUrl, profile.Region,
                BatchSize: primary.BatchSize, MaxCharacters: primary.MaxCharacters, MaxRequests: primary.MaxRequests,
                CertificateSha256: profile.CertificateSha256, JsonMode: profile.JsonMode);
            string key = "";
            try { key = ProviderCredentials.Read(raw, profile.Id); } catch { }
            map[profile.Name] = raw with { ApiKey = key };
        }
        return map;
    }

    private IReadOnlyList<string> GetModelsForProfile(string? profileName)
    {
        SavedProviderProfile? profile = string.IsNullOrWhiteSpace(profileName)
            ? ActiveProfile
            : savedProfiles.FirstOrDefault(p => p.Name.Equals(profileName.Trim(), StringComparison.OrdinalIgnoreCase));
        var models = new List<string>();
        if (profile == null) return models;
        if (profile.Id == activeProfileId && !string.IsNullOrWhiteSpace(Model.Text))
            models.Add(Model.Text.Trim());
        else if (!string.IsNullOrWhiteSpace(profile.Model))
            models.Add(profile.Model.Trim());
        if (profile.CachedModels != null)
        {
            foreach (string m in profile.CachedModels)
                if (!string.IsNullOrWhiteSpace(m) && !models.Contains(m, StringComparer.OrdinalIgnoreCase))
                    models.Add(m);
        }
        return models;
    }

    private void RefreshSavedKeyIndicator()
    {
        try { KeepApiKey.IsChecked = ProviderCredentials.Read(ReadConnection(useSavedKey: false), activeProfileId).Length > 0; }
        catch { KeepApiKey.IsChecked = false; }
    }

    private ProviderOptions ReadConnection(bool useSavedKey = true)
    {
        var options = new ProviderOptions(Selected(Provider), Model.Text.Trim(), ApiKey.Password,
            string.IsNullOrWhiteSpace(ApiUrl.Text) ? null : ApiUrl.Text.Trim(), Region.Text.Trim(),
            CertificateSha256: CertificateSha256.Text.Trim(), JsonMode: JsonMode.IsChecked == true);
        ApiProvider.ResolveBaseAddress(options);
        return useSavedKey && options.ApiKey.Length == 0
            ? options with { ApiKey = ProviderCredentials.Read(options, activeProfileId) }
            : options;
    }

    private ProviderOptions ReadProvider(bool useSavedKey = true)
    {
        int maxCharacters = int.MaxValue;
        int maxRequests = int.MaxValue;
        if (UnlimitedBudget.IsChecked != true)
        {
            if (!int.TryParse(CharacterBudget.Text, out maxCharacters) || maxCharacters <= 0
                || !int.TryParse(RequestBudget.Text, out maxRequests) || maxRequests <= 0)
                throw new ArgumentException("أدخل حدّاً موجباً للأحرف والطلبات أو فعّل خيار «بلا سقف».");
        }
        var options = ReadConnection(useSavedKey) with
        {
            BatchSize = SelectedBatchSize(),
            MaxCharacters = maxCharacters,
            MaxRequests = maxRequests
        };
        ApiProvider.ResolveEndpoint(options);
        return options;
    }

    private void ResetModelCatalog()
    {
        string current = Model.Text;
        Model.ItemsSource = null;
        Model.Text = current;
        ModelListStatus.Visibility = Visibility.Collapsed;
    }

    private static string CertificatePath(Uri address) => Path.Combine(UserData, "server-certificates",
        Identity.Hash(Encoding.UTF8.GetBytes(address.GetLeftPart(UriPartial.Authority))) + ".sha256");

    private string ReadSavedCertificate()
    {
        if (Selected(Provider) != "openai-compatible"
            || !Uri.TryCreate(ApiUrl.Text.Trim(), UriKind.Absolute, out var address) || address.Scheme != Uri.UriSchemeHttps)
            return "";
        string path = CertificatePath(address);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "تعذّرت قراءة بصمة شهادة الخادم المحفوظة.";
            return "";
        }
    }

    private static void SaveCertificate(Uri address, string? fingerprint)
    {
        string path = CertificatePath(address);
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        WriteProviderSetting(path, fingerprint);
    }

    private static void WriteProviderSetting(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string staging = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(staging, content);
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private async void FetchModels(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string>? choices = null;
        await Run(async cancellation =>
        {
            var options = ReadConnection();
            IReadOnlyList<string> models;
            try { models = await ApiProvider.ListModels(options, cancellation); }
            catch (PrivateServerCertificateException error)
            {
                CertificateOptions.IsExpanded = true;
                string message = $"قدّم الخادم {error.Endpoint.Authority} شهادة ذاتية التوقيع.\n\n"
                    + $"بصمة SHA-256 التي قرأها Locara\n{error.Fingerprint}\n\n"
                    + "قارنها ببصمة الشهادة من مسؤول الخادم أو من ملفها على الخادم. هل تطابقت وتريد حفظها لهذا العنوان؟";
                if (MessageBox.Show(this, message, "مراجعة شهادة الخادم", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                    throw new OperationCanceledException();
                cancellation.ThrowIfCancellationRequested();
                SaveCertificate(error.Endpoint, error.Fingerprint);
                CertificateSha256.Text = error.Fingerprint;
                models = await ApiProvider.ListModels(options with { CertificateSha256 = error.Fingerprint }, cancellation);
            }
            catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError
                && options.Provider == "openai-compatible")
            {
                CertificateOptions.IsExpanded = true;
                throw;
            }
            string current = Model.Text;
            Model.ItemsSource = models;
            Model.Text = current;
            ModelListStatus.Text = models.Count == 0 ? "لم يعرض الخادم نماذج. يمكنك كتابة اسم النموذج."
                : $"النماذج المتاحة من الخادم · {models.Count}";
            ModelListStatus.Visibility = Visibility.Visible;
            choices = models;
            if (models.Count > 0)
            {
                int idx = savedProfiles.FindIndex(p => p.Id == activeProfileId);
                if (idx >= 0)
                {
                    savedProfiles[idx] = savedProfiles[idx] with { CachedModels = models.ToArray() };
                    PersistProfilesFile();
                    RefreshRoutingOptions();
                }
            }
        });
        if (choices?.Count > 0)
        {
            Model.Focus();
            Model.IsDropDownOpen = true;
        }
    }

    private void SaveProvider(object sender, RoutedEventArgs e)
    {
        try
        {
            bool keepKey = KeepApiKey.IsChecked == true;
            var options = ReadProvider(useSavedKey: keepKey);
            string profileName = string.IsNullOrWhiteSpace(ProfileNameInput.Text)
                ? (ActiveProfile.Name.Length > 0 ? ActiveProfile.Name : options.Provider)
                : ProfileNameInput.Text.Trim();
            if (savedProfiles.Any(p => p.Id != activeProfileId && p.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"يوجد ملف اتصال آخر بنفس الاسم «{profileName}». اختر اسماً مختلفاً.");

            if (keepKey)
            {
                if (ApiKey.Password.Length > 0)
                    ProviderCredentials.Write(options with { ApiKey = ApiKey.Password }, activeProfileId);
                else if (options.ApiKey.Length == 0)
                    throw new ArgumentException("أدخل مفتاحاً صالحاً للحفظ في Windows.");
            }
            int cBudget = int.TryParse(CharacterBudget.Text, out int c) && c > 0 ? c : 500000;
            int rBudget = int.TryParse(RequestBudget.Text, out int r) && r > 0 ? r : 500;
            var settings = new ProviderSettings(options.Provider, options.Model, options.BaseUrl, options.Region ?? "",
                cBudget, rBudget, options.CertificateSha256, options.JsonMode);
            var address = ApiProvider.ResolveBaseAddress(options);
            if (options.Provider == "openai-compatible" && address.Scheme == Uri.UriSchemeHttps)
                SaveCertificate(address, options.CertificateSha256);

            string[]? cachedModels = Model.ItemsSource is IEnumerable<string> items
                ? items.Where(m => !string.IsNullOrWhiteSpace(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                : ActiveProfile.CachedModels;
            if (!string.IsNullOrWhiteSpace(options.Model) && (cachedModels == null || !cachedModels.Contains(options.Model, StringComparer.OrdinalIgnoreCase)))
                cachedModels = cachedModels == null ? [options.Model] : [options.Model, .. cachedModels];

            int idx = savedProfiles.FindIndex(p => p.Id == activeProfileId);
            var updated = new SavedProviderProfile(activeProfileId, profileName, options.Provider, options.Model,
                options.BaseUrl, options.Region ?? "", options.CertificateSha256, options.JsonMode, cachedModels);
            if (idx >= 0) savedProfiles[idx] = updated;
            else savedProfiles.Add(updated);

            WriteProviderSetting(ProviderSettingsPath, JsonSerializer.Serialize(settings));
            PersistProfilesFile();
            BindProfileSelector();
            ProfileNameInput.Text = profileName;
            RefreshRoutingOptions();
            RefreshProviderSummary();
            ConnectionPanel.IsExpanded = false;
            Status.Text = $"حُفظ ملف الاتصال «{profileName}».";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void ForgetProviderKey(object sender, RoutedEventArgs e)
    {
        try
        {
            ProviderCredentials.Delete(ReadConnection(useSavedKey: false), activeProfileId, deleteLegacy: savedProfiles.Count <= 1);
            ApiKey.Clear();
            KeepApiKey.IsChecked = false;
            Status.Text = "أُزيل المفتاح المحفوظ لهذا الاتصال.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private bool settingProject;
    private string ReviewStatus => ReviewFilter.SelectedValue as string ?? "";

    private void ChangeReviewFilter(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && !settingProject) SearchRows(sender, e);
    }

    private async void Estimate(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path || !SaveEdits()) return;
        string search = Search.Text, status = ReviewStatus;
        try
        {
            var result = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                var rows = project.Units(search, limit: -1, status: status).Where(row => row.Status == "pending").ToList();
                return (rows.Count, Characters: rows.Sum(row => row.Source.Length));
            });
            if (projectPath == path && Search.Text == search && ReviewStatus == status)
                Status.Text = $"{result.Count} نصاً في نتائج البحث / {result.Characters} حرفاً.";
        }
        catch (Exception error) { if (projectPath == path) Status.Text = error.Message; }
    }

    private async void TranslateSelected(object sender, RoutedEventArgs e)
    {
        if (!Rows.IsEnabled) return;
        var rows = Rows.SelectedItems.OfType<Unit>().ToList();
        if (rows.Count == 0) { Status.Text = "حدّد الصفوف التي تريد ترجمتها."; return; }
        await Translate(selection: rows);
    }

    private async void TranslateSection(object sender, RoutedEventArgs e) => await Translate();
    private async void TranslateAll(object sender, RoutedEventArgs e) => await Translate(all: true);
    private async void TranslateSample(object sender, RoutedEventArgs e) => await Translate(maximumUnits: 3);
    private async void ResumeTranslation(object sender, RoutedEventArgs e) => await Translate(resume: true);

    private async Task Translate(IReadOnlyList<Unit>? selection = null, int? maximumUnits = null,
        bool all = false, bool resume = false)
    {
        if (projectPath is not { } path || !SaveEdits()) return;
        ProviderOptions options;
        try { options = ReadProvider(); }
        catch (Exception error) { Tabs.SelectedItem = TranslationPage; ConnectionPanel.IsExpanded = true; Status.Text = error.Message; return; }

        TranslationJob job;
        (int Units, int Characters) estimate;
        TranslationUsage usage;
        try
        {
            string search = Search.Text, filter = ReviewStatus;
            var selectedRows = selection?.Where(row => row.Status == "pending")
                .ToDictionary(row => row.Id, row => row.Revision, StringComparer.Ordinal);
            string label = all ? "مشروع التعريب" : selection != null ? "الصفوف المحددة"
                : string.IsNullOrWhiteSpace(search) ? "نتائج الفلتر" : search;
            (job, estimate, usage) = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                var runner = new TranslationRunner(project);
                var captured = resume ? runner.LastJob ?? throw new InvalidOperationException("لا توجد دفعة محفوظة لهذا المصدر.")
                    : selectedRows != null ? runner.Capture(label, selectedRows, options)
                    : runner.Capture(label, all ? project.Units(limit: -1) : project.Units(search, limit: -1, status: filter), options);
                return (captured, runner.Estimate(captured, maximumUnits), runner.Usage(captured));
            });
        }
        catch (Exception error) { Status.Text = error.Message; return; }

        if (projectPath != path || openingProject || work != null || !IsLoaded) return;
        if (estimate.Units == 0) { Status.Text = "لا توجد نصوص معلّقة في النطاق المختار."; return; }
        var endpoint = ApiProvider.ResolveEndpoint(options);
        string budgetLine = options.MaxRequests == int.MaxValue
            ? "حدّ التشغيل — بلا سقف"
            : $"حدّ التشغيل — {options.MaxCharacters:N0} حرفاً / {options.MaxRequests:N0} طلباً";
        string confirmation = $"{job.Label}\n{estimate.Units:N0} نصاً / {estimate.Characters:N0} حرفاً قبل إعادة استخدام الذاكرة."
            + $"\nالمزوّد والنموذج — {options.Provider} {options.Model}\nالوجهة — {endpoint.Authority}"
            + $"\n{budgetLine}"
            + $"\nالاستخدام المسجّل — {usage.Characters:N0} حرفاً / {usage.Requests:N0} طلباً";
        if (MessageBox.Show(this, confirmation, maximumUnits == null ? "بدء التعريب" : "ترجمة عينة من النطاق",
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        string password = ZipPassword.Password;
        var progress = new Progress<TranslationProgress>(update =>
        {
            string tok = update.Tokens is { TotalTokens: > 0 } t ? $" · {t.TotalTokens:N0} توكن" : "";
            Status.Text = $"الدفعات المحفوظة — {update.Completed}/{update.Total}{tok}";
            if (update.Tokens is { TotalTokens: > 0 } live)
            {
                LiveTokensTotal.Text = live.TotalTokens.ToString("N0");
                LiveTokensBreakdown.Text = $"دخل: {live.InputTokens:N0} · خرج: {live.OutputTokens:N0}";
            }
        });
        await Run(async cancellation =>
        {
            var build = await Task.Run(async () =>
            {
                using var project = new ProjectStore(path);
                await new TranslationRunner(project).Run(options with { MaxUnits = maximumUnits }, progress, cancellation, job: job);
                if (maximumUnits != null) return null;
                var service = new ProjectService(project);
                var preview = service.Preview(password: password, cancellation: cancellation);
                if (preview.ChangedFiles.Count == 0) return null;
                var result = service.Build(Path.Combine(path, "outputs", "preview-" + Guid.NewGuid().ToString("N")),
                    password: password, cancellation: cancellation, reviewed: preview, overlayOnly: true);
                project.LastOutput = result.Directory;
                return result;
            }, cancellation);
            if (build != null)
            {
                packagePath = build.Directory;
                BuildLog.Text = $"المعاينة جاهزة — {build.Translations} ترجمة\n{build.Directory}";
                Tabs.SelectedItem = PackagesPage;
            }
            else Tabs.SelectedItem = EditorPage;
        });
    }

    private async void Retranslate(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || Rows.SelectedItems.Count != 1 || Rows.SelectedItem is not Unit row || !SaveEdits())
        { Status.Text = "حدّد سطراً واحداً لإعادة ترجمته."; return; }
        ProviderOptions options;
        try { options = ReadProvider(); }
        catch (Exception error) { Tabs.SelectedItem = TranslationPage; ConnectionPanel.IsExpanded = true; Status.Text = error.Message; return; }
        var endpoint = ApiProvider.ResolveEndpoint(options);
        if (MessageBox.Show(this, $"إرسال سطر واحد ({row.Source.Length} حرفاً) إلى {endpoint.Authority}؟",
            "طلب مقترح جديد", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;

        string path = projectPath;
        await Run(async cancellation =>
        {
            var suggestion = await Task.Run(async () =>
            {
                using var project = new ProjectStore(path);
                return await new TranslationRunner(project).Suggest(row.Id, options, cancellation);
            }, cancellation);
            ShowProposal(path, suggestion.Original, suggestion.Target, suggestion.Warning, "إعادة ترجمة السطر");
        });
    }

    private void ShowProposal(string path, Unit original, string target, string warning, string title)
    {
        var changes = new[] { new ReviewChange(original, original.Source, original.Target, target, warning) };
        var dialog = new ReviewExchangeWindow(changes) { Owner = this, Title = title };
        if (dialog.ShowDialog() != true) return;
        using var project = new ProjectStore(path);
        new ReviewExchange(project).Apply(changes);
        _ = RefreshRows();
    }

    private void SuggestMemory(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || Rows.SelectedItem is not Unit row || !SaveEdits()) return;
        try
        {
            using var project = new ProjectStore(projectPath);
            var match = project.MemoryFor(row).FirstOrDefault();
            if (match == null) { Status.Text = "لا توجد ترجمة معتمدة مطابقة للأصل."; return; }
            ShowProposal(projectPath, row, match.Target, Tokens.Validate(row.Source, match.Target, scannedEngine),
                "اقتراح الذاكرة — " + (match.Context.Length > 0 ? match.Context : match.Path));
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void OpenGlossary(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || !SaveEdits()) return;
        ReloadTerms();
        if (Rows.SelectedItem is Unit row)
        {
            TermSource.Text = row.Source;
            TermTarget.Text = "";
        }
        Tabs.SelectedItem = TranslationPage;
        GlossaryPanel.IsExpanded = true;
        TermSource.Focus();
        TermSource.SelectAll();
    }

    private void ReloadTerms()
    {
        if (projectPath == null) return;
        using var project = new ProjectStore(projectPath);
        Terms.ItemsSource = project.Glossary().OrderBy(pair => pair.Key).ToList();
    }

    private void OpenTerm(object sender, SelectionChangedEventArgs e)
    {
        if (Terms.SelectedItem is not KeyValuePair<string, string> term || term.Key == null) return;
        TermSource.Text = term.Key;
        TermTarget.Text = term.Value;
    }

    private void SaveTerm(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || string.IsNullOrWhiteSpace(TermSource.Text) || string.IsNullOrWhiteSpace(TermTarget.Text)) return;
        try
        {
            using var project = new ProjectStore(projectPath);
            project.Glossary(TermSource.Text.Trim(), TermTarget.Text.Trim());
            ReloadTerms();
            Status.Text = "حُفظ المصطلح.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private void DeleteTerm(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || Terms.SelectedItem is not KeyValuePair<string, string> term || term.Key == null) return;
        try
        {
            using var project = new ProjectStore(projectPath);
            project.RemoveTerm(term.Key);
            TermSource.Clear();
            TermTarget.Clear();
            ReloadTerms();
            Status.Text = "حُذف المصطلح المحدد.";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void BackupProject(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || !SaveEdits()) return;
        var dialog = new SaveFileDialog { Title = "حفظ نسخة مستقلة من المشروع", Filter = "Locara|*.locara", FileName = "Locara-backup.locara" };
        if (dialog.ShowDialog(this) != true) return;
        string path = projectPath;
        await Run(async cancellation =>
        {
            await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                store.Backup(dialog.FileName);
            }, cancellation);
            MessageBox.Show(this, $"افتح هذه النسخة من «فتح مشروع» عند الاستعادة.\n{dialog.FileName}", "نسخة المشروع");
        });
    }

    private async void ExportReview(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || !SaveEdits()) return;
        var dialog = new SaveFileDialog { Title = "تصدير نتائج البحث للمراجعة", Filter = "CSV|*.csv", FileName = "Locara-review.csv" };
        if (dialog.ShowDialog(this) != true) return;
        string path = projectPath, search = Search.Text, status = ReviewStatus;
        await Run(cancellation => Task.Run(() =>
        {
            using var project = new ProjectStore(path);
            new ReviewExchange(project).Export(dialog.FileName, project.Units(search, limit: -1, status: status));
        }, cancellation));
    }

    private async void ImportReview(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || !SaveEdits()) return;
        var dialog = new OpenFileDialog { Title = "اختر ملف مراجعة Locara", Filter = "CSV|*.csv" };
        if (dialog.ShowDialog(this) != true) return;
        string path = projectPath;
        await Run(async cancellation =>
        {
            var changes = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                return new ReviewExchange(project).Preview(dialog.FileName);
            }, cancellation);
            if (changes.Count == 0)
            {
                MessageBox.Show(this, "لا توجد تغييرات في هذا الملف.", "مراجعة CSV");
                return;
            }
            if (new ReviewExchangeWindow(changes) { Owner = this }.ShowDialog() != true) return;
            await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                new ReviewExchange(project).Apply(changes);
            }, cancellation);
        });
    }
}
