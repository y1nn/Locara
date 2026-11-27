using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Locara.Core;

namespace Locara.Desktop;

internal sealed class PatchImportWindow : Window
{
    private sealed class GlossaryRowView(GlossaryImportEntry entry)
    {
        public string Source => entry.Source;
        public string ExistingTarget => entry.ExistingTarget;
        public string Target => entry.Target;
        public GlossaryEntryState State => entry.State;
        public string Note => entry.Note;

        public string StateLabel => entry.State switch
        {
            GlossaryEntryState.New => "جديد",
            GlossaryEntryState.Identical => "مطابق",
            GlossaryEntryState.Conflict => "تعارض",
            GlossaryEntryState.DuplicateInFile => "مكرر بالملف",
            _ => "غير صالح"
        };
    }

    private sealed class PatchRowView(PatchImportEntry entry, Action onToggle) : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public PatchImportEntry Entry { get; } = entry;
        public bool CanInclude => Entry.Status == PatchEntryStatus.Ready;
        public string Path => Entry.Path;
        public string Source => Entry.Source;
        public string CurrentTarget => Entry.CurrentTarget;
        public string ProposedTarget => Entry.ProposedTarget;
        public PatchEntryStatus Status => Entry.Status;
        public string Issue => Entry.Issue;

        public bool Include
        {
            get => Entry.Include;
            set
            {
                bool effective = CanInclude && value;
                if (Entry.Include == effective) return;
                Entry.Include = effective;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Include)));
                onToggle();
            }
        }

        public string StatusLabel => Entry.Status switch
        {
            PatchEntryStatus.Ready => "جاهز",
            PatchEntryStatus.SkippedExisting => "مترجم مسبقاً",
            PatchEntryStatus.Identical => "مطابق",
            PatchEntryStatus.TokenError => "خطأ رموز",
            PatchEntryStatus.SourceMismatch => "تغيّر الأصل",
            _ => "غير موجود"
        };
    }

    public GlossaryImportMode SelectedGlossaryMode { get; private set; } = GlossaryImportMode.AddMissingOnly;
    public PatchMergeMode SelectedPatchMode { get; private set; } = PatchMergeMode.FillEmptyOnly;
    public bool ApproveImportedDirectly { get; private set; }
    public PatchImportPreview? CurrentPatchPreview { get; private set; }

    internal static GlossaryImportMode? ShowGlossaryPreview(Window? owner, GlossaryImportPreview preview)
    {
        var dialog = new PatchImportWindow(preview);
        if (owner != null)
            dialog.Owner = owner;
        return dialog.ShowDialog() == true ? dialog.SelectedGlossaryMode : null;
    }

    internal static (PatchImportPreview Preview, bool ApproveDirectly)? ShowPatchPreview(
        Window? owner,
        PatchImportPreview initialPreview,
        Func<PatchMergeMode, PatchImportPreview>? reloadPreview = null)
    {
        var dialog = new PatchImportWindow(initialPreview, reloadPreview);
        if (owner != null)
            dialog.Owner = owner;
        if (dialog.ShowDialog() == true && dialog.CurrentPatchPreview != null)
            return (dialog.CurrentPatchPreview, dialog.ApproveImportedDirectly);
        return null;
    }

    internal PatchImportWindow(GlossaryImportPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ConfigureWindow("معاينة استيراد القاموس", 980, 620, 680, 420);

        var layout = new DockPanel { Margin = new Thickness(20, 18, 20, 18) };

        var summaryText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        var modeHint = new TextBlock
        {
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        modeHint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var addMissingRadio = new RadioButton
        {
            Content = "إضافة الجديد فقط",
            IsChecked = true,
            GroupName = "GlossaryImportMode",
            Margin = new Thickness(0, 0, 18, 0)
        };
        var overwriteRadio = new RadioButton
        {
            Content = "تحديث المصطلحات المتعارضة",
            GroupName = "GlossaryImportMode"
        };
        var modePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        modePanel.Children.Add(addMissingRadio);
        modePanel.Children.Add(overwriteRadio);

        var headerStack = new StackPanel();
        headerStack.Children.Add(summaryText);
        headerStack.Children.Add(modeHint);
        headerStack.Children.Add(modePanel);

        var summaryCard = new Border { Margin = new Thickness(0, 0, 0, 12), Child = headerStack };
        summaryCard.SetResourceReference(StyleProperty, "SubtleCardBorderStyle");
        DockPanel.SetDock(summaryCard, Dock.Top);
        layout.Children.Add(summaryCard);

        var applyButton = new Button
        {
            Content = "استيراد القاموس",
            IsDefault = true
        };
        applyButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        applyButton.Click += (_, _) => DialogResult = true;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(applyButton);
        actions.Children.Add(new Button { Content = "إلغاء", IsCancel = true });
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);

        void RefreshGlossaryState()
        {
            SelectedGlossaryMode = overwriteRadio.IsChecked == true
                ? GlossaryImportMode.OverwriteExisting
                : GlossaryImportMode.AddMissingOnly;

            int applicable = preview.NewCount
                + (SelectedGlossaryMode == GlossaryImportMode.OverwriteExisting ? preview.ConflictCount : 0);

            summaryText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "المصدر: {0}   ·   جديد: {1:N0}   ·   تعارض: {2:N0}   ·   مطابق: {3:N0}   ·   مكرر: {4:N0}   ·   غير صالح: {5:N0}",
                preview.SourceFormat.ToUpperInvariant(),
                preview.NewCount,
                preview.ConflictCount,
                preview.IdenticalCount,
                preview.DuplicateCount,
                preview.InvalidCount);

            modeHint.Text = SelectedGlossaryMode == GlossaryImportMode.OverwriteExisting
                ? $"سيُضاف {preview.NewCount:N0} مصطلح جديد ويُحدَّث {preview.ConflictCount:N0} مصطلح متعارض."
                : $"سيُضاف {preview.NewCount:N0} مصطلح جديد فقط مع الإبقاء على {preview.ConflictCount:N0} مصطلح متعارض كما هو.";

            applyButton.IsEnabled = applicable > 0;
            applyButton.Content = applicable > 0
                ? $"استيراد ({applicable:N0} مصطلح)"
                : "لا توجد مصطلحات للاستيراد";
        }

        addMissingRadio.Checked += (_, _) => RefreshGlossaryState();
        overwriteRadio.Checked += (_, _) => RefreshGlossaryState();
        RefreshGlossaryState();

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            IsReadOnly = true,
            RowHeight = 38,
            ItemsSource = preview.Entries.Select(entry => new GlossaryRowView(entry)).ToList()
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "المصطلح",
            Binding = new Binding(nameof(GlossaryRowView.Source)),
            Width = new DataGridLength(1.1, DataGridLengthUnitType.Star),
            ElementStyle = CreateLtrCellStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الترجمة الحالية",
            Binding = new Binding(nameof(GlossaryRowView.ExistingTarget)),
            Width = new DataGridLength(1.0, DataGridLengthUnitType.Star),
            ElementStyle = CreateRtlCellStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الترجمة المستوردة",
            Binding = new Binding(nameof(GlossaryRowView.Target)),
            Width = new DataGridLength(1.0, DataGridLengthUnitType.Star),
            ElementStyle = CreateRtlCellStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الحالة",
            Binding = new Binding(nameof(GlossaryRowView.StateLabel)),
            Width = 115,
            ElementStyle = CreateGlossaryStateStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "ملاحظة",
            Binding = new Binding(nameof(GlossaryRowView.Note)),
            Width = new DataGridLength(1.3, DataGridLengthUnitType.Star),
            ElementStyle = CreateRtlCellStyle()
        });

        layout.Children.Add(grid);
        Content = layout;
    }

    internal PatchImportWindow(
        PatchImportPreview initialPreview,
        Func<PatchMergeMode, PatchImportPreview>? reloadPreview = null)
    {
        ArgumentNullException.ThrowIfNull(initialPreview);
        ConfigureWindow("معاينة استيراد حزمة التعريب (.locara-patch.json)", 1120, 660, 740, 440);

        CurrentPatchPreview = initialPreview;
        SelectedPatchMode = initialPreview.Mode;

        var layout = new DockPanel { Margin = new Thickness(20, 18, 20, 18) };

        var summaryText = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        var metaText = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };
        metaText.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondary");

        var fillEmptyRadio = new RadioButton
        {
            Content = "ملء الفارغ فقط",
            IsChecked = initialPreview.Mode == PatchMergeMode.FillEmptyOnly,
            GroupName = "PatchMergeMode",
            Margin = new Thickness(0, 0, 18, 0)
        };
        var updateAllRadio = new RadioButton
        {
            Content = "تحديث الكل مع فحص الرموز",
            IsChecked = initialPreview.Mode == PatchMergeMode.UpdateAll,
            GroupName = "PatchMergeMode"
        };
        if (reloadPreview == null)
        {
            fillEmptyRadio.IsEnabled = initialPreview.Mode == PatchMergeMode.FillEmptyOnly;
            updateAllRadio.IsEnabled = initialPreview.Mode == PatchMergeMode.UpdateAll;
        }

        var modePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 0)
        };
        modePanel.Children.Add(fillEmptyRadio);
        modePanel.Children.Add(updateAllRadio);

        var approveCheckBox = new CheckBox
        {
            Content = "اعتماد السطور المستوردة مباشرة (approved)",
            IsChecked = false,
            Margin = new Thickness(0, 8, 0, 0)
        };
        approveCheckBox.Checked += (_, _) => ApproveImportedDirectly = true;
        approveCheckBox.Unchecked += (_, _) => ApproveImportedDirectly = false;

        var headerStack = new StackPanel();
        headerStack.Children.Add(summaryText);
        headerStack.Children.Add(metaText);
        headerStack.Children.Add(modePanel);
        headerStack.Children.Add(approveCheckBox);

        var summaryCard = new Border { Margin = new Thickness(0, 0, 0, 12), Child = headerStack };
        summaryCard.SetResourceReference(StyleProperty, "SubtleCardBorderStyle");
        DockPanel.SetDock(summaryCard, Dock.Top);
        layout.Children.Add(summaryCard);

        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            CanUserAddRows = false,
            RowHeight = 40
        };

        var applyButton = new Button
        {
            Content = "تطبيق الحزمة",
            IsDefault = true
        };
        applyButton.SetResourceReference(StyleProperty, "PrimaryButtonStyle");
        applyButton.Click += (_, _) =>
        {
            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            ApproveImportedDirectly = approveCheckBox.IsChecked == true;
            DialogResult = true;
        };

        var rowViews = new List<PatchRowView>();

        void UpdateApplyButton()
        {
            int selectedCount = rowViews.Count(row => row.Include && row.CanInclude);
            applyButton.IsEnabled = selectedCount > 0;
            applyButton.Content = selectedCount > 0
                ? $"تطبيق المحدد ({selectedCount:N0} سطر)"
                : "لا توجد سطور جاهزة للتطبيق";
        }

        void BindPreview(PatchImportPreview preview)
        {
            CurrentPatchPreview = preview;
            SelectedPatchMode = preview.Mode;

            summaryText.Text = string.Format(
                CultureInfo.InvariantCulture,
                "جاهز: {0:N0}   ·   مترجم مسبقاً: {1:N0}   ·   مطابق: {2:N0}   ·   خطأ رموز: {3:N0}   ·   تغيّر الأصل: {4:N0}   ·   غير موجود: {5:N0}",
                preview.ReadyCount,
                preview.SkippedExistingCount,
                preview.IdenticalCount,
                preview.TokenErrorCount,
                preview.SourceMismatchCount,
                preview.NotFoundCount);

            var metaParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(preview.PatchEngine))
                metaParts.Add("المحرك: " + preview.PatchEngine);
            if (!string.IsNullOrWhiteSpace(preview.PatchVersion))
                metaParts.Add("إصدار Locara: " + preview.PatchVersion);
            if (preview.ExportedAtUtc.HasValue)
                metaParts.Add("تاريخ التصدير: " + preview.ExportedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            metaParts.Add("الافتراضي يحفظ السطور بحالة مستوردة (imported) للمراجعة.");
            metaText.Text = string.Join("   ·   ", metaParts);

            rowViews = preview.Entries
                .Select(entry => new PatchRowView(entry, UpdateApplyButton))
                .ToList();
            grid.ItemsSource = rowViews;
            UpdateApplyButton();
        }

        void SwitchPatchMode(PatchMergeMode newMode)
        {
            if (reloadPreview == null || SelectedPatchMode == newMode)
                return;

            grid.CommitEdit(DataGridEditingUnit.Cell, true);
            grid.CommitEdit(DataGridEditingUnit.Row, true);
            BindPreview(reloadPreview(newMode));
        }

        fillEmptyRadio.Checked += (_, _) => SwitchPatchMode(PatchMergeMode.FillEmptyOnly);
        updateAllRadio.Checked += (_, _) => SwitchPatchMode(PatchMergeMode.UpdateAll);

        var selectReadyButton = new Button { Content = "تحديد الجاهز" };
        selectReadyButton.Click += (_, _) =>
        {
            foreach (var row in rowViews)
                row.Include = row.CanInclude;
            UpdateApplyButton();
        };

        var clearAllButton = new Button { Content = "إلغاء التحديد" };
        clearAllButton.Click += (_, _) =>
        {
            foreach (var row in rowViews)
                row.Include = false;
            UpdateApplyButton();
        };

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        actions.Children.Add(applyButton);
        actions.Children.Add(selectReadyButton);
        actions.Children.Add(clearAllButton);
        actions.Children.Add(new Button { Content = "إلغاء", IsCancel = true });
        DockPanel.SetDock(actions, Dock.Bottom);
        layout.Children.Add(actions);

        var checkBoxStyle = new Style(typeof(CheckBox));
        checkBoxStyle.Setters.Add(new Setter(HorizontalAlignmentProperty, HorizontalAlignment.Center));
        checkBoxStyle.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        var disableTrigger = new DataTrigger
        {
            Binding = new Binding(nameof(PatchRowView.CanInclude)),
            Value = false
        };
        disableTrigger.Setters.Add(new Setter(IsEnabledProperty, false));
        checkBoxStyle.Triggers.Add(disableTrigger);

        grid.Columns.Add(new DataGridCheckBoxColumn
        {
            Header = "تطبيق",
            Binding = new Binding(nameof(PatchRowView.Include)) { UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
            Width = 60,
            ElementStyle = checkBoxStyle,
            EditingElementStyle = checkBoxStyle
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الملف",
            Binding = new Binding(nameof(PatchRowView.Path)),
            IsReadOnly = true,
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
            ElementStyle = CreateLtrCellStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الأصل",
            Binding = new Binding(nameof(PatchRowView.Source)),
            IsReadOnly = true,
            Width = new DataGridLength(1.1, DataGridLengthUnitType.Star),
            ElementStyle = CreateLtrCellStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الحالي",
            Binding = new Binding(nameof(PatchRowView.CurrentTarget)),
            IsReadOnly = true,
            Width = new DataGridLength(0.9, DataGridLengthUnitType.Star),
            ElementStyle = CreateRtlCellStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "المقترح",
            Binding = new Binding(nameof(PatchRowView.ProposedTarget)),
            IsReadOnly = true,
            Width = new DataGridLength(1.1, DataGridLengthUnitType.Star),
            ElementStyle = CreateRtlCellStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "الحالة",
            Binding = new Binding(nameof(PatchRowView.StatusLabel)),
            IsReadOnly = true,
            Width = 105,
            ElementStyle = CreatePatchStatusStyle()
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "التنبيه",
            Binding = new Binding(nameof(PatchRowView.Issue)),
            IsReadOnly = true,
            Width = new DataGridLength(1.2, DataGridLengthUnitType.Star),
            ElementStyle = CreateRtlCellStyle()
        });

        BindPreview(initialPreview);

        layout.Children.Add(grid);
        Content = layout;
    }

    private void ConfigureWindow(string title, double width, double height, double minWidth, double minHeight)
    {
        Title = title;
        Width = width;
        Height = height;
        MinWidth = minWidth;
        MinHeight = minHeight;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.AttachWindow(this);
    }

    private static Style CreateLtrCellStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(FlowDirectionProperty, FlowDirection.LeftToRight));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 8, 0)));
        return style;
    }

    private static Style CreateRtlCellStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 8, 0)));
        return style;
    }

    private static Style CreateGlossaryStateStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 8, 0)));
        style.Triggers.Add(CreateColorTrigger(nameof(GlossaryRowView.State), GlossaryEntryState.New, "SuccessFg"));
        style.Triggers.Add(CreateColorTrigger(nameof(GlossaryRowView.State), GlossaryEntryState.Conflict, "WarningFg"));
        style.Triggers.Add(CreateColorTrigger(nameof(GlossaryRowView.State), GlossaryEntryState.Identical, "TextMuted"));
        style.Triggers.Add(CreateColorTrigger(nameof(GlossaryRowView.State), GlossaryEntryState.DuplicateInFile, "DangerFg"));
        style.Triggers.Add(CreateColorTrigger(nameof(GlossaryRowView.State), GlossaryEntryState.Invalid, "DangerFg"));
        return style;
    }

    private static Style CreatePatchStatusStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        style.Setters.Add(new Setter(VerticalAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(8, 0, 8, 0)));
        style.Triggers.Add(CreateColorTrigger(nameof(PatchRowView.Status), PatchEntryStatus.Ready, "SuccessFg"));
        style.Triggers.Add(CreateColorTrigger(nameof(PatchRowView.Status), PatchEntryStatus.SkippedExisting, "WarningFg"));
        style.Triggers.Add(CreateColorTrigger(nameof(PatchRowView.Status), PatchEntryStatus.Identical, "TextMuted"));
        style.Triggers.Add(CreateColorTrigger(nameof(PatchRowView.Status), PatchEntryStatus.TokenError, "DangerFg"));
        style.Triggers.Add(CreateColorTrigger(nameof(PatchRowView.Status), PatchEntryStatus.SourceMismatch, "DangerFg"));
        style.Triggers.Add(CreateColorTrigger(nameof(PatchRowView.Status), PatchEntryStatus.NotFound, "TextMuted"));
        return style;
    }

    private static DataTrigger CreateColorTrigger(string propertyName, object matchValue, string resourceKey)
    {
        var trigger = new DataTrigger
        {
            Binding = new Binding(propertyName),
            Value = matchValue
        };
        trigger.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension(resourceKey)));
        return trigger;
    }
}
