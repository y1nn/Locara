using System.IO;
using System.Windows;
using Locara.Core;
using Microsoft.Win32;

namespace Locara.Desktop;

public partial class MainWindow
{
    private static bool MatchesSource(ProjectStore project, string source) =>
        StringComparer.OrdinalIgnoreCase.Equals(project.Info.Source, source)
        || StringComparer.OrdinalIgnoreCase.Equals(new ProjectSettings(project).GetRenpyOriginFor(project.Info.Source), source);

    private async void StartSteamSource(object sender, RoutedEventArgs e)
    {
        if (work != null) return;
        IReadOnlyList<SteamGame>? games = null;
        await Run(async cancellation =>
            games = await Task.Run(() => SteamLibrary.Discover(cancellation), cancellation));
        if (games == null) return;
        if (games.Count == 0) { Status.Text = "لم يعثر Locara على ألعاب مثبّتة في مكتبات Steam."; return; }
        var picker = new SourcePickerWindow("مكتبة Steam", games, nameof(SteamGame.Display)) { Owner = this };
        if (picker.ShowDialog() == true && picker.Selected is SteamGame game)
            await StartSource(game.Directory);
    }

    private void SourceDragOver(object sender, DragEventArgs e)
    {
        e.Effects = work == null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 }
            && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void DropSource(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (work == null && e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files)
            await StartSource(files[0]);
    }

    private async void ChooseLanguage(object sender, RoutedEventArgs e)
    {
        if (await SelectLanguage(showWhenUnavailable: true)) await ScanCurrent(openEditor: true);
    }

    private async Task<bool> SelectLanguage(bool showWhenUnavailable)
    {
        if (projectPath == null || !Directory.Exists(SourcePath.Text))
        {
            if (showWhenUnavailable) Status.Text = "اختر مجلد اللعبة لعرض كتالوجات اللغة.";
            return false;
        }
        string path = projectPath;
        bool selected = false;
        await Run(async cancellation =>
        {
            var (languages, current) = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                var service = new ProjectService(project);
                return (service.Languages(cancellation), service.SelectedPackage);
            }, cancellation);
            if (languages.Count < 2 && !showWhenUnavailable) return;
            if (languages.Count == 0) return;
            var initial = languages.FirstOrDefault(language => language.Package == current)
                ?? languages.FirstOrDefault(language => language.Code == "en");
            var picker = new SourcePickerWindow("لغة المصدر", languages, nameof(LanguageSource.Display), initial) { Owner = this };
            if (picker.ShowDialog() != true || picker.Selected is not LanguageSource source) return;
            using var project = new ProjectStore(path);
            new ProjectService(project).SelectPackage(source.Package);
            selected = true;
        });
        if (showWhenUnavailable && !selected && Status.Text == "اكتملت العملية")
            Status.Text = "لم تتغيّر لغة المصدر.";
        return selected;
    }

    private async void ShowCapabilities(object sender, RoutedEventArgs e)
    {
        if (projectPath == null) return;
        string path = projectPath, password = ZipPassword.Password;
        if (!TrySyncSourceAdvancedSettings(path, includeEngine: true)) return;
        await Run(async cancellation =>
        {
            var capability = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                return new ProjectService(project).Capabilities(password, cancellation);
            }, cancellation);
            ScanLog.Text = DescribeCapability(capability);
        });
    }

    private static string DescribeCapability(SourceCapability capability)
    {
        string engine = capability.Engine == "generic" ? "ملفات عامة" : capability.Engine;
        if (capability.Version.Length > 0) engine += " " + capability.Version;
        return $"{engine}\nالقراءة — {(capability.CanRead ? "متاحة" : "تحتاج مصدراً قابلاً للقراءة")}"
            + $"\nإعادة البناء — {(capability.CanBuild ? "متاحة للنصوص الممسوحة" : "تحتاج فحصاً بنصوص قابلة للتصدير")}"
            + $"\nالتجربة من Locara — {(capability.Launch != GameLaunch.None ? "متاحة" : "راجع طريقة الإرجاع أدناه")}\n{capability.Deployment}"
            + (capability.Arabic.Length == 0 ? "" : "\n" + capability.Arabic);
    }

    private async void GenerateRenpy(object sender, RoutedEventArgs e) => await GenerateRenpyCore();

    private async Task GenerateRenpyCore()
    {
        if (projectPath == null || work != null) return;
        string path = projectPath, password = ZipPassword.Password;
        bool switched = false;
        await Run(async cancellation =>
        {
            var (source, capability) = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return (store.Info.Source, new ProjectService(store).Capabilities(password, cancellation));
            }, cancellation);
            if (!capability.CanGenerateTemplates) throw new InvalidOperationException(capability.Deployment);
            string sdk = ChooseRenpySdk(path);
            string copy = Path.Combine(Path.GetDirectoryName(path)!, "RenpySources", Guid.NewGuid().ToString("N"));
            await Task.Run(() => GameRuntime.GenerateRenpyTemplates(source, copy, sdk, cancellation), cancellation);
            using var project = new ProjectStore(path);
            if (!StringComparer.OrdinalIgnoreCase.Equals(project.Info.Source, source))
                throw new InvalidOperationException("تغيّر مصدر المشروع أثناء التوليد. افتح النسخة المولّدة من " + copy);
            project.ChangeSource(copy, relocated: true, workflowOrigin: source);
            switched = true;
        });
        if (!switched) return;
        if (!await SetProject(path)) return;
        await ScanCurrent(openEditor: true);
    }

    private static string ReadRuntimeSetting(string file)
    {
        string path = Path.Combine(UserData, file);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
    }

    private string ChooseRenpySdk(string path)
    {
        const string setting = "renpy-sdk.txt";
        using var project = new ProjectStore(path);
        var projSettings = new ProjectSettings(project);
        string saved = projSettings.RenpySdk;
        if (Directory.Exists(saved))
        {
            try { GameRuntime.RequireRenpySdk(saved); return saved; }
            catch (FileNotFoundException) { }
        }
        var picker = new OpenFolderDialog { Title = "اختر مجلد Ren’Py SDK المطابق للعبة" };
        string lastUsed = ReadRuntimeSetting(setting);
        if (Directory.Exists(lastUsed)) picker.InitialDirectory = lastUsed;
        if (picker.ShowDialog(this) != true) throw new OperationCanceledException();
        GameRuntime.RequireRenpySdk(picker.FolderName);
        projSettings.RenpySdk = picker.FolderName;
        File.WriteAllText(Path.Combine(UserData, setting), picker.FolderName);
        return picker.FolderName;
    }

    private async Task<string> ChooseGodot(string version, CancellationToken cancellation)
    {
        const string setting = "godot-runtime.txt";
        string saved = ReadRuntimeSetting(setting);
        if (File.Exists(saved))
        {
            try { await GameRuntime.RequireGodot(saved, version, cancellation); return saved; }
            catch (InvalidOperationException) { }
            catch (IOException) { }
        }
        var picker = new OpenFileDialog { Title = $"اختر برنامج Godot المطابق للحزمة ({version})", Filter = "Godot (*.exe)|*.exe" };
        if (picker.ShowDialog(this) != true) throw new OperationCanceledException();
        await GameRuntime.RequireGodot(picker.FileName, version, cancellation);
        File.WriteAllText(Path.Combine(UserData, setting), picker.FileName);
        return picker.FileName;
    }

    private async void TryGame(object sender, RoutedEventArgs e) => await TryGameCore();

    private async Task TryGameCore()
    {
        if (projectPath == null) { Status.Text = "اختر لعبة أولاً."; return; }
        if (!TrySyncSourceAdvancedSettings(projectPath)) return;
        if (!await EnsureOriginalsBeforeActionAsync("تجربة اللعبة المؤقتة")) return;
        string path = projectPath, password = ZipPassword.Password;
        await Run(async cancellation =>
        {
            var (plan, capability, steamTarget, genericSteamGame) = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                var service = new ProjectService(store);
                var support = service.Capabilities(password, cancellation);
                if (support.Launch == GameLaunch.None) throw new InvalidOperationException(support.Deployment);
                var preview = service.Preview(password: password, cancellation: cancellation);
                if (support.Launch == GameLaunch.Steam)
                {
                    if (support.Engine == "Star Trucker")
                        return (preview, support, (string?)StarTruckerTrial.ConfirmDestination(store, preview), (SteamGame?)null);
                    var game = SteamGameTrial.ConfirmDestination(store, preview);
                    return (preview, support, (string?)game.Directory, (SteamGame?)game);
                }
                return (preview, support, (string?)null, (SteamGame?)null);
            }, cancellation);

            string output = Path.Combine(path, "outputs", "trial-" + Guid.NewGuid().ToString("N"));
            string copy = output + "-game";
            string target = steamTarget ?? (capability.Launch == GameLaunch.RenpyCopy ? copy : output);
            var review = new BuildReviewWindow(plan, automaticOutput: true, trialDestination: target, launch: capability.Launch) { Owner = this };
            if (review.ShowDialog() != true) throw new OperationCanceledException();

            string? runtime = capability.Launch switch
            {
                GameLaunch.GodotPack => await ChooseGodot(capability.Version, cancellation),
                GameLaunch.RenpyCopy => ChooseRenpySdk(path),
                _ => null
            };
            Tabs.SelectedItem = PackagesPage;
            var progress = new Progress<string>(message => BuildLog.Text = message);
            if (capability.Launch == GameLaunch.Steam)
            {
                if (genericSteamGame != null)
                {
                    var result = await Task.Run(() => SteamGameTrial.RunAsync(path, genericSteamGame, output, plan,
                        password, progress, cancellation), cancellation);
                    packagePath = result.Directory;
                    BuildLog.Text = $"استُعيدت ملفات {genericSteamGame.Name} الأصلية.\nالحزمة — {result.Directory}";
                    return;
                }
                var stResult = await Task.Run(() => StarTruckerTrial.RunAsync(path, target, output, plan,
                    password, progress, cancellation), cancellation);
                packagePath = stResult.Directory;
                BuildLog.Text = $"استُعيدت ملفات Star Trucker الأصلية.\nالحزمة — {stResult.Directory}";
                return;
            }
            BuildLog.Text = "يجري بناء نسخة التجربة.";
            string launched = await Task.Run(async () =>
            {
                using var store = new ProjectStore(path);
                new ProjectService(store).Build(output, password: password, cancellation: cancellation, reviewed: plan);
                store.LastOutput = output;
                if (capability.Launch == GameLaunch.GodotPack)
                    return GameRuntime.LaunchGodot(runtime!, output, cancellation);
                await GameRuntime.LaunchRenpy(store, output, copy, runtime!, cancellation);
                return copy;
            }, cancellation);
            packagePath = output;
            BuildLog.Text = $"بُنيت نسخة التجربة وبدأ تشغيلها.\n{launched}\nالحزمة — {output}";
        });
    }
}
