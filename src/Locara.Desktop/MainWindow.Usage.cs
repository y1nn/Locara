using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Locara.Core;

namespace Locara.Desktop;

public partial class MainWindow
{
    private static string ModelPricingPath => Path.Combine(UserData, "model-pricing.json");
    private Dictionary<string, ModelPrice> customModelPrices = new(StringComparer.OrdinalIgnoreCase);
    private List<ModelUsageEntry> lastProjectUsage = [];

    private sealed record UsageDisplayRow(
        string Model,
        string Provider,
        string RequestsText,
        string InputText,
        string OutputText,
        string ThinkingText,
        string TotalText,
        string InputPriceText,
        string OutputPriceText,
        string CostText,
        double? InputPriceValue,
        double? OutputPriceValue);

    private void LoadCustomPricing()
    {
        customModelPrices = new(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(ModelPricingPath)) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, ModelPrice>>(File.ReadAllText(ModelPricingPath));
            if (loaded == null) return;
            foreach (var entry in loaded)
            {
                if (!string.IsNullOrWhiteSpace(entry.Key) && entry.Value != null)
                    customModelPrices[entry.Key.Trim()] = entry.Value;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveCustomPricing()
    {
        try
        {
            WriteProviderSetting(ModelPricingPath, JsonSerializer.Serialize(customModelPrices));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Status.Text = "تعذّر حفظ جدول الأسعار. " + error.Message;
        }
    }

    private (ModelPrice? Price, bool IsCustom) ResolveModelPrice(string model)
    {
        string key = (model ?? "").Trim();
        if (key.Length > 0 && customModelPrices.TryGetValue(key, out var custom))
            return (custom, true);
        return (ModelPricing.Suggest(key), false);
    }

    private static string FormatUsd(double amount) =>
        amount <= 0 ? "$0.00" : amount < 0.01 ? $"${amount:0.0000}" : $"${amount:0.00}";

    private void ApplyProjectUsage(IReadOnlyList<ModelUsageEntry> entries)
    {
        lastProjectUsage = entries.ToList();
        long totalReqs = 0, totalIn = 0, totalOut = 0, totalThink = 0, totalTokens = 0;
        double totalCost = 0;
        int unknownPriceModels = 0;
        var rows = new List<UsageDisplayRow>(entries.Count);

        foreach (var entry in entries)
        {
            totalReqs += entry.Requests;
            totalIn += entry.InputTokens;
            totalOut += entry.OutputTokens;
            totalThink += entry.ThinkingTokens;
            totalTokens += entry.TotalTokens;

            var (price, isCustom) = ResolveModelPrice(entry.Model);
            string inPriceText = "غير محدد";
            string outPriceText = "غير محدد";
            string costText = "—";
            if (price != null)
            {
                double cost = ModelPricing.CalculateCost(entry, price);
                totalCost += cost;
                string suffix = isCustom ? " ★" : "";
                inPriceText = $"${price.InputPerMillion:0.##}{suffix}";
                outPriceText = $"${price.OutputPerMillion:0.##}{suffix}";
                costText = FormatUsd(cost);
            }
            else if (entry.TotalTokens > 0)
            {
                unknownPriceModels++;
            }

            rows.Add(new UsageDisplayRow(
                Model: entry.Model,
                Provider: entry.Provider,
                RequestsText: entry.Requests.ToString("N0"),
                InputText: entry.InputTokens.ToString("N0"),
                OutputText: entry.OutputTokens.ToString("N0"),
                ThinkingText: entry.ThinkingTokens.ToString("N0"),
                TotalText: entry.TotalTokens.ToString("N0"),
                InputPriceText: inPriceText,
                OutputPriceText: outPriceText,
                CostText: costText,
                InputPriceValue: price?.InputPerMillion,
                OutputPriceValue: price?.OutputPerMillion));
        }

        string breakdown = totalThink > 0
            ? $"دخل: {totalIn:N0} · خرج: {totalOut:N0} · تفكير: {totalThink:N0}"
            : $"دخل: {totalIn:N0} · خرج: {totalOut:N0}";
        string costDisplay = totalTokens == 0 ? "$0.00"
            : unknownPriceModels > 0 && totalCost == 0 ? "حدد السعر"
            : unknownPriceModels > 0 ? $"{FormatUsd(totalCost)}+" : FormatUsd(totalCost);
        string costHint = unknownPriceModels > 0
            ? $"يوجد {unknownPriceModels} نموذج بلا سعر — حدّده من الجدول"
            : "تقديري حسب أسعار كل نموذج (قابل للتعديل)";

        OverviewTotalTokens.Text = totalTokens.ToString("N0");
        OverviewTokensBreakdown.Text = breakdown;
        OverviewTotalCost.Text = costDisplay;
        OverviewTotalRequests.Text = totalReqs > 0 ? $"{totalReqs:N0} طلب عبر {entries.Count} نموذج" : "0 طلب مسجّل";

        UsageSummaryTokens.Text = totalTokens.ToString("N0");
        UsageSummaryBreakdown.Text = breakdown;
        UsageSummaryCost.Text = costDisplay;
        UsageSummaryCostHint.Text = costHint;
        UsageSummaryRequests.Text = $"{totalReqs:N0} طلب";
        UsageLedgerGrid.ItemsSource = rows;

        if (string.IsNullOrWhiteSpace(PriceModelInput.Text) && rows.Count > 0)
            PopulatePriceEditor(rows[0]);
    }

    private void PopulatePriceEditor(UsageDisplayRow row)
    {
        PriceModelInput.Text = row.Model;
        PriceInputPerMillion.Text = row.InputPriceValue?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
        PriceOutputPerMillion.Text = row.OutputPriceValue?.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
    }

    private void SelectUsageRow(object sender, SelectionChangedEventArgs e)
    {
        if (UsageLedgerGrid.SelectedItem is UsageDisplayRow row)
            PopulatePriceEditor(row);
    }

    private void OpenUsagePanel(object sender, RoutedEventArgs e) => OpenUsageSettings();

    private void OpenUsagePanel(object sender, System.Windows.Input.MouseButtonEventArgs e) => OpenUsageSettings();

    private static bool TryParseUsd(string text, out double value)
    {
        string normalized = (text ?? "").Trim().Replace("$", "").Replace(",", ".");
        return double.TryParse(normalized, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value >= 0;
    }

    private void SaveModelPrice(object sender, RoutedEventArgs e)
    {
        string model = (PriceModelInput.Text ?? "").Trim();
        if (model.Length == 0)
        {
            Status.Text = "اختر نموذجاً من الجدول أو اكتب اسمه أولاً.";
            return;
        }
        if (!TryParseUsd(PriceInputPerMillion.Text, out double inPrice) || !TryParseUsd(PriceOutputPerMillion.Text, out double outPrice))
        {
            Status.Text = "أدخل سعراً رقمياً صالحاً (مثال: 0.30 للدخل و 2.50 للخرج لكل مليون توكن).";
            return;
        }
        customModelPrices[model] = new ModelPrice(inPrice, outPrice);
        SaveCustomPricing();
        ApplyProjectUsage(lastProjectUsage);
        Status.Text = $"حُفظ سعر النموذج «{model}» (${inPrice:0.##} دخل / ${outPrice:0.##} خرج لكل 1M توكن).";
    }

    private void ResetModelPrice(object sender, RoutedEventArgs e)
    {
        string model = (PriceModelInput.Text ?? "").Trim();
        if (model.Length == 0) return;
        customModelPrices.Remove(model);
        SaveCustomPricing();
        ApplyProjectUsage(lastProjectUsage);
        var (suggested, _) = ResolveModelPrice(model);
        PriceInputPerMillion.Text = suggested?.InputPerMillion.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
        PriceOutputPerMillion.Text = suggested?.OutputPerMillion.ToString("0.####", CultureInfo.InvariantCulture) ?? "";
        Status.Text = suggested != null
            ? $"أُعيد السعر الافتراضي للنموذج «{model}»."
            : $"أُزيل السعر المخصّص للنموذج «{model}».";
    }
}
