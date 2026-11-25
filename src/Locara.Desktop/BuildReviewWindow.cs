using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Locara.Core;

namespace Locara.Desktop;

internal sealed class BuildReviewWindow : Window
{
    internal BuildReviewWindow(PackagePreview plan, bool automaticOutput, string? trialDestination = null,
        GameLaunch launch = GameLaunch.None)
    {
        Title = launch switch
        {
            GameLaunch.Steam => "تجربة اللعبة عبر Steam",
            GameLaunch.GodotPack => "تجربة لعبة Godot",
            GameLaunch.RenpyCopy => "تجربة لعبة Ren’Py",
            _ => plan.Final ? "مراجعة الحزمة النهائية" : "مراجعة حزمة التجربة"
        };
        Width = 740;
        Height = 480;
        MinWidth = 500;
        MinHeight = 320;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ThemeManager.AttachWindow(this);

        var layout = new DockPanel { Margin = new Thickness(20, 18, 20, 18) };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 12, 0, 0)
        };
        var build = new Button
        {
            Content = trialDestination != null ? "تشغيل التجربة الآن" : automaticOutput ? "بناء الحزمة" : "اختيار مجلد المخرجات",
            IsDefault = trialDestination == null,
            Style = (Style)Application.Current.FindResource("PrimaryButtonStyle")
        };
        build.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(build);
        buttons.Children.Add(new Button { Content = "إلغاء", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);

        var summaryCard = new Border
        {
            Style = (Style)Application.Current.FindResource("SubtleCardBorderStyle"),
            Margin = new Thickness(0, 0, 0, 12),
            Child = new TextBlock
            {
                Text = $"{plan.Resources.Sum(resource => resource.Translations):N0} نص مؤهل للبناء  ·  {plan.ChangedFiles.Count:N0} ملف سيتغير"
                    + (plan.Package.Length > 0 ? "\n" + plan.Package : "")
                    + (plan.Notice.Length > 0 ? "\n" + plan.Notice : "")
                    + (launch == GameLaunch.Steam
                        ? "\nستُطبَّق الحزمة مؤقتاً على تثبيت Steam التالي، ويستعيد Locara الأصل تلقائياً بعد إغلاق اللعبة:\n" + trialDestination
                        : trialDestination != null ? "\nستُشغّل التجربة من المسار المستقل التالي:\n" + trialDestination : "")
                    + (plan.Blocked > 0 ? $"\nالنصوص المستبعدة من البناء: {plan.Blocked:N0}" : ""),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            }
        };
        DockPanel.SetDock(summaryCard, Dock.Top);
        layout.Children.Add(summaryCard);

        var filesList = new ListBox
        {
            FlowDirection = FlowDirection.LeftToRight,
            ItemsSource = plan.ChangedFiles
        };
        if (Application.Current.Resources["MonoFont"] is FontFamily mono)
            filesList.FontFamily = mono;
        layout.Children.Add(filesList);
        Content = layout;
    }
}
