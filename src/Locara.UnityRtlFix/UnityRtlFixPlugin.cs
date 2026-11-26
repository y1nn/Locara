using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx;
using Locara.Core;

namespace Locara.UnityRtlFix
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public sealed class UnityRtlFixPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.locara.unity.rtlfix";
        public const string PluginName = "Locara Unity Classic RTL Fix";
        public const string PluginVersion = "1.4.0";

        private const int MaxCacheEntries = 2048;
        private static readonly object CacheLock = new object();
        private static readonly Dictionary<string, string> ReflowCache = new Dictionary<string, string>(StringComparer.Ordinal);

        [ThreadStatic] private static bool _inReflow;
        [ThreadStatic] private static string? _pendingLogicalOriginal;
        [ThreadStatic] private static bool _usedCachedLogical;

        private static bool _enabled = true;
        private static bool _fixUgui = true;
        private static bool _fixNgui = true;
        private static bool _fixPreShaped = true;
        private static bool _fixLogical = true;

        private static MethodInfo? _populateWithErrorsMethod;
        private static PropertyInfo? _textGenLinesProp;
        private static FieldInfo? _lineStartCharIdxField;
        private static FieldInfo? _settingsExtentsField;
        private static FieldInfo? _settingsFontSizeField;
        private static FieldInfo? _settingsHorizontalOverflowField;
        private static FieldInfo? _vector2XField;

        private object? _harmonyInstance;
        private MethodInfo? _unpatchSelfMethod;

        private void Awake()
        {
            try
            {
                LoadConfig();
                if (!_enabled)
                {
                    Logger.LogInfo("[Locara.UnityRtlFix] Disabled via Locara.UnityRtlFix.cfg.");
                    return;
                }

                ApplyHarmonyPatches();
            }
            catch (Exception ex)
            {
                Logger.LogError("[Locara.UnityRtlFix] Initialization failed: " + ex);
            }
        }

        private void OnDestroy()
        {
            try
            {
                _unpatchSelfMethod?.Invoke(_harmonyInstance, null);
            }
            catch { }
        }

        private void LoadConfig()
        {
            try
            {
                string configDir = Paths.ConfigPath;
                if (string.IsNullOrEmpty(configDir)) return;
                string cfgPath = Path.Combine(configDir, "Locara.UnityRtlFix.cfg");
                if (!File.Exists(cfgPath)) return;

                string[] lines = File.ReadAllLines(cfgPath);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[')
                        continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();
                    bool boolVal = val.Equals("true", StringComparison.OrdinalIgnoreCase) || val == "1";

                    if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) _enabled = boolVal;
                    else if (key.Equals("FixUGUI", StringComparison.OrdinalIgnoreCase)) _fixUgui = boolVal;
                    else if (key.Equals("FixNGUI", StringComparison.OrdinalIgnoreCase)) _fixNgui = boolVal;
                    else if (key.Equals("FixPreShapedArabic", StringComparison.OrdinalIgnoreCase)) _fixPreShaped = boolVal;
                    else if (key.Equals("FixLogicalArabic", StringComparison.OrdinalIgnoreCase)) _fixLogical = boolVal;
                }
            }
            catch { }
        }

        private void ApplyHarmonyPatches()
        {
            Type? harmonyType = FindType("HarmonyLib.Harmony");
            Type? harmonyMethodType = FindType("HarmonyLib.HarmonyMethod");
            if (harmonyType == null || harmonyMethodType == null)
            {
                Logger.LogWarning("[Locara.UnityRtlFix] HarmonyLib not found; skipping runtime RTL patches.");
                return;
            }

            _harmonyInstance = Activator.CreateInstance(harmonyType, new object[] { PluginGuid });
            _unpatchSelfMethod = harmonyType.GetMethod("UnpatchSelf", BindingFlags.Public | BindingFlags.Instance);
            MethodInfo? patchMethod = FindHarmonyPatchMethod(harmonyType);
            ConstructorInfo? hmCtor = harmonyMethodType.GetConstructor(new[] { typeof(MethodInfo) });
            if (_harmonyInstance == null || patchMethod == null || hmCtor == null)
                return;

            int patchedCount = 0;
            if (_fixUgui && TryPatchUnityTextGenerator(patchMethod, hmCtor))
                patchedCount++;
            if (_fixNgui && TryPatchNguiWrapText(patchMethod, hmCtor))
                patchedCount++;

            // Also subscribe to late-loaded assemblies (in case Assembly-CSharp / UI loads after Awake)
            AppDomain.CurrentDomain.AssemblyLoad += (_, args) =>
            {
                try
                {
                    if (_fixUgui && _populateWithErrorsMethod == null)
                        TryPatchUnityTextGenerator(patchMethod, hmCtor);
                    if (_fixNgui && args.LoadedAssembly != null)
                        TryPatchNguiInAssembly(args.LoadedAssembly, patchMethod, hmCtor);
                }
                catch { }
            };

            Logger.LogInfo("[Locara.UnityRtlFix] Active (" + patchedCount + " UI text layout subsystem(s) hooked).");
        }

        private bool TryPatchUnityTextGenerator(MethodInfo patchMethod, ConstructorInfo hmCtor)
        {
            Type? textGenType = FindType("UnityEngine.TextGenerator");
            if (textGenType == null) return false;

            _textGenLinesProp = textGenType.GetProperty("lines", BindingFlags.Public | BindingFlags.Instance);
            Type? uiLineInfoType = FindType("UnityEngine.UILineInfo");
            if (uiLineInfoType != null)
                _lineStartCharIdxField = uiLineInfoType.GetField("startCharIdx", BindingFlags.Public | BindingFlags.Instance);

            Type? settingsType = FindType("UnityEngine.TextGenerationSettings");
            if (settingsType != null)
            {
                _settingsExtentsField = settingsType.GetField("generationExtents", BindingFlags.Public | BindingFlags.Instance);
                _settingsFontSizeField = settingsType.GetField("fontSize", BindingFlags.Public | BindingFlags.Instance);
                _settingsHorizontalOverflowField = settingsType.GetField("horizontalOverflow", BindingFlags.Public | BindingFlags.Instance);
                if (_settingsExtentsField != null)
                    _vector2XField = _settingsExtentsField.FieldType.GetField("x", BindingFlags.Public | BindingFlags.Instance);
            }

            MethodInfo? target = null;
            MethodInfo[] methods = textGenType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == "PopulateWithErrors")
                {
                    ParameterInfo[] ps = methods[i].GetParameters();
                    if (ps.Length == 3 && ps[0].ParameterType == typeof(string))
                    {
                        target = methods[i];
                        break;
                    }
                }
            }

            if (target == null) return false;
            _populateWithErrorsMethod = target;

            MethodInfo prefixInfo = typeof(UnityRtlFixPlugin).GetMethod(nameof(TextGenPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
            MethodInfo postfixInfo = typeof(UnityRtlFixPlugin).GetMethod(nameof(TextGenPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;
            InvokeHarmonyPatch(patchMethod, hmCtor, target, prefixInfo, postfixInfo);
            return true;
        }

        private bool TryPatchNguiWrapText(MethodInfo patchMethod, ConstructorInfo hmCtor)
        {
            bool any = false;
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                if (TryPatchNguiInAssembly(assemblies[i], patchMethod, hmCtor))
                    any = true;
            }
            return any;
        }

        private static readonly HashSet<MethodInfo> PatchedNguiMethods = new HashSet<MethodInfo>();

        private bool TryPatchNguiInAssembly(Assembly asm, MethodInfo patchMethod, ConstructorInfo hmCtor)
        {
            Type? nguiText = asm.GetType("NGUIText", throwOnError: false);
            if (nguiText == null) return false;

            bool patched = false;
            MethodInfo[] methods = nguiText.GetMethods(BindingFlags.Public | BindingFlags.Static);
            MethodInfo prefixInfo = typeof(UnityRtlFixPlugin).GetMethod(nameof(NguiWrapPrefix), BindingFlags.NonPublic | BindingFlags.Static)!;
            MethodInfo postfixInfo = typeof(UnityRtlFixPlugin).GetMethod(nameof(NguiWrapPostfix), BindingFlags.NonPublic | BindingFlags.Static)!;

            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name != "WrapText" || PatchedNguiMethods.Contains(m)) continue;
                ParameterInfo[] ps = m.GetParameters();
                if (ps.Length >= 2 && ps[0].ParameterType == typeof(string)
                    && ps[1].ParameterType.IsByRef && ps[1].ParameterType.GetElementType() == typeof(string))
                {
                    InvokeHarmonyPatch(patchMethod, hmCtor, m, prefixInfo, postfixInfo);
                    PatchedNguiMethods.Add(m);
                    patched = true;
                }
            }
            return patched;
        }

        private void InvokeHarmonyPatch(MethodInfo patchMethod, ConstructorInfo hmCtor, MethodInfo target, MethodInfo? prefix, MethodInfo? postfix)
        {
            object? hmPrefix = prefix != null ? hmCtor.Invoke(new object[] { prefix }) : null;
            object? hmPostfix = postfix != null ? hmCtor.Invoke(new object[] { postfix }) : null;

            int paramCount = patchMethod.GetParameters().Length;
            object?[] args = new object?[paramCount];
            args[0] = target;
            if (paramCount > 1) args[1] = hmPrefix;
            if (paramCount > 2) args[2] = hmPostfix;
            for (int i = 3; i < paramCount; i++) args[i] = null;
            patchMethod.Invoke(_harmonyInstance, args);
        }

        // ==================== UGUI / TEXTMESH HARMONY HOOKS ====================

        private static void TextGenPrefix(ref string __0, object __1)
        {
            _pendingLogicalOriginal = null;
            _usedCachedLogical = false;
            if (_inReflow || string.IsNullOrEmpty(__0)) return;

            ClassicArabicTextKind kind = UnityClassicRtlReflow.DetectArabicKind(__0);
            if (kind == ClassicArabicTextKind.None) return;

            if (kind == ClassicArabicTextKind.PreShaped && _fixPreShaped)
            {
                string cacheKey = BuildCacheKey("P", __0, __1);
                if (TryGetCached(cacheKey, out string? cached) && cached != null)
                {
                    __0 = cached;
                    _usedCachedLogical = true;
                }
                return;
            }

            if (kind == ClassicArabicTextKind.Logical && _fixLogical)
            {
                string cacheKey = BuildCacheKey("L", __0, __1);
                if (TryGetCached(cacheKey, out string? cached) && cached != null)
                {
                    __0 = cached;
                    _usedCachedLogical = true;
                    return;
                }

                string shapedLogical = UnityClassicRtlReflow.ShapeLogicalForMeasurement(__0);
                _pendingLogicalOriginal = shapedLogical;
                __0 = shapedLogical;
            }
        }

        private static void TextGenPostfix(object __instance, string __0, object __1, object __2, ref bool __result)
        {
            if (_inReflow || _usedCachedLogical || string.IsNullOrEmpty(__0) || _populateWithErrorsMethod == null)
                return;

            string? pendingLogical = _pendingLogicalOriginal;
            _pendingLogicalOriginal = null;

            if (pendingLogical != null)
            {
                List<int>? starts = ReadLineStarts(__instance);
                string reflowed = starts != null && starts.Count > 1
                    ? UnityClassicRtlReflow.ReflowLogicalLines(pendingLogical, starts)
                    : UnityClassicRtlReflow.ShapeLogicalTextPerExplicitLine(pendingLogical);

                StoreCached(BuildCacheKey("L", pendingLogical, __1), reflowed);
                ReinvokePopulate(__instance, reflowed, __1, __2, ref __result);
                return;
            }

            if (!_fixPreShaped || UnityClassicRtlReflow.DetectArabicKind(__0) != ClassicArabicTextKind.PreShaped)
                return;

            // Check horizontalOverflow: 0 == HorizontalWrapMode.Wrap, 1 == Overflow
            if (IsHorizontalOverflowUnwrapped(__1))
                return;

            List<int>? lineStarts = ReadLineStarts(__instance);
            if (lineStarts == null || lineStarts.Count <= 1)
                return;

            string fixedPreShaped = UnityClassicRtlReflow.ReflowPreShapedLines(__0, lineStarts);
            if (string.Equals(fixedPreShaped, __0, StringComparison.Ordinal))
                return;

            StoreCached(BuildCacheKey("P", __0, __1), fixedPreShaped);
            ReinvokePopulate(__instance, fixedPreShaped, __1, __2, ref __result);
        }

        private static void ReinvokePopulate(object instance, string fixedText, object settingsBoxed, object contextObj, ref bool result)
        {
            if (_populateWithErrorsMethod == null) return;
            _inReflow = true;
            try
            {
                object? ret = _populateWithErrorsMethod.Invoke(instance, new[] { fixedText, settingsBoxed, contextObj });
                if (ret is bool b) result = b;
            }
            finally
            {
                _inReflow = false;
            }
        }

        private static List<int>? ReadLineStarts(object textGenerator)
        {
            if (_textGenLinesProp == null || _lineStartCharIdxField == null) return null;
            if (!(_textGenLinesProp.GetValue(textGenerator, null) is IList list) || list.Count == 0)
                return null;

            var starts = new List<int>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                object? item = list[i];
                if (item != null && _lineStartCharIdxField.GetValue(item) is int idx)
                    starts.Add(idx);
            }
            return starts;
        }

        private static bool IsHorizontalOverflowUnwrapped(object? settingsBoxed)
        {
            if (settingsBoxed == null || _settingsHorizontalOverflowField == null) return false;
            try
            {
                object? val = _settingsHorizontalOverflowField.GetValue(settingsBoxed);
                if (val != null && Convert.ToInt32(val) != 0) // 0 = Wrap, 1 = Overflow
                    return true;
            }
            catch { }
            return false;
        }

        private static string BuildCacheKey(string mode, string text, object? settingsBoxed)
        {
            int widthBucket = 0;
            int fontSize = 0;
            int overflow = 0;
            if (settingsBoxed != null)
            {
                try
                {
                    if (_settingsExtentsField != null && _vector2XField != null)
                    {
                        object? ext = _settingsExtentsField.GetValue(settingsBoxed);
                        if (ext != null && _vector2XField.GetValue(ext) is float fx)
                            widthBucket = (int)Math.Round(fx);
                    }
                    if (_settingsFontSizeField != null && _settingsFontSizeField.GetValue(settingsBoxed) is int fs)
                        fontSize = fs;
                    if (_settingsHorizontalOverflowField != null)
                        overflow = Convert.ToInt32(_settingsHorizontalOverflowField.GetValue(settingsBoxed));
                }
                catch { }
            }
            return mode + "|" + widthBucket + "|" + fontSize + "|" + overflow + "|" + text;
        }

        // ==================== NGUI HARMONY HOOKS ====================

        [ThreadStatic] private static string? _nguiOriginalArg;
        [ThreadStatic] private static ClassicArabicTextKind _nguiKind;

        private static void NguiWrapPrefix(ref string __0)
        {
            _nguiOriginalArg = null;
            _nguiKind = ClassicArabicTextKind.None;
            if (string.IsNullOrEmpty(__0)) return;

            ClassicArabicTextKind kind = UnityClassicRtlReflow.DetectArabicKind(__0);
            if (kind == ClassicArabicTextKind.Logical && _fixLogical)
            {
                string shapedLogical = UnityClassicRtlReflow.ShapeLogicalForMeasurement(__0);
                _nguiOriginalArg = shapedLogical;
                _nguiKind = ClassicArabicTextKind.Logical;
                __0 = shapedLogical;
            }
            else if (kind == ClassicArabicTextKind.PreShaped && _fixPreShaped)
            {
                _nguiOriginalArg = __0;
                _nguiKind = ClassicArabicTextKind.PreShaped;
            }
        }

        private static void NguiWrapPostfix(ref string __1)
        {
            string? orig = _nguiOriginalArg;
            ClassicArabicTextKind kind = _nguiKind;
            _nguiOriginalArg = null;
            _nguiKind = ClassicArabicTextKind.None;

            if (orig == null || string.IsNullOrEmpty(__1))
                return;

            if (kind == ClassicArabicTextKind.Logical)
            {
                __1 = UnityClassicRtlReflow.ShapeLogicalTextPerExplicitLine(__1);
            }
            else if (kind == ClassicArabicTextKind.PreShaped)
            {
                __1 = UnityClassicRtlReflow.ReflowPreShapedWrappedText(orig, __1);
            }
        }

        // ==================== REFLECTION & CACHE HELPERS ====================

        private static bool TryGetCached(string key, out string? value)
        {
            lock (CacheLock)
            {
                return ReflowCache.TryGetValue(key, out value);
            }
        }

        private static void StoreCached(string key, string value)
        {
            lock (CacheLock)
            {
                if (ReflowCache.Count >= MaxCacheEntries)
                    ReflowCache.Clear();
                ReflowCache[key] = value;
            }
        }

        private static Type? FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    Type? t = assemblies[i].GetType(fullName, throwOnError: false);
                    if (t != null) return t;
                }
                catch { }
            }
            return null;
        }

        private static MethodInfo? FindHarmonyPatchMethod(Type harmonyType)
        {
            MethodInfo[] methods = harmonyType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
            for (int i = 0; i < methods.Length; i++)
            {
                if (methods[i].Name == "Patch")
                {
                    ParameterInfo[] ps = methods[i].GetParameters();
                    if (ps.Length >= 3 && typeof(MethodBase).IsAssignableFrom(ps[0].ParameterType))
                        return methods[i];
                }
            }
            return null;
        }
    }
}
