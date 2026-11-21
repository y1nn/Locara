using System.IO;
using System.Windows;
using System.Windows.Media;
using Locara.Core;

namespace Locara.Desktop;

public partial class MainWindow
{
    private void RefreshDirectInstallStatus()
    {
        if (projectPath == null)
        {
            DirectInstallStatusTitle.Text = "الحالة: لا يوجد مشروع مفتوح";
            DirectInstallStatusDetail.Text = "";
            DirectRestoreButton.Visibility = Visibility.Collapsed;
            AcceptGameUpdateButton.Visibility = Visibility.Collapsed;
            return;
        }
        string path = projectPath;
        _ = Task.Run(() =>
        {
            try
            {
                using var project = new ProjectStore(path);
                string destination = "";
                try { destination = PackageInstaller.ResolveInstallDestination(project); } catch { }
                var state = PackageInstaller.GetInPlaceStatus(project);
                string engine = project.Get("scanned_engine");
                return (Destination: destination, State: state, Engine: engine, Error: (string?)null);
            }
            catch (Exception error)
            {
                return (Destination: "", State: (InPlaceInstallState?)null, Engine: "", Error: (string?)error.Message);
            }
        }).ContinueWith(task =>
        {
            Dispatcher.Invoke(() =>
            {
                if (projectPath != path) return;
                var (destination, state, engine, error) = task.Result;
                bool showUnityTmp = string.IsNullOrWhiteSpace(engine) || engine.Equals("unity", StringComparison.OrdinalIgnoreCase);
                UnityTmpSettingsBorder.Visibility = showUnityTmp ? Visibility.Visible : Visibility.Collapsed;
                if (error != null)
                {
                    DirectInstallStatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF8EB"));
                    DirectInstallStatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E9C46A"));
                    DirectInstallStatusTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A5A00"));
                    DirectInstallStatusTitle.Text = "تعذّر فحص حالة التثبيت";
                    DirectInstallStatusDetail.Text = error;
                    DirectRestoreButton.Visibility = Visibility.Collapsed;
                    AcceptGameUpdateButton.Visibility = Visibility.Collapsed;
                    return;
                }

                if (state == null)
                {
                    DirectInstallStatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F4F8F7"));
                    DirectInstallStatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D5E3DF"));
                    DirectInstallStatusTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#174B45"));
                    DirectInstallStatusTitle.Text = "الحالة: اللعبة على ملفاتها الأصلية";
                    string engineLabel = string.IsNullOrWhiteSpace(engine) ? "" : $" · المحرك: {engine}";
                    DirectInstallStatusDetail.Text = string.IsNullOrWhiteSpace(destination)
                        ? "اختر مجلد اللعبة المصدر أولاً ليتم التعرّف على مسار التثبيت تلقائياً."
                        : $"المسار المكتشف تلقائياً: {destination}{engineLabel}";
                    DirectInstallButton.Content = "تثبيت دائم داخل اللعبة (مع نسخة احتياطية)";
                    DirectRestoreButton.Visibility = Visibility.Collapsed;
                    AcceptGameUpdateButton.Visibility = Visibility.Collapsed;
                }
                else if (state.ModifiedExternally)
                {
                    DirectInstallStatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#FFF6E8"));
                    DirectInstallStatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#E9B949"));
                    DirectInstallStatusTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A5A00"));
                    DirectInstallStatusTitle.Text = $"تنبيه: تغيّرت {state.ModifiedCount} ملفات داخل اللعبة خارجياً (تحديث رسمي للعبة)";
                    DirectInstallStatusDetail.Text =
                        $"المسار: {state.Destination}\n" +
                        "اضغط «اعتماد تحديث اللعبة الجديد» ثم أعد فحص الملفات لمزامنة التحديث الجديد، وبعدها ثبّت الترجمة من جديد.";
                    DirectInstallButton.Content = "تثبيت دائم داخل اللعبة (مع نسخة احتياطية)";
                    DirectRestoreButton.Visibility = Visibility.Collapsed;
                    AcceptGameUpdateButton.Visibility = Visibility.Visible;
                }
                else
                {
                    DirectInstallStatusBorder.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EBF8F4"));
                    DirectInstallStatusBorder.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8CCBB9"));
                    DirectInstallStatusTitle.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F5B4F"));
                    DirectInstallStatusTitle.Text = $"الحالة: التعريب مثبّت دائماً داخل اللعبة ({state.Files} ملفات)";
                    string summaryLine = string.IsNullOrWhiteSpace(state.Summary) ? "" : $"\n{state.Summary}";
                    string localTime = state.InstalledAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    DirectInstallStatusDetail.Text =
                        $"تاريخ التثبيت: {localTime} · النسخة الاحتياطية الأصلية محفوظة بأمان داخل المشروع.\nالمسار: {state.Destination}{summaryLine}";
                    DirectInstallButton.Content = "تحديث التثبيت الدائم (بأحدث ترجمة / خط)";
                    DirectRestoreButton.Visibility = Visibility.Visible;
                    AcceptGameUpdateButton.Visibility = Visibility.Collapsed;
                }
            });
        }, TaskScheduler.Default);
    }

    private async Task<bool> EnsureOriginalsBeforeActionAsync(string actionLabel)
    {
        if (projectPath == null) return true;
        string path = projectPath;
        var state = await Task.Run(() =>
        {
            using var project = new ProjectStore(path);
            return PackageInstaller.GetInPlaceStatus(project);
        });
        if (state == null) return true;
        if (state.ModifiedExternally)
        {
            MessageBox.Show(this,
                "تغيّرت ملفات اللعبة خارجياً بعد التثبيت الدائم (مثل تحديث من Steam).\n" +
                "اذهب إلى تبويب «الحزم والتجربة» ← اضغط «اعتماد تحديث اللعبة الجديد» ثم أعد فحص الملفات أولاً.",
                "تحديث خارجي للعبة", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        var answer = MessageBox.Show(this,
            $"التعريب مثبّت حالياً بشكل دائم داخل اللعبة ({state.Files} ملفات).\n" +
            $"لتنفيذ «{actionLabel}» يحتاج Locara إلى الملفات الأصلية أولاً.\n\n" +
            "هل تريد استعادة الملفات الأصلية تلقائياً الآن والمتابعة؟",
            "التعريب مثبّت داخل اللعبة",
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return false;

        await Run(async ct =>
        {
            int restored = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                return PackageInstaller.RestoreInPlace(project, ct);
            }, ct);
            BuildLog.Text = $"استُعيدت الملفات الأصلية ({restored} ملفات) تمهيداً لـ {actionLabel}.";
            RefreshDirectInstallStatus();
        });
        return await Task.Run(() =>
        {
            using var project = new ProjectStore(path);
            return PackageInstaller.GetInPlaceStatus(project) == null;
        });
    }

    private async void DirectInstallGame(object sender, RoutedEventArgs e)
    {
        if (projectPath == null || !SaveEdits()) return;
        string path = projectPath, password = ZipPassword.Password;

        var precheck = await Task.Run(() =>
        {
            using var project = new ProjectStore(path);
            string destination = PackageInstaller.ResolveInstallDestination(project);
            string? running = PackageInstaller.FindRunningExecutableIn(destination);
            var existing = PackageInstaller.GetInPlaceStatus(project);
            string engine = project.Get("scanned_engine");
            var font = ArabicFontCatalog.Resolve(project.Get("display_font"), CustomFontsDir);
            var unityDisplay = UnityArabicDisplayOptions.Parse(project.Get("unity_arabic_wrap"), project.Get("unity_arabic_font_scale"), project.Get("unity_arabic_shaping"));
            return (Destination: destination, Running: running, Existing: existing, Engine: engine, FontName: font.DisplayName, UnityDisplay: unityDisplay);
        });

        if (precheck.Running != null)
        {
            MessageBox.Show(this,
                $"اللعبة قيد التشغيل حالياً ({precheck.Running}).\nأغلق اللعبة أولاً ثم أعد الضغط على زر التثبيت.",
                "أغلق اللعبة أولاً", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (precheck.Existing is { ModifiedExternally: true })
        {
            MessageBox.Show(this,
                "تغيّرت ملفات اللعبة خارجياً بعد التثبيت السابق (مثل تحديث من Steam).\n" +
                "اضغط «اعتماد تحديث اللعبة الجديد» ثم أعد فحص الملفات أولاً.",
                "تحديث خارجي للعبة", MessageBoxButton.OK, MessageBoxImage.Warning);
            RefreshDirectInstallStatus();
            return;
        }

        bool isUpdate = precheck.Existing != null;
        string heading = isUpdate ? "تأكيد تحديث التثبيت الدائم" : "تأكيد التثبيت النهائي داخل اللعبة";
        string actionDesc = isUpdate
            ? "سيستعيد Locara الملفات الأصلية تلقائياً ثم يعيد بناء الحزمة بأحدث ترجماتك وخطك المختار ويثبّتها مباشرة في اللعبة."
            : "سيبني Locara الحزمة النهائية ويحفظ نسخة احتياطية مؤكدة من الملفات الأصلية داخل المشروع قبل تثبيت الترجمة مباشرة في اللعبة.";
        string unityLine = precheck.Engine.Equals("unity", StringComparison.OrdinalIgnoreCase)
            ? $"\n• إعدادات Unity TMP: التفاف الأسطر ({precheck.UnityDisplay.WrapLabel}) · حجم الخط ({precheck.UnityDisplay.ScaleLabel})"
            : "";
        string confirmText =
            $"{actionDesc}\n\n" +
            $"• مسار اللعبة التلقائي:\n{precheck.Destination}\n" +
            $"• الخط المختار (عند دعم المحرك): {precheck.FontName}{unityLine}\n\n" +
            "يمكنك في أي وقت استعادة اللعبة الأصلية بضغطة واحدة من زر «إزالة التعريب واستعادة الأصل».";

        if (MessageBox.Show(this, confirmText, heading, MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;

        await Run(async ct =>
        {
            string outputDir = Path.Combine(path, "outputs", "installed-" + Guid.NewGuid().ToString("N"));
            var (build, install, updatedExisting) = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                return PackageInstaller.DeployInPlace(project, outputDir, final: true, password: password, cancellation: ct);
            }, ct);
            packagePath = build.Directory;
            string verb = updatedExisting ? "تم تحديث التعريب المثبّت دائماً داخل اللعبة بنجاح!" : "تم تثبيت التعريب نهائياً داخل اللعبة بنجاح!";
            BuildLog.Text =
                $"{verb}\n" +
                $"• النصوص المصدّرة: {build.Translations:N0} نص\n" +
                $"• الملفات المثبّتة: {install.Files} ملفات في {precheck.Destination}\n" +
                $"• النسخة الاحتياطية الآمنة: {install.ReceiptDirectory}\n" +
                $"يمكنك الآن تشغيل اللعبة مباشرة من Steam أو من أيقونتها بدون الحاجة لفتح Locara.";
            Status.Text = verb;
            RefreshDirectInstallStatus();
            UpdateOverview();
        });
    }

    private async void DirectRestoreGame(object sender, RoutedEventArgs e)
    {
        if (projectPath == null) return;
        string path = projectPath;
        if (MessageBox.Show(this,
                "سيستعيد Locara جميع ملفات اللعبة الأصلية من النسخة الاحتياطية المحفوظة وتعود اللعبة كما كانت تماماً.\nهل تريد المتابعة؟",
                "استعادة ملفات اللعبة الأصلية", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;

        await Run(async ct =>
        {
            int restored = await Task.Run(() =>
            {
                using var project = new ProjectStore(path);
                return PackageInstaller.RestoreInPlace(project, ct);
            }, ct);
            BuildLog.Text = restored == 0
                ? "ملفات اللعبة مطابقة للأصل بالفعل."
                : $"تمت استعادة {restored} ملفات أصلية بنجاح. عادت اللعبة إلى حالتها الأصلية بالكامل.";
            Status.Text = "استُعيدت ملفات اللعبة الأصلية.";
            RefreshDirectInstallStatus();
            UpdateOverview();
        });
    }

    private void AcceptExternalGameUpdate(object sender, RoutedEventArgs e)
    {
        if (projectPath == null) return;
        if (MessageBox.Show(this,
                "سيُحذف سجل التثبيت القديم فقط (دون مساس بترجماتك المحفوظة في قاعدة البيانات) لتتمكن من إعادة فحص ملفات اللعبة المحدّثة وتثبيت الترجمة عليها من جديد.\nهل تريد المتابعة؟",
                "اعتماد تحديث اللعبة الجديد", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;

        try
        {
            using var project = new ProjectStore(projectPath);
            PackageInstaller.ClearInPlaceRecord(project);
            BuildLog.Text = "تم اعتماد التحديث الجديد. اذهب إلى تبويب «الملفات» واضغط «إعادة فحص» ثم أعد التثبيت الدائم.";
            Status.Text = "تم اعتماد تحديث اللعبة الجديد — أعد فحص الملفات الآن.";
            RefreshDirectInstallStatus();
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
        }
    }
}
