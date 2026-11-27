using System.IO;
using System.Windows;
using Locara.Core;

namespace Locara.Desktop;

/// <summary>Verified in-app self-updater (SHA-256) living beside the manual GitHub release link.</summary>
public partial class MainWindow
{
    private CancellationTokenSource? selfUpdateCts;
    private bool selfUpdating;

    private void UpdateBadgeClick(object sender, RoutedEventArgs e)
    {
        SelectStage(SettingsPage);
        AppUpdatesSettingsCard?.BringIntoView();
    }

    private void ApplySelfUpdateUi()
    {
        if (SelfUpdateInstallButton == null) return;
        bool canSelfUpdate = latestUpdateResult?.CanSelfUpdate == true;
        SelfUpdateInstallButton.Visibility = canSelfUpdate ? Visibility.Visible : Visibility.Collapsed;
        SelfUpdateInstallButton.IsEnabled = canSelfUpdate && !selfUpdating;
        if (!selfUpdating && SelfUpdateProgressPanel != null)
            SelfUpdateProgressPanel.Visibility = Visibility.Collapsed;
    }

    private void SetSelfUpdateBusy(bool busy)
    {
        selfUpdating = busy;
        SelfUpdateInstallButton.IsEnabled = !busy && latestUpdateResult?.CanSelfUpdate == true;
        CheckUpdatesNowButton.IsEnabled = !busy;
        SelfUpdateProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        SelfUpdateCancelButton.IsEnabled = busy;
        if (busy)
        {
            SelfUpdateProgressBar.Value = 0;
            SelfUpdateProgressBar.IsIndeterminate = true;
        }
        else
        {
            SelfUpdateProgressBar.IsIndeterminate = false;
        }
    }

    private void CancelSelfUpdateClick(object sender, RoutedEventArgs e)
    {
        selfUpdateCts?.Cancel();
        SelfUpdateCancelButton.IsEnabled = false;
    }

    private async void SelfUpdateInstallClick(object sender, RoutedEventArgs e)
    {
        if (work != null)
        {
            Status.Text = "انتظر اكتمال العملية الجارية قبل تحديث Locara.";
            return;
        }
        if (selfUpdating || latestUpdateResult is not { CanSelfUpdate: true } release) return;

        SelfUpdateEligibility eligibility = AppSelfUpdater.CheckInstallability();
        if (!eligibility.CanInstall)
        {
            string guidance =
                "لا يمكن تحديث هذه النسخة تلقائياً:\n" + eligibility.Reason + "\n\n" +
                "يمكنك تنزيل الإصدار الجديد يدوياً من صفحة الإصدار على GitHub ثم استبدال ملفات البرنامج بها.";
            Status.Text = "التحديث التلقائي غير متاح لهذه النسخة — استخدم صفحة الإصدار على GitHub.";
            MessageBox.Show(this, guidance, "التحديث التلقائي غير متاح", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string tag = release.LatestVersion?.ToTagString() ?? release.LatestTag ?? "";
        if (MessageBox.Show(this,
                $"سيُنزّل Locara الإصدار {tag} من GitHub الرسمي ويتحقق من بصمة SHA-256 قبل أي تثبيت.\nلن يُغلق البرنامج قبل موافقتك على إعادة التشغيل.\n\nهل تريد المتابعة؟",
                "تنزيل التحديث الموثّق", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
            return;

        using var cts = new CancellationTokenSource();
        selfUpdateCts = cts;
        SetSelfUpdateBusy(true);
        SelfUpdateProgressText.Text = "جارٍ بدء التنزيل…";
        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            SelfUpdateProgressBar.IsIndeterminate = p.TotalBytes is null or <= 0;
            if (!SelfUpdateProgressBar.IsIndeterminate)
                SelfUpdateProgressBar.Value = Math.Clamp(p.Percent, 0, 100);
            string size = p.TotalBytes is > 0
                ? $"{p.BytesReceived / 1048576.0:0.0} / {p.TotalBytes.Value / 1048576.0:0.0} ميغابايت"
                : $"{p.BytesReceived / 1048576.0:0.0} ميغابايت";
            string stage = p.Stage switch
            {
                "download" => "تنزيل الحزمة",
                "sha256" => "جلب بصمة SHA-256",
                "verify" => "التحقق من البصمة",
                "extract" => "فك الضغط الآمن",
                "ready" => "جاهز",
                _ => p.Stage
            };
            SelfUpdateProgressText.Text = $"{stage} — {size}";
        });

        StagedAppUpdate? staged = null;
        try
        {
            staged = await AppSelfUpdater.DownloadAndVerifyAsync(release, progress, cancellation: cts.Token);
        }
        catch (OperationCanceledException)
        {
            Status.Text = "أُلغي تنزيل التحديث.";
            SelfUpdateProgressText.Text = "أُلغي التنزيل ولم يتغيّر شيء.";
        }
        catch (Exception error)
        {
            Status.Text = "تعذّر تنزيل أو توثيق التحديث — لم يتغيّر شيء. يمكنك استخدام صفحة الإصدار على GitHub.";
            SelfUpdateProgressText.Text = error.Message;
        }
        finally
        {
            selfUpdateCts = null;
            if (IsLoaded) SetSelfUpdateBusy(false);
        }

        if (staged == null || !IsLoaded) return;
        SelfUpdateProgressPanel.Visibility = Visibility.Visible;
        SelfUpdateProgressBar.Value = 100;
        SelfUpdateProgressText.Text = $"✓ نُزّل الإصدار {staged.Tag} وتم التحقق من بصمة SHA-256.";

        if (work != null || !SaveEdits())
        {
            Status.Text = "التحديث جاهز لكن العملية الجارية تمنع إعادة التشغيل الآن — أعد المحاولة لاحقاً.";
            TryDeleteStagedUpdate(staged);
            return;
        }

        if (MessageBox.Show(this,
                $"تم تنزيل الإصدار {staged.Tag} والتحقق من سلامته.\nسيُغلق Locara الآن لتطبيق التحديث ثم يُعاد تشغيله تلقائياً (مع تراجع آمن عند الفشل).\n\nهل تريد إعادة التشغيل الآن؟",
                "إعادة التشغيل لتطبيق التحديث", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
        {
            Status.Text = "لم يُطبّق التحديث. يمكنك إعادة المحاولة في أي وقت.";
            TryDeleteStagedUpdate(staged);
            return;
        }

        try
        {
            AppSelfUpdater.LaunchSwapAndRestart(staged);
            Application.Current.Shutdown();
        }
        catch (Exception error)
        {
            Status.Text = "تعذّر بدء تطبيق التحديث: " + error.Message;
            MessageBox.Show(this, error.Message + "\n\nيمكنك تنزيل الإصدار يدوياً من صفحة الإصدار على GitHub.",
                "تعذّر تطبيق التحديث", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static void TryDeleteStagedUpdate(StagedAppUpdate staged)
    {
        try
        {
            if (Directory.Exists(staged.StagingDirectory))
                Directory.Delete(staged.StagingDirectory, recursive: true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Best-effort cleanup of the staging directory.
        }
    }
}
