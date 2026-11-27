using System.IO;
using System.Windows;
using Locara.Core;
using Microsoft.Win32;

namespace Locara.Desktop;

/// <summary>Glossary JSON/CSV exchange and light <c>.locara-patch.json</c> translation patch exchange.</summary>
public partial class MainWindow
{
    private async void ExportGlossaryClick(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path || work != null || !SaveEdits()) return;
        var dialog = new SaveFileDialog
        {
            Title = "تصدير المعجم اليدوي",
            Filter = "ملف JSON (*.json)|*.json|ملف CSV (*.csv)|*.csv",
            FileName = "locara-glossary",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        string file = dialog.FileName;
        bool asCsv = Path.GetExtension(file).Equals(".csv", StringComparison.OrdinalIgnoreCase);

        try
        {
            int count = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return asCsv ? GlossaryExchange.ExportCsv(store, file) : GlossaryExchange.ExportJson(store, file);
            });
            if (!IsLoaded) return;
            Status.Text = $"صُدّر {count:N0} مصطلح من المعجم إلى «{Path.GetFileName(file)}».";
        }
        catch (Exception error) { Status.Text = error.Message; }
    }

    private async void ImportGlossaryClick(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path || work != null || !SaveEdits()) return;
        var dialog = new OpenFileDialog
        {
            Title = "استيراد معجم (JSON أو CSV)",
            Filter = "ملفات المعجم (*.json;*.csv)|*.json;*.csv|كل الملفات (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        string file = dialog.FileName;

        try
        {
            var preview = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return GlossaryExchange.PreviewImport(store, file);
            });
            if (!IsLoaded || projectPath != path) return;

            GlossaryImportMode? mode = PatchImportWindow.ShowGlossaryPreview(this, preview);
            if (mode is not { } chosenMode)
            {
                Status.Text = "أُلغي استيراد المعجم.";
                return;
            }

            var result = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return GlossaryExchange.ApplyImport(store, preview, chosenMode);
            });
            if (!IsLoaded || projectPath != path) return;

            ReloadTerms();
            Status.Text = $"اكتمل استيراد المعجم: أُضيف {result.Added:N0} · حُدّث {result.Updated:N0} · تُخطّي {result.Skipped:N0}.";
            await RunGlossaryAuditCoreAsync();
            RefreshQaGate();
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
            MessageBox.Show(this, error.Message, "استيراد المعجم", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ExportPatchClick(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path || work != null || !SaveEdits()) return;
        string ext = LocaraPatchExchange.DefaultFileExtension;
        var dialog = new SaveFileDialog
        {
            Title = "تصدير حزمة تعريب خفيفة",
            Filter = $"حزمة تعريب خفيفة (*{ext})|*{ext}",
            FileName = "translation" + ext,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        string file = dialog.FileName;
        if (!file.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
        {
            if (file.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                file = file[..^".json".Length];
            file += ext;
        }
        string target = file;

        try
        {
            var export = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return LocaraPatchExchange.ExportApprovedPatch(store, target);
            });
            if (!IsLoaded) return;
            Status.Text = $"صُدّرت {export.ExportedCount:N0} ترجمة معتمدة إلى «{Path.GetFileName(export.FilePath)}».";
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
            MessageBox.Show(this, error.Message, "تصدير حزمة التعريب", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void ImportPatchClick(object sender, RoutedEventArgs e)
    {
        if (projectPath is not { } path || work != null || !SaveEdits()) return;
        string ext = LocaraPatchExchange.DefaultFileExtension;
        var dialog = new OpenFileDialog
        {
            Title = "استيراد حزمة تعريب خفيفة",
            Filter = "حزمة تعريب خفيفة (*.json)|*.json|كل الملفات (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        string file = dialog.FileName;

        try
        {
            var initial = await Task.Run(() =>
            {
                using var store = new ProjectStore(path);
                return LocaraPatchExchange.PreviewImport(store, file, PatchMergeMode.FillEmptyOnly);
            });
            if (!IsLoaded || projectPath != path) return;

            var picked = PatchImportWindow.ShowPatchPreview(this, initial, mode =>
            {
                using var store = new ProjectStore(path);
                return LocaraPatchExchange.PreviewImport(store, file, mode);
            });
            if (picked is not { } choice)
            {
                Status.Text = $"أُلغي استيراد حزمة التعريب ({ext}).";
                return;
            }

            PatchImportPreview finalPreview = choice.Preview;
            bool approve = choice.ApproveDirectly;
            PatchImportResult? applied = null;
            await Run(async ct =>
            {
                applied = await Task.Run(() =>
                {
                    using var store = new ProjectStore(path);
                    return LocaraPatchExchange.ApplyImport(store, finalPreview, approve);
                }, ct);
                string state = applied.ApprovedDirectly ? "معتمدة مباشرة" : "بحالة «مستوردة» بانتظار المراجعة";
                Status.Text = $"استُورد {applied.Applied:N0} ترجمة ({state}) · تُخطّي {applied.Skipped:N0}.";
            });

            if (!IsLoaded || projectPath != path) return;
            if (applied is { Applied: > 0, ApprovedDirectly: true })
                SyncProjectApprovedToSharedMemoryBackground(path);
            UpdateOverview();
            RefreshQaGate();
        }
        catch (Exception error)
        {
            Status.Text = error.Message;
            MessageBox.Show(this, error.Message, "استيراد حزمة التعريب", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
