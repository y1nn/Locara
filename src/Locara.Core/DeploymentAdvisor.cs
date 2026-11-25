namespace Locara.Core;

public sealed record DeploymentAdvice(
    string Engine,
    string Strategy,
    string Summary,
    IReadOnlyList<string> Notes,
    bool SuggestXUnity,
    bool HasGettextPotOnly,
    IReadOnlyList<string> SiblingPoPaths);

/// <summary>
/// Layer 3 Deployment Intelligence.
/// Evaluates the scanned project's engine, active formats, and sibling localization assets
/// to surface clear, engine-aware deployment guidance before build and installation.
/// </summary>
public static class DeploymentAdvisor
{
    public static DeploymentAdvice Analyze(
        SourceCapability capability,
        IReadOnlyList<FileQualitySummary> scannedFiles)
    {
        var included = scannedFiles.Where(f => f.Included && f.IncludedUnits > 0).ToList();
        var notes = new List<string>();
        bool suggestXUnity = false;
        bool hasGettextPotOnly = false;
        var siblingPoPaths = new List<string>();

        int activeUnits = included.Sum(f => f.IncludedUnits);
        var activeFormats = included.Select(f => f.Format).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        string engine = NormalizeEngine(capability.Engine);

        // 1. Gettext .pot vs .po check (applies across Unity, Godot, Ren'Py, Unreal, and generic Gettext games)
        var includedPots = included.Where(f => f.Format == "po" && ProjectService.IsGettextTemplate(f.Path)).ToList();
        var includedPos = included.Where(f => f.Format == "po" && !ProjectService.IsGettextTemplate(f.Path)).ToList();
        if (includedPots.Count > 0 && includedPos.Count == 0)
        {
            hasGettextPotOnly = true;
            siblingPoPaths = scannedFiles
                .Where(f => f.Format == "po" && !ProjectService.IsGettextTemplate(f.Path))
                .Select(f => f.Path)
                .ToList();
            notes.Add(siblingPoPaths.Count > 0
                ? $"تنبيه Gettext: الترجمة الحالية على قالب (.pot) بينما توجد {siblingPoPaths.Count} ملفات لغات (.po) جاهزة في اللعبة. كثير من الألعاب لا تقرأ الترجمة من .pot مباشرة بل تحتاج ملف .po في مجلد اللغات أو استبدال أحد ملفات اللغة المثبّتة."
                : "تنبيه Gettext: الترجمة الحالية على قالب (.pot). تأكد من أن اللعبة تقرأ .pot مباشرة أو انسخ الناتج كملف .po في مجلد اللغات المدعوم باللعبة.");
        }

        // 2. Unity hardcoded / zero-extractable text check
        if (engine == "unity" && activeUnits == 0)
        {
            suggestXUnity = true;
            notes.Add("لم تُعثر على نصوص بشرية مفعّلة داخل موارد Unity المفتوحة أو المحزومة — يُرجّح أن النصوص مدمجة داخل الشيفرة البرمجية (C# / IL2CPP). استخدم «تصدير XUnity.AutoTranslator (BepInEx)» أو فعّل الملفات يدوياً من تبويب «الملفات».");
        }
        else if (engine == "unity" && activeFormats.Contains("unity-bundle"))
        {
            notes.Add("لعبة Unity Addressables: سيُحدّث Locara بصمات CRC32 والأحجام في catalog.bin / catalog.json تلقائياً ويحقن الخط العربي الاحتياطي في TextMeshPro.");
        }
        else if (engine == "unity" && (activeFormats.Contains("unity-i2") || activeFormats.Contains("unity-assets")))
        {
            notes.Add("سيحقن Locara خط TextMeshPro العربي تلقائياً مع دعم الالتفاف الذكي للأسطر وخيار تعطيل التشكيل للألعاب الداعمة لـ RTL أصلياً.");
        }

        // 3. Unreal Engine IoStore vs _P.pak note
        if (engine == "unreal")
        {
            notes.Add("يدعم Locara حزم UE4/UE5 عبر توليد حزمة تعديل *_Locara_P.pak أو التحديث المباشر لحزم UE5 IoStore (.pak + .utoc) مع حقن خط عربي+لاتيني ثنائي.");
        }

        // 4. Godot PCK v2/v3 & binary .translation note
        if (engine == "godot")
        {
            notes.Add("يعرض Godot 4 العربية بترميز UTF-8 المنطقي أصلياً؛ تُبنى حزم PCK v2/v3 وملفات .translation الثنائية (RSRC/RSCC) مع الحفاظ على جداول التجزئة.");
        }

        // 4b. GameMaker compiled vs external localization note
        if (engine == "gamemaker")
        {
            notes.Add(activeUnits == 0
                ? "هذه لعبة GameMaker وتخزّن نصوصها داخل حزمة data.win المجمّعة (مقطع STRG) دون ملفات ترجمة خارجية مفتوحة؛ إذا وفّرت اللعبة مجلد لغات خارجي (JSON/CSV/INI/PO) اختره أو فعّل ملفاته من تبويب «الملفات»."
                : "لعبة GameMaker بملفات ترجمة خارجية — تُبنى الملفات بترميز UTF-8 مع نسخ احتياطي واستعادة بضغطة زر.");
        }

        // 5. Mixed / manual override caution
        int unfilteredMixed = included.Count(f => !f.HumanOnlyFilter && f.Verdict is FileQualityVerdict.Mixed or FileQualityVerdict.Technical);
        if (unfilteredMixed > 0)
        {
            notes.Add($"تحذير سلامة: يوجد {unfilteredMixed} ملف مفعّل بتصنيف مختلط أو تقني دون تصفية حقول — يُنصح باستخدام «تحليل ذكي (AI Scout)» أو «تفعيل النصوص البشرية فقط» قبل الترجمة والبناء.");
        }

        string strategy = (engine, activeFormats) switch
        {
            (_, var fmts) when fmts.Contains("unreal-pak") || fmts.Contains("unreal-locres") || fmts.Contains("unreal-stringtable") => "unreal-pak-patch",
            (_, var fmts) when fmts.Contains("unity-i2") => "unity-i2-tmp",
            (_, var fmts) when fmts.Contains("unity-bundle") => "unity-addressables-tmp",
            (_, var fmts) when hasGettextPotOnly => "gettext-pot-to-po",
            (_, var fmts) when fmts.Contains("po") => "gettext-po",
            (_, var fmts) when fmts.Contains("godot-translation") => "godot-binary-translation",
            ("unity", _) when suggestXUnity => "unity-xunity-overlay",
            ("godot", _) => "godot-pck-or-loose",
            ("gamemaker", _) => activeUnits == 0 ? "gamemaker-compiled-datawin" : "gamemaker-external-files",
            ("rpgmaker", _) => "rpgmaker-data-json",
            ("renpy", _) => "renpy-tl-arabic",
            _ => "standard-in-place"
        };

        string summary = notes.Count > 0 ? notes[0] : capability.Deployment;
        return new DeploymentAdvice(capability.Engine, strategy, summary, notes, suggestXUnity, hasGettextPotOnly, siblingPoPaths);
    }

    private static string NormalizeEngine(string? engine) => (engine ?? "").Trim().ToLowerInvariant() switch
    {
        "godot" => "godot",
        "rpgmaker" or "rpg maker" => "rpgmaker",
        "renpy" or "ren'py" or "ren’py" => "renpy",
        "unity" or "star trucker" => "unity",
        "unreal" or "unreal engine" => "unreal",
        "gamemaker" or "game maker" => "gamemaker",
        var other => other
    };
}
