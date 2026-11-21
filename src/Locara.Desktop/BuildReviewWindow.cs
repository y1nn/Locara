using System.Windows;
using System.Windows.Controls;
using Locara.Core;

namespace Locara.Desktop;

internal sealed class BuildReviewWindow : Window
{
    internal BuildReviewWindow(PackagePreview plan, bool automaticOutput, string? trialDestination = null,
        GameLaunch launch = GameLaunch.None)
    {
        Title = launch switch
        {
            GameLaunch.Steam => "تجربة Star Trucker عبر Steam",
            GameLaunch.GodotPack => "تجربة لعبة Godot",
            GameLaunch.RenpyCopy => "تجربة لعبة Ren’Py",
            _ => plan.Final ? "مراجعة الحزمة النهائية" : "مراجعة حزمة التجربة"
        };
        Width = 730;
        Height = 460;
        MinWidth = 500;
        MinHeight = 310;
        FlowDirection = FlowDirection.RightToLeft;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var layout = new DockPanel { Margin = new Thickness(18) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var build = new Button
        {
            Content = trialDestination != null ? "تشغيل التجربة" : automaticOutput ? "بناء الحزمة" : "اختيار مجلد المخرجات",
            IsDefault = trialDestination == null
        };
        build.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(build);
        buttons.Children.Add(new Button { Content = "إلغاء", IsCancel = true });
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(buttons);

        var summary = new TextBlock
        {
            Text = $"{plan.Resources.Sum(resource => resource.Translations)} نص مؤهل للبناء · {plan.ChangedFiles.Count} ملف سيتغير"
                + (plan.Package.Length > 0 ? "\n" + plan.Package : "")
                + (plan.Notice.Length > 0 ? "\n" + plan.Notice : "")
                + (launch == GameLaunch.Steam
                    ? "\nستُطبَّق الحزمة مؤقتاً على تثبيت Steam التالي. يستعيد Locara الأصل بعد خروج اللعبة.\n" + trialDestination
                    : trialDestination != null ? "\nستُشغّل التجربة من المسار المستقل التالي.\n" + trialDestination : "")
                + (plan.Blocked > 0 ? $"\nالنصوص المستبعدة من البناء — {plan.Blocked}" : ""),
            TextWrapping = TextWrapping.Wrap
        };
        DockPanel.SetDock(summary, Dock.Top);
        layout.Children.Add(summary);
        layout.Children.Add(new ListBox
        {
            FlowDirection = FlowDirection.LeftToRight,
            ItemsSource = plan.ChangedFiles
        });
        Content = layout;
    }
}
