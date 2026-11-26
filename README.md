<p align="center">
  <img src="docs/assets/locara-banner.jpg" alt="Locara — PC Game Arabic Localization Studio" width="100%" />
</p>

<div dir="rtl">

# Locara

أداة مجانية ومفتوحة المصدر لتعريب ألعاب الحاسوب على نظام Windows، تعمل بالكامل على جهازك دون أي خوادم خارجية.

تساعدك Locara على استخراج نصوص اللعبة، وترجمتها يدوياً أو باستخدام مفتاح الذكاء الاصطناعي الخاص بك، ثم تجهيز الخط العربي وعرض الحروف المتصلة بشكل سليم، وتجربة التعريب أو تثبيته مع نسخة احتياطية يمكن استعادتها بضغطة زر.

---

## التحميل والتشغيل

1. نزّل ملف `Locara-windows-x64.zip` من صفحة [الإصدارات (Releases)](../../releases/latest).
2. فك الضغط في أي مجلد وشغّل `Locara.Desktop.exe` مباشرة (لا يحتاج تثبيت .NET).

لدليل الاستخدام الكامل راجع [docs/Windows-Release.md](docs/Windows-Release.md).

---

## الألعاب والصيغ المدعومة

- **Unity:** جداول I2 Localization، حزم Addressables (`.bundle`)، النصوص المدمجة في موارد اللعبة، ملفات `XUnity.AutoTranslator` (BepInEx)، والملفات النصية المفتوحة — مع حقن خط عربي تلقائي، وتشكيل الحروف، والتفاف مناسب للأسطر، وخيار تعطيل التشكيل للألعاب التي تدعم العربية أصلاً.
- **Unreal Engine 4 & 5:** ملفات `.locres` وجداول `StringTable` وحزم `.pak` (بما فيها الحزم المشفّرة بـ AES-256 عند توفر المفتاح)، مع توليد حزمة تعديل جاهزة `*_Locara_P.pak` أو تحديث حزم UE5 IoStore، وحقن خط عربي+لاتيني تلقائي.
- **Godot 3 & 4:** حزم `.pck` (بما فيها Godot 4.5+) وملفات `.translation` الثنائية وملفات النصوص القياسية.
- **محرّكات أخرى:** Ren’Py، وRPG Maker MV/MZ، وGameMaker (للملفات الخارجية).
- **ملفات النصوص والأرشيفات:** JSON / JSONC، CSV / TSV، XML / XLIFF، INI، YAML، TOML، `.strings`، Lua، `.txt` داخل مجلدات اللغات، PO / POT، XLSX، وأرشيفات ZIP.

> **ملاحظة:** الألعاب التي تستخدم محركاً خاصاً وتخزّن نصوصها داخل حزم ثنائية مخصّصة (مثل `data.pack`) أو داخل الملف التنفيذي مباشرة بدون ملفات لغة مفتوحة لا يمكن استخراج نصوصها تلقائياً.

---

## المزايا الرئيسية

- **تصفية تلقائية للملفات:** يميّز ملفات الترجمة الفعلية عن ملفات الإعدادات التقنية واللغات الأخرى، ويتيح لك التحكم بكل ملف قبل بدء الترجمة.
- **تحرير يدوي أو بمساعدة الذكاء الاصطناعي:** محرّر مدمج مع معجم مصطلحات، وتصدير/استيراد CSV، وإمكانية الترجمة والمراجعة عبر Gemini أو Anthropic أو DeepSeek أو Azure أو أي خادم متوافق مع OpenAI باستخدام مفتاحك الخاص (يُحفظ بأمان في Windows Credential Manager).
- **حماية رموز اللعبة:** يحافظ تلقائياً على المتغيرات والألوان والوسوم (`{0}`، `%s`، `<color>`، `[tag]`) حتى لا تتعطل اللعبة بعد التعريب.
- **خطوط عربية جاهزة:** يأتي مع خطوط عربية مفتوحة المصدر (Noto Sans Arabic، Noto Naskh Arabic، IBM Plex Sans Arabic، Amiri)، ويمكنك إضافة أي خط `.ttf` أو `.otf` من جهازك.
- **تجربة وتثبيت آمن:** يمكنك تجربة التعريب مؤقتاً عبر Steam أو تثبيته بشكل دائم مع نسخة احتياطية كاملة وإرجاع الملفات الأصلية بضغطة زر.

---

## البناء من المصدر

يتطلب البناء [.NET 10 SDK](global.json) على نظام Windows.

</div>

```powershell
dotnet build -c Release -p:RestoreLockedMode=true
dotnet test -c Release --no-build
powershell -ExecutionPolicy Bypass -File scripts/Publish-Windows.ps1
```

لتشغيل أداة سطر الأوامر:

```powershell
dotnet run --project src/Locara.Cli -- --help
```

<div dir="rtl">

---

## التوثيق والترخيص

- [docs/Windows-Release.md](docs/Windows-Release.md) — دليل الاستخدام على Windows
- [ARCHITECTURE.md](ARCHITECTURE.md) — نظرة تقنية على بنية المشروع للمطوّرين
- [THIRD-PARTY-LICENSES.txt](THIRD-PARTY-LICENSES.txt) — تراخيص المكتبات والخطوط المدمجة

المشروع مرخّص تحت رخصة [MIT](LICENSE).

</div>

---

**English:** Locara is a local-first Windows desktop app and CLI for translating PC games into Arabic. It supports Unity, Unreal Engine 4/5 (including AES-256 encrypted `.pak` files when a key is provided), Godot 3/4, Ren’Py, RPG Maker, GameMaker, and standard text/archive formats, with built-in Arabic shaping, font injection, bring-your-own-key AI translation, and SHA-256 verified backup/restore. See [ARCHITECTURE.md](ARCHITECTURE.md) for developer documentation.
