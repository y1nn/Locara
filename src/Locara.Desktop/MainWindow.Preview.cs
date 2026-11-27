using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Locara.Core;

namespace Locara.Desktop;

/// <summary>Live in-game Arabic preview card inside the editor inspector.</summary>
public partial class MainWindow
{
    private int livePreviewBoxWidth = 45;
    private ArabicPreviewMode livePreviewMode = ArabicPreviewMode.PreShaped;

    private void RefreshEditorLivePreview()
    {
        if (InspectorLivePreviewText == null || InspectorLivePreviewSummary == null) return;

        bool preShaped = livePreviewMode == ArabicPreviewMode.PreShaped;
        InspectorLivePreviewText.FlowDirection = preShaped ? FlowDirection.LeftToRight : FlowDirection.RightToLeft;
        InspectorLivePreviewText.TextAlignment = preShaped ? TextAlignment.Right : TextAlignment.Left;
        if (activePreviewFontFamily != null)
            InspectorLivePreviewText.FontFamily = activePreviewFontFamily;

        string text = InspectorTargetBox?.Text ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            InspectorLivePreviewText.Text = "";
            InspectorLivePreviewSummary.Text = "اكتب الترجمة العربية لرؤية شكلها داخل صندوق اللعبة.";
            InspectorLivePreviewSummary.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            return;
        }

        var result = ArabicPreviewFormatter.Format(text, livePreviewBoxWidth, livePreviewMode, scannedEngine);
        InspectorLivePreviewText.Text = result.DisplayText;

        string lineWord = result.LineCount == 1 ? "سطر" : "أسطر";
        string summary = string.Create(CultureInfo.InvariantCulture,
            $"{result.LineCount} {lineWord} · أطول سطر {result.MaxVisibleLineLength}/{result.BoxWidthChars} حرفاً");
        if (result.ExceedsBoxWidth)
            summary += " — يتجاوز عرض الصندوق";
        InspectorLivePreviewSummary.Text = summary;
        InspectorLivePreviewSummary.SetResourceReference(TextBlock.ForegroundProperty,
            result.ExceedsBoxWidth ? "DangerFg" : "SuccessFg");
    }

    private void PreviewBoxWidthClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }
            || !int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out int width))
            return;
        livePreviewBoxWidth = width;
        RefreshEditorLivePreview();
    }

    private void PreviewModeClick(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag }) return;
        livePreviewMode = tag == "logical" ? ArabicPreviewMode.Logical : ArabicPreviewMode.PreShaped;
        RefreshEditorLivePreview();
    }
}
