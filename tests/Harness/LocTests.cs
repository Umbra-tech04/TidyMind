using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using TidyMind;

// Localization tests, on the app started by App.OnStartup with a temp data folder:
//   Harness loc <data> <shots>          key parity, hardcoded strings, every window in en/hu/sr, live switching
//                                       (windows and the tour), screenshots, culture, sorting, search folding,
//                                       backup/restore and the language
//   Harness locfirst <data> <uiCulture> a fresh data folder started with that Windows display language
//   Harness locdata1 <data> / locdata2  data made in English, switched hu/sr/en, restarted: files unchanged
static partial class Program
{
    static readonly string Repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "TidyMind"));
    static readonly string[] Codes = { Loc.English, Loc.Hungarian, Loc.SerbianLatin };
    static readonly List<string> boxes = new List<string>(); // message boxes seen: caption | text | buttons

    // ---- Entry points -----------------------------------------------------

    static async Task LocScenarios(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        main.Topmost = true;
        StartBoxWatcher();

        Dictionary<string, Dictionary<string, string>> resx = ReadAllResx();
        KeyChecks(main, resx);
        HardcodedChecks();
        PluralChecks();
        FoldingChecks();
        SortingChecks();
        FontChecks();
        DetectionChecks();

        // Data to show: the examples (made in English here) and a few things of our own with every special letter.
        Check(Loc.Current == Loc.English, "the harness starts in English (settings.json)");
        Invoke(main, "StartTour", true);
        await Wait(1500);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        await Wait(500);
        SeedData(main);

        foreach (string code in Codes)
            await CycleLanguage(main, code, resx);

        await LiveSwitch(main);
        await SwitcherControls(main);
        CultureChecks();
        await BoxButtons();
        BackupKeepsLanguage();

        Loc.Switch(Loc.English);
        Console.WriteLine("message boxes seen: " + boxes.Count);
        foreach (string box in boxes)
            Console.WriteLine("  " + box);
        Check(!boxes.Any(b => Codes.Any(c => b.StartsWith(Lookup(resx, c, "Error.Caption") + " |"))), "no unexpected error box in any language");
    }

    static async Task LocFirstScenario(MainWindow main, string uiCulture)
    {
        await Wait(1500);
        string expected = Loc.FromWindows(new CultureInfo(uiCulture));
        Check(Loc.Current == expected, "Windows display language " + uiCulture + " starts TidyMind in " + expected + " (got " + Loc.Current + ")");
        Check(AppSettings.Load().Language == null, "the detected language isn't written to settings.json (the user hasn't picked one)");
        Check(GuidedTour.Current != null && Steps().Count == 1, "the welcome card is up");
        string title = CalloutTexts().FirstOrDefault() ?? "";
        Check(title == Loc.T("Tour.Welcome.Title"), "the welcome card is in that language: '" + title + "'");
        shots ??= Path.Combine(AppPaths.DataFolder, "..");
        RenderWindow(main, "first-launch-" + uiCulture);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
    }

    // Phase 1: data made in English; every page shown once (so anything a first visit writes is written), hashed;
    // then shown again in Hungarian, Serbian and English: nothing may change. It ends in Serbian for phase 2.
    static async Task LocData1(MainWindow main)
    {
        await Wait(1200);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        StartBoxWatcher();
        Invoke(main, "StartTour", true);
        await Wait(1500);
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        await Wait(300);
        SeedData(main);
        Guid blockId = SeedReminderBlock();

        await VisitEverything(main);
        Dictionary<string, string> before = HashData();
        File.WriteAllText(Path.Combine(AppPaths.DataFolder, "..", "hashes.json"), JsonSerializer.Serialize(before));
        Console.WriteLine("  data files hashed: " + before.Count);

        foreach (string code in new[] { Loc.Hungarian, Loc.SerbianLatin, Loc.English, Loc.SerbianLatin })
        {
            Loc.Switch(code);
            await VisitEverything(main);
        }
        CompareHashes(before, HashData(), "after switching en → hu → sr → en → sr and showing every page");
        Check(AppSettings.Load().Language == Loc.SerbianLatin, "settings.json remembers sr-Latn");
        File.WriteAllText(Path.Combine(AppPaths.DataFolder, "..", "block.txt"), blockId.ToString());
    }

    // Phase 2, a new process on the same folder: the language comes back, and the data is still byte for byte the same.
    static async Task LocData2(MainWindow main)
    {
        await Wait(1500);
        StartBoxWatcher();
        Check(Loc.Current == Loc.SerbianLatin, "after a restart TidyMind is in Serbian again (" + Loc.Current + ")");
        await VisitEverything(main);
        var before = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(AppPaths.DataFolder, "..", "hashes.json")));
        CompareHashes(before, HashData(), "after the restart in Serbian");
        Guid block = Guid.Parse(File.ReadAllText(Path.Combine(AppPaths.DataFolder, "..", "block.txt")));
        Reminder reminder = ReminderManager.LoadReminders().FirstOrDefault(r => r.Id == block);
        Check(reminder?.Title == DayPlanReminders.ReminderTitle, "the block's reminder still stores its title as \"Day plan\"");
        Check(reminder != null && DayPlanReminders.ShownTitle(reminder, DayPlanReminders.BlockIds()) == "Dnevni plan", "and shows it as \"Dnevni plan\" in Serbian");
        try { ReminderManager.DeleteReminder(block); } catch (Exception e) { Console.WriteLine("  cleanup: " + e.Message); }
        Check(boxes.Count == 0, "no message box came up (" + string.Join(" / ", boxes) + ")");
    }

    // ---- Keys -------------------------------------------------------------

    static Dictionary<string, Dictionary<string, string>> ReadAllResx() => new Dictionary<string, Dictionary<string, string>>
    {
        [Loc.English] = ReadResx("Strings.resx"),
        [Loc.Hungarian] = ReadResx("Strings.hu.resx"),
        [Loc.SerbianLatin] = ReadResx("Strings.sr-Latn.resx")
    };

    static Dictionary<string, string> ReadResx(string name) =>
        XDocument.Load(Path.Combine(Repo, name)).Root.Elements("data")
            .ToDictionary(d => (string)d.Attribute("name"), d => (string)d.Element("value") ?? "");

    static string Lookup(Dictionary<string, Dictionary<string, string>> resx, string code, string key) =>
        resx[code].TryGetValue(key, out string v) ? v : resx[Loc.English][key];

    static readonly Regex PluralKey = new Regex(@"\.(One|Few|Other)$");
    static readonly Regex Holes = new Regex(@"\{(\d+)(?:[:,][^}]*)?\}");

    static void KeyChecks(MainWindow main, Dictionary<string, Dictionary<string, string>> resx)
    {
        Console.WriteLine("Keys");
        Dictionary<string, string> en = resx[Loc.English];
        Dictionary<string, bool[]> need = new Dictionary<string, bool[]>
        {
            ["One"] = new[] { true, false, true },
            ["Few"] = new[] { false, false, true },
            ["Other"] = new[] { true, true, true }
        };
        HashSet<string> all = new HashSet<string>(Codes.SelectMany(c => resx[c].Keys));
        List<string> problems = new List<string>();
        foreach (string key in all.OrderBy(k => k))
        {
            Match plural = PluralKey.Match(key);
            for (int i = 0; i < 3; i++)
            {
                bool has = resx[Codes[i]].TryGetValue(key, out string text);
                bool required = !plural.Success || need[plural.Groups[1].Value][i];
                if (required && (!has || string.IsNullOrWhiteSpace(text)))
                    problems.Add(key + ": no " + Codes[i] + " text");
                if (!required && has)
                    problems.Add(key + ": " + Codes[i] + " shouldn't have this plural form");
            }
            string[] sets = Codes.Where(c => resx[c].ContainsKey(key))
                .Select(c => string.Join(",", Holes.Matches(resx[c][key]).Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s)))
                .Distinct().ToArray();
            if (sets.Length > 1)
                problems.Add(key + ": placeholders differ (" + string.Join(" / ", sets) + ")");
        }
        Check(problems.Count == 0, "every key in all three languages, plural forms as each language needs, same placeholders, no empty text ("
            + all.Count + " keys)" + (problems.Count > 0 ? ": " + string.Join("; ", problems.Take(20)) : ""));

        // Which keys the code uses: string literals that are keys (or a plural's base, or a family prefix such as
        // "Home.Tile."), {local:Tr ...} in XAML, and the families built at run time (tour steps, greetings).
        HashSet<string> literals = new HashSet<string>();
        HashSet<string> inLocCalls = new HashSet<string>();
        Regex literal = new Regex("\"([A-Z][A-Za-z0-9]*(?:\\.[A-Za-z0-9]+)+\\.?)\"");
        foreach (string file in Directory.GetFiles(Repo, "*.cs"))
        {
            foreach (string line in File.ReadLines(file))
            {
                foreach (Match m in literal.Matches(line))
                {
                    literals.Add(m.Groups[1].Value);
                    if (line.Contains("Loc.") || line.Contains("Primary =") || line.Contains("Secondary ="))
                        inLocCalls.Add(m.Groups[1].Value);
                }
            }
        }
        Regex tr = new Regex(@"\{local:Tr ([A-Za-z0-9.]+)\}");
        foreach (string file in Directory.GetFiles(Repo, "*.xaml"))
            foreach (Match m in tr.Matches(File.ReadAllText(file)))
            {
                literals.Add(m.Groups[1].Value);
                inLocCalls.Add(m.Groups[1].Value);
            }

        bool Defined(string k) => en.ContainsKey(k) || en.ContainsKey(k + ".Other") || (k.EndsWith(".") && en.Keys.Any(x => x.StartsWith(k)));
        List<string> missing = inLocCalls.Where(k => !Defined(k)).OrderBy(k => k).ToList();
        Check(missing.Count == 0, "every key the code asks for exists" + (missing.Count > 0 ? ": " + string.Join(", ", missing) : ""));

        HashSet<string> dynamic = new HashSet<string>();
        foreach (TourStep step in TourSteps.Build(main).Prepend(TourSteps.Welcome))
        {
            dynamic.Add("Tour." + step.Key + ".Title");
            dynamic.Add("Tour." + step.Key + ".Text");
        }
        foreach (TimeOfDayBucket bucket in Enum.GetValues(typeof(TimeOfDayBucket)))
        {
            int count = GreetingProvider.MessageCount(bucket);
            Check(count > 0, "greetings for " + bucket + ": " + count);
            for (int n = 1; n <= count; n++)
                dynamic.Add("Greeting." + bucket + "." + n);
        }
        // Choice values, palette names and repeat types are looked up by the stored value or name.
        IEnumerable<string> choiceValues = Choices.Material.Concat(Choices.Season).Concat(Choices.Condition)
            .Concat(EntityTypes.Fields(EntityTypes.Vehicle).Concat(EntityTypes.Records(EntityTypes.Vehicle).SelectMany(r => r.Fields))
                .Where(fd => fd.ChoiceLabel.Method.DeclaringType == typeof(Choices)).SelectMany(fd => fd.Choices));
        foreach (string value in choiceValues)
            dynamic.Add("Choice." + new string(value.Where(char.IsLetterOrDigit).ToArray()));
        foreach ((string colorName, string _) in BackgroundDialog.Palette)
            dynamic.Add("Color." + colorName);
        var textColors = ((string Name, string Hex)[])typeof(FormatToolbar).GetField("TextColors", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        foreach ((string colorName, string _) in textColors)
            dynamic.Add("TextColor." + colorName);
        foreach (RepeatType repeat in Enum.GetValues(typeof(RepeatType)))
            dynamic.Add("Repeat." + repeat);
        foreach (string action in new[] { "Add", "Edit" })
            foreach (string type in new[] { "Clothing", "Vehicle", "Item" })
                dynamic.Add("Entity." + action + "." + type);

        List<string> prefixes = literals.Where(l => l.EndsWith(".") && l.Count(c => c == '.') >= 2).ToList();
        List<string> unused = en.Keys.Where(k =>
                !literals.Contains(k) && !literals.Contains(PluralKey.Replace(k, "")) && !dynamic.Contains(k)
                && !prefixes.Any(p => k.StartsWith(p)))
            .OrderBy(k => k).ToList();
        Check(unused.Count == 0, "no unused keys" + (unused.Count > 0 ? ": " + string.Join(", ", unused) : ""));
        List<string> tourMissing = dynamic.Where(k => !en.ContainsKey(k)).ToList();
        Check(tourMissing.Count == 0, "every tour step, greeting and item-form title has its texts" + (tourMissing.Count > 0 ? ": " + string.Join(", ", tourMissing) : ""));

        foreach (string code in new[] { Loc.Hungarian, Loc.SerbianLatin })
        {
            List<string> same = resx[code].Where(p => en.TryGetValue(p.Key, out string e) && e == p.Value && Regex.IsMatch(p.Value, "[a-z]{3}"))
                .Select(p => p.Key + "=" + p.Value).ToList();
            Console.WriteLine("  " + code + ": " + resx[code].Count + " texts; the same as English (on purpose or worth a look): "
                + (same.Count == 0 ? "none" : string.Join(", ", same)));
        }

        Console.WriteLine("  keys per area: " + string.Join(", ", en.Keys.GroupBy(k => k.Split('.')[0]).OrderByDescending(g => g.Count())
            .Select(g => g.Key + " " + g.Count())));
    }

    // ---- Hardcoded strings --------------------------------------------------

    // Left on purpose: the brand, key names, file format names, and the stored "Day plan" title (see DayPlanReminders).
    static readonly string[] AllowedLiterals =
    {
        "TidyMind", "TIDYMIND", "TidyMind ", "Ctrl+Shift+C", "Ctrl+Shift+F", "Ctrl+Shift+N", "Ctrl+Shift+R", "Esc",
        "Excel (XLSX)", "Word (DOCX)", "PDF", "Day plan", "Wrap", "Underline", "Center", "Stretch", "Clothing", "Vehicle"
    };

    static void HardcodedChecks()
    {
        Console.WriteLine("Hardcoded text");
        Regex letters = new Regex("[A-Za-zÀ-ž]{2,}");
        Regex xamlAttr = new Regex("\\b(Text|Content|Header|ToolTip|Title|Tag|AutomationProperties\\.Name|PlaceholderText)=\"([^\"{][^\"]*)\"");
        Regex setterValue = new Regex("Property=\"(ToolTip|Content|Header|Text|AutomationProperties\\.Name)\" Value=\"([^\"{][^\"]*)\"");
        Regex codeAssign = new Regex("\\b(Text|Content|Header|ToolTip|Title|InputGestureText)\\s*=\\s*\"([^\"]*)\"");
        Regex codeCall = new Regex("(SetName\\([^,]+,|Prompt\\([^,]+,)\\s*\"([^\"]*)\"");
        List<string> found = new List<string>();
        foreach (string file in Directory.GetFiles(Repo, "*.xaml").Concat(Directory.GetFiles(Repo, "*.cs")))
        {
            string name = Path.GetFileName(file);
            string[] lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.TrimStart().StartsWith("//") || line.TrimStart().StartsWith("<!--"))
                    continue;
                IEnumerable<Match> matches = name.EndsWith(".xaml")
                    ? xamlAttr.Matches(line).Concat(setterValue.Matches(line))
                    : codeAssign.Matches(line).Concat(codeCall.Matches(line));
                foreach (Match m in matches)
                {
                    string value = m.Groups[2].Value;
                    if (letters.IsMatch(value) && !value.StartsWith("&#x") && !value.StartsWith("\\u") && !AllowedLiterals.Contains(value))
                        found.Add(name + ":" + (i + 1) + " " + m.Groups[1].Value + "=\"" + value + "\"");
                }
                if (!name.EndsWith(".xaml") && name != "Msg.cs" && line.Contains("MessageBox.Show"))
                    found.Add(name + ":" + (i + 1) + " MessageBox.Show (not through Msg)");
            }
        }
        Check(found.Count == 0, "no user-visible text written into XAML or code outside the resources" + (found.Count > 0 ? ":\n      " + string.Join("\n      ", found) : ""));
        Console.WriteLine("  left on purpose: " + string.Join(", ", AllowedLiterals.Take(13)));
    }

    // ---- Plural, folding, sorting, fonts, detection --------------------------

    static void PluralChecks()
    {
        Console.WriteLine("Plural forms");
        (long N, string Form)[] sr = { (0, "Other"), (1, "One"), (2, "Few"), (4, "Few"), (5, "Other"), (11, "Other"), (12, "Other"), (14, "Other"),
            (20, "Other"), (21, "One"), (22, "Few"), (25, "Other"), (101, "One"), (111, "Other"), (112, "Other"), (122, "Few") };
        Check(sr.All(x => Loc.PluralForm(Loc.SerbianLatin, x.N) == x.Form), "Serbian: 1/21/101 one, 2–4/22–24 few, 0, 5–20, 11–14, 111–114 other");
        Check(new long[] { 0, 1, 2, 5, 21 }.All(n => Loc.PluralForm(Loc.Hungarian, n) == "Other"), "Hungarian: one form after any number");
        Check(Loc.PluralForm(Loc.English, 1) == "One" && Loc.PluralForm(Loc.English, 0) == "Other" && Loc.PluralForm(Loc.English, 2) == "Other",
            "English: 1 one, everything else other");
        Loc.Switch(Loc.SerbianLatin);
        string s = string.Join(" | ", new[] { 1, 2, 5, 11, 21, 22 }.Select(n => Loc.Plural("Card.Tasks", n)));
        Check(s == "1 zadatak | 2 zadatka | 5 zadataka | 11 zadataka | 21 zadatak | 22 zadatka", "Serbian task counts: " + s);
        Loc.Switch(Loc.Hungarian);
        s = string.Join(" | ", new[] { 1, 3 }.Select(n => Loc.Plural("Card.Tasks", n)));
        Check(s == "1 feladat | 3 feladat", "Hungarian task counts: " + s);
        Loc.Switch(Loc.English);
        s = string.Join(" | ", new[] { 1, 3 }.Select(n => Loc.Plural("Card.Tasks", n)));
        Check(s == "1 task | 3 tasks", "English task counts: " + s);
        Check(Loc.T("No.Such.Key.Here") == "No.Such.Key.Here", "a key that doesn't exist at all comes back as itself (the key check above keeps that from happening)");
    }

    static void FoldingChecks()
    {
        Console.WriteLine("Search folding");
        (string Text, string Query)[] cases =
        {
            ("Đorđe Đurić", "dorde duric"), ("ĐAK", "dak"), ("Čačak", "cacak"), ("Ćuprija", "cuprija"), ("Šabac", "sabac"),
            ("Žabalj", "zabalj"), ("Őszi szőlő", "oszi szolo"), ("ŰRHAJÓ", "urhajo"), ("áéíóöúü", "aeioouu"), ("Árvíztűrő tükörfúrógép", "ARVIZTURO")
        };
        foreach ((string text, string query) in cases)
            Check(TidyMind.TextSearch.Contains(text, query), "\"" + query + "\" finds \"" + text + "\"");
        Check(TidyMind.TextSearch.IndexOf("x Đak", "dak", out int length) == 2 && length == 3, "the match covers the original letters (for highlighting)");
    }

    static void SortingChecks()
    {
        Console.WriteLine("Sorting by the language's alphabet");
        string Sorted(string code, params string[] words)
        {
            Loc.Switch(code);
            return string.Join(" ", words.OrderBy(w => w, Loc.Comparer));
        }
        Check(Sorted(Loc.Hungarian, "őr", "oz", "pad") == "oz őr pad", "Hungarian puts ő after o: " + Sorted(Loc.Hungarian, "őr", "oz", "pad"));
        Check(Sorted(Loc.Hungarian, "űr", "uz") == "uz űr", "Hungarian puts ű after u");
        Check(Sorted(Loc.SerbianLatin, "čaj", "cvet", "ćup", "dom") == "cvet čaj ćup dom", "Serbian: c, č, ć: " + Sorted(Loc.SerbianLatin, "čaj", "cvet", "ćup", "dom"));
        Check(Sorted(Loc.SerbianLatin, "đak", "dom", "ema") == "dom đak ema", "Serbian: đ after d: " + Sorted(Loc.SerbianLatin, "đak", "dom", "ema"));
        Check(Sorted(Loc.SerbianLatin, "šal", "sto", "žar", "zid") == "sto šal zid žar", "Serbian: š after s, ž after z");
        Check(Sorted(Loc.English, "őr", "oz") == "őr oz", "English sorts ő with o, as before");
        Loc.Switch(Loc.English);
    }

    static void FontChecks()
    {
        Console.WriteLine("Fonts");
        const string special = "őŐűŰčČćĆšŠžŽđĐáÁéÉíÍóÓöÖúÚüÜ„”“";
        foreach (string family in new[] { "Segoe UI", "Segoe UI Variable Display" })
        {
            Typeface face = new Typeface(new FontFamily(family), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            bool ok = face.TryGetGlyphTypeface(out GlyphTypeface glyphs);
            string lacking = ok ? new string(special.Where(c => !glyphs.CharacterToGlyphMap.ContainsKey(c)).ToArray()) : special;
            Check(ok && lacking.Length == 0, family + " has every Hungarian and Serbian letter" + (lacking.Length > 0 ? " (missing " + lacking + ")" : ""));
        }
    }

    static void DetectionChecks()
    {
        Console.WriteLine("First-launch language");
        foreach ((string ui, string want) in new[] { ("hu-HU", "hu"), ("sr-Latn-RS", "sr-Latn"), ("sr-Cyrl-RS", "sr-Latn"), ("sr", "sr-Latn"),
                     ("sr-Latn-ME", "sr-Latn"), ("en-US", "en"), ("en-GB", "en"), ("de-DE", "en"), ("hr-HR", "en") })
            Check(Loc.FromWindows(new CultureInfo(ui)) == want, "Windows in " + ui + " → " + want);
        Check(Loc.Normalize("fr") == null && Loc.Normalize("sr-Latn") == "sr-Latn", "an unknown saved code is ignored");
    }

    static void CultureChecks()
    {
        Console.WriteLine("Dates, numbers, first day of the week");
        DayOfWeek windows = new CultureInfo(CultureInfo.InstalledUICulture.Name).DateTimeFormat.FirstDayOfWeek;
        DateTime d = new DateTime(2026, 10, 16);
        foreach (string code in Codes)
        {
            Loc.Switch(code);
            Console.WriteLine("  " + code + ": " + d.ToString(Loc.T("Format.WeekdayDayMonth"), Loc.Culture) + "  /  " + d.ToString(Loc.T("Format.YearMonth"), Loc.Culture)
                + "  /  " + d.ToString(Loc.T("Format.FullDate"), Loc.Culture) + "  /  " + (6.5).ToString(Loc.Culture) + "  /  first day " + Loc.FirstDayOfWeek);
            Check(CultureInfo.CurrentCulture.Name == Loc.Culture.Name && CultureInfo.CurrentUICulture.Name == Loc.Culture.Name,
                code + ": CurrentCulture and CurrentUICulture follow (" + CultureInfo.CurrentCulture.Name + ")");
            Check(string.Equals(Application.Current.MainWindow.Language.IetfLanguageTag, Loc.Culture.IetfLanguageTag, StringComparison.OrdinalIgnoreCase),
                code + ": the main window's WPF Language follows (" + Application.Current.MainWindow.Language.IetfLanguageTag + ")");
        }
        Loc.Switch(Loc.Hungarian);
        Check(Loc.FirstDayOfWeek == DayOfWeek.Monday && d.ToString(Loc.T("Format.WeekdayDayMonth"), Loc.Culture) == "október 16., péntek", "Hungarian: Monday first, \"október 16., péntek\"");
        Loc.Switch(Loc.SerbianLatin);
        Check(Loc.FirstDayOfWeek == DayOfWeek.Monday && d.ToString(Loc.T("Format.WeekdayDayMonth"), Loc.Culture) == "petak, 16. oktobar", "Serbian: Monday first, \"petak, 16. oktobar\"");
        Loc.Switch(Loc.English);
        Console.WriteLine("  Windows' own first day here: " + CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek);
    }

    // ---- Every window in every language ------------------------------------

    static async Task CycleLanguage(MainWindow main, string code, Dictionary<string, Dictionary<string, string>> resx)
    {
        Console.WriteLine("All pages and windows in " + code);
        Loc.Switch(code);
        await Wait(500);
        Check(Loc.Current == code && AppSettings.Load().Language == code, code + ": switched and remembered in settings.json");

        HashSet<string> keys = new HashSet<string>(resx[Loc.English].Keys);
        // English texts that read differently in this language: one of them on screen is a missed translation.
        // The examples' own names and notes are data made in English, not interface text.
        HashSet<string> sampleTexts = new HashSet<string>(resx[Loc.English].Where(p => p.Key.StartsWith("Welcome.")).Select(p => p.Value));
        HashSet<string> englishOnly = new HashSet<string>(code == Loc.English ? Enumerable.Empty<string>()
            : resx[Loc.English].Where(p => !p.Key.StartsWith("Welcome.") && !p.Key.StartsWith("Format.") && !sampleTexts.Contains(p.Value)
                    && resx[code].TryGetValue(p.Key, out string t) && t != p.Value && !resx[code].ContainsValue(p.Value))
                .Select(p => p.Value));
        string tag = code == Loc.SerbianLatin ? "sr" : code;
        List<string> layout = new List<string>();

        async Task Page(string what, Func<Task> show, FrameworkElement root, string shot = null)
        {
            try
            {
                await show();
                await Wait(450);
                InspectTexts(root, what + " (" + code + ")", keys, englishOnly);
                layout.AddRange(Clipped(root).Select(c => what + ": " + c));
                if (shot != null)
                    RenderWindow(Window.GetWindow(root) ?? main, tag + "-" + shot);
            }
            catch (Exception e)
            {
                Check(false, what + " threw in " + code + ": " + e.GetType().Name + " " + e.Message);
            }
        }

        main.WindowState = WindowState.Normal;
        main.Width = 1160;
        main.Height = 720;

        await Page("Home", async () => { Invoke(main, "NavigateHome"); await Wait(300); }, main, "01-home");
        await Page("Home tiles and expiries", async () =>
        {
            ((FrameworkElement)((HomeView)Content(main)).FindName("ExpirySection")).BringIntoView();
            await Wait(400);
        }, main, "01b-home-tiles");
        // A tooltip with the special letters, in the app's tooltip style.
        ToolTip sample = new ToolTip { Content = Loc.T("Main.Corner.QuickNotes") + " · őŐűŰ čćšžđ ČĆŠŽĐ" };
        sample.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        sample.Arrange(new Rect(sample.DesiredSize));
        RenderPopup(sample, tag + "-tooltip");
        await Page("Project memory", async () => { main.ShowSample(ProfileType.Project); await Wait(300); }, main, "02-project");
        await Page("Collection", async () =>
        {
            main.ShowSample(ProfileType.Collection);
            await Wait(400);
        }, main, "03-collection");
        await Page("Day Plan (timeline)", async () =>
        {
            main.ShowDayPlan(DateTime.Today);
            await Wait(300);
            ((RadioButton)((DayPlanView)Content(main)).FindName("TimelineButton")).IsChecked = true;
        }, main, "04-dayplan");
        await Page("Day Plan (list)", async () => { ((RadioButton)((DayPlanView)Content(main)).FindName("ListButton")).IsChecked = true; await Wait(100); }, main, "05-dayplan-list");
        ((RadioButton)((DayPlanView)Content(main)).FindName("TimelineButton")).IsChecked = true;
        await Page("Calendar", async () => { main.OpenCalendar(); await Wait(500); }, main, "06-calendar");
        main.CloseCalendar();
        await Wait(400);
        await Page("Search", async () =>
        {
            main.OpenSearch();
            await Wait(300);
            ((TextBox)Find(main, "QueryBox")).Text = "bi";
            await Wait(400);
        }, main, "07-search");
        main.CloseSearch();

        // Every category of Settings, each page in turn (only the chosen one shows).
        SettingsWindow settings = new SettingsWindow(null, owner => false) { Owner = main };
        settings.Show();
        ListBox categories = (ListBox)settings.FindName("CategoryList");
        for (int i = 0; i < categories.Items.Count; i++)
        {
            int index = i;
            await Page("Settings: " + ((ListBoxItem)categories.Items[i]).Content, async () => { categories.SelectedIndex = index; await Wait(200); },
                settings, "08-settings-" + (i + 1));
        }
        if (code == Loc.English)
        {
            Check(settings.ActualHeight <= main.ActualHeight * 0.8 + 1 && settings.ActualWidth <= 901 && settings.ResizeMode == ResizeMode.CanResize,
                "Settings: resizable, at most 80% of the main window's height (" + (int)settings.ActualWidth + "×" + (int)settings.ActualHeight
                + " over " + (int)main.ActualWidth + "×" + (int)main.ActualHeight + ")");
            categories.SelectedIndex = 0;
            ListBoxItem first = (ListBoxItem)categories.ItemContainerGenerator.ContainerFromIndex(0);
            first.Focus();
            PressKey(first, System.Windows.Input.Key.Down);
            await Wait(200);
            Check(categories.SelectedIndex == 1 && ((FrameworkElement)settings.FindName("AppearancePage")).IsVisible
                  && !((FrameworkElement)settings.FindName("GeneralPage")).IsVisible, "Settings: Down in the category list shows the next category");
            PressKey((UIElement)System.Windows.Input.Keyboard.FocusedElement ?? settings, System.Windows.Input.Key.Escape);
            await Wait(200);
            Check(!settings.IsVisible, "Settings: Esc closes it");
        }
        if (settings.IsVisible)
            settings.Close();

        OverlayManager.Route(Overlay.QuickNotes);
        await Wait(500);
        Window notes = Application.Current.Windows.OfType<QuickNotesWindow>().First();
        await Page("Quick Notes", () => Task.CompletedTask, notes, "09-quicknotes");
        OverlayManager.CloseAll();

        OverlayManager.Route(Overlay.Reminders);
        await Wait(500);
        Window reminders = Application.Current.Windows.OfType<RemindersWindow>().First();
        await Page("Reminders", () => Task.CompletedTask, reminders, "10-reminders");
        OverlayManager.CloseAll();

        main.ShowSample(ProfileType.Project);
        await Wait(300);
        main.OpenSampleNotebook();
        ProjectWindow project = await WaitFor(() => GuidedTour.Open<ProjectWindow>());
        if (project != null)
        {
            await Page("Project Details", () => Task.CompletedTask, project, "11-project-details");
            project.Close();
            await Wait(500);
        }
        else
            Check(false, "the example project opened (" + code + ")");

        Profile collection = WelcomeMemory.Find(Profiles(main), ProfileType.Project);
        foreach ((string what, Func<Window> make) in new (string, Func<Window>)[]
        {
            ("Add reminder", () => new AddReminderWindow("Ősz čćšžđ")),
            ("Calendar day", () => Make<DayEditDialog>(DateTime.Today)),
            ("Day plan block", () => Make<DayPlanBlockDialog>(null, DateTime.Today, 9 * 60, 10 * 60, (IEnumerable<Profile>)Profiles(main), new List<PlanTodo>(), null, true)),
            ("Vehicle form", () => new EntityForm(EntityTypes.Vehicle)),
            ("Clothing form", () => new EntityForm(EntityTypes.Clothing)),
            ("Vehicle window", () => new ItemWindow(SampleVehicle(), new ItemWindow.Host())),
            ("Garment window", () => new ItemWindow(SampleGarment(), new ItemWindow.Host())),
            ("Item type", () => new EntityTypeSelector()),
            ("Memory notebook", () => new NotebookWindow(collection, new MemoryNotebook())),
            ("Export format", () => new ExportFormatWindow()),
            ("Licenses", () => new LicensesWindow()),
            ("Update", () => new UpdateDialog(UpdateRelease.Parse(GitHubJson("v9.9.9", null, 80000000, "- Faster start\n- Fixes"), UpdateEndpoints.Production, out _, out _))),
            ("Text prompt", () => Make<InputDialog>(Loc.T("Main.NewProject"), Loc.T("Memory.New.ProjectPrompt"), "", Loc.T("Input.Create"), false, false)),
            ("Background", () => new BackgroundDialog(Loc.T("Card.CustomizeTitle"), "Ősz", new BackgroundSetting(BackgroundFill.None, null, null),
                ImageStore.Cards, c => c, (t, c, i) => new Border { Width = 160, Height = 160 }))
        })
        {
            Window window = null;
            await Page(what, async () =>
            {
                window = make();
                window.Owner = main;
                window.Show();
                await Wait(200);
            }, main);
            if (window != null)
            {
                InspectTexts(window, what + " (" + code + ")", keys, englishOnly);
                layout.AddRange(Clipped(window).Select(c => what + ": " + c));
                RenderWindow(window, tag + "-dialog-" + what.Replace(' ', '-').ToLowerInvariant());
                window.Close();
                await Wait(200);
            }
        }

        // The tour: every step's callout, and one with its spotlight for the screenshot.
        Invoke(main, "StartTour", true);
        await Wait(1200);
        List<string> tourProblems = new List<string>();
        for (int i = 0; i < Steps().Count; i++)
        {
            TourStep step = Steps()[i];
            if (keys.Contains(step.Title) || keys.Contains(step.Text) || englishOnly.Contains(step.Title) || englishOnly.Contains(step.Text))
                tourProblems.Add(step.Key);
        }
        Check(tourProblems.Count == 0, code + ": every tour step reads in " + code + (tourProblems.Count > 0 ? " (not: " + string.Join(", ", tourProblems) + ")" : ""));
        await ToStepKey("SelectCards");
        await Wait(800);
        InspectTexts(main, "Tour step (" + code + ")", keys, englishOnly);
        RenderWindow(main, tag + "-12-tour");
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        await Wait(500);

        // Narrow: the smallest window the app allows.
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        await Page("Home, narrow", async () => { Invoke(main, "NavigateHome"); await Wait(300); }, main, "13-home-narrow");
        await Page("Day Plan, narrow", async () => { main.ShowDayPlan(DateTime.Today); await Wait(300); }, main, "14-dayplan-narrow");
        await Page("Collection, narrow", async () => { main.ShowSample(ProfileType.Collection); await Wait(300); }, main, "15-collection-narrow");
        main.Width = 1160;
        main.Height = 720;
        Invoke(main, "NavigateHome");

        Check(layout.Count == 0, code + ": no text cut off without an ellipsis" + (layout.Count > 0 ? ":\n      " + string.Join("\n      ", layout.Distinct()) : ""));
    }

    static T Make<T>(params object[] args) where T : Window =>
        (T)Activator.CreateInstance(typeof(T), BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, args, null);

    static async Task ToStepKey(string key)
    {
        int at = Steps().ToList().FindIndex(s => s.Key == key);
        GuidedTour.Current.GetType().GetMethod("Show", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(GuidedTour.Current, new object[] { at });
        await Wait(700);
    }

    // Every text a window shows or announces: no raw key, and in Hungarian or Serbian no English left over.
    static void InspectTexts(DependencyObject root, string what, HashSet<string> keys, HashSet<string> englishOnly)
    {
        List<string> raw = new List<string>(), english = new List<string>();
        foreach (string text in Texts(root).Select(t => t.Trim()).Where(t => t.Length > 0).Distinct())
        {
            if (keys.Contains(text))
                raw.Add(text);
            else if (englishOnly.Contains(text))
                english.Add(text);
        }
        Check(raw.Count == 0 && english.Count == 0, what + ": no raw keys, nothing left in English"
            + (raw.Count > 0 ? " [keys: " + string.Join(" | ", raw) + "]" : "") + (english.Count > 0 ? " [English: " + string.Join(" | ", english) + "]" : ""));
    }

    // What's on screen: hidden parts (a closed panel's old rows) are skipped, but a shown element's menu and tooltip
    // count, since they appear on a click or hover.
    static IEnumerable<string> Texts(DependencyObject root)
    {
        HashSet<DependencyObject> seen = new HashSet<DependencyObject>();
        Stack<(DependencyObject Node, bool Popup)> todo = new Stack<(DependencyObject, bool)>();
        todo.Push((root, false));
        while (todo.Count > 0)
        {
            (DependencyObject d, bool popup) = todo.Pop();
            if (!seen.Add(d))
                continue;
            if (!popup && d is UIElement ui && !ui.IsVisible && !(d is Window))
                continue;
            switch (d)
            {
                case TextBlock block: yield return block.Text; break;
                case Run run: yield return run.Text; break;
                case TextBox box: yield return box.Text; break;
                case Window window: yield return window.Title; break;
                case HeaderedItemsControl headered when headered.Header is string header: yield return header; break;
                case ContentControl content when content.Content is string text: yield return text; break;
            }
            if (d is FrameworkElement element)
            {
                if (element.ToolTip is string tip)
                    yield return tip;
                else if (element.ToolTip is DependencyObject tipObject)
                    todo.Push((tipObject, true));
                if (element.ContextMenu != null)
                    todo.Push((element.ContextMenu, true));
                string name = AutomationProperties.GetName(element);
                if (!string.IsNullOrEmpty(name))
                    yield return name;
                if (element is TextBox search && search.Tag is string placeholder)
                    yield return placeholder;
            }
            if (d is Visual)
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
                    todo.Push((VisualTreeHelper.GetChild(d, i), popup));
            foreach (object child in LogicalTreeHelper.GetChildren(d))
                if (child is DependencyObject c)
                    todo.Push((c, popup));
        }
    }

    // A one-line text that doesn't fit and has no ellipsis (and isn't in a scrolling or wrapping place): it's cut.
    static IEnumerable<string> Clipped(DependencyObject root)
    {
        foreach (TextBlock block in Descendants(root).OfType<TextBlock>())
        {
            if (!block.IsVisible || block.ActualWidth <= 0 || block.TextWrapping != TextWrapping.NoWrap
                || block.TextTrimming != TextTrimming.None || string.IsNullOrWhiteSpace(block.Text))
                continue;
            if (block.FontFamily.Source.Contains("MDL2"))
                continue;
            FormattedText formatted = new FormattedText(block.Text, CultureInfo.CurrentUICulture, block.FlowDirection,
                new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch), block.FontSize, Brushes.Black,
                VisualTreeHelper.GetDpi(block).PixelsPerDip);
            double need = formatted.WidthIncludingTrailingWhitespace + block.Padding.Left + block.Padding.Right;
            if (need > block.ActualWidth + 1.5 && !InHorizontalScroll(block))
                yield return "\"" + block.Text + "\" needs " + need.ToString("0") + " px, has " + block.ActualWidth.ToString("0");
        }
    }

    static bool InHorizontalScroll(DependencyObject d)
    {
        for (DependencyObject p = VisualTreeHelper.GetParent(d); p != null; p = VisualTreeHelper.GetParent(p))
            if (p is ScrollViewer viewer && viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled
                && viewer.ScrollableWidth > 0)
                return true;
        return false;
    }

    // ---- Live switching -----------------------------------------------------

    // With the tour on a step, Quick Notes, Reminders and Settings open: every one of them follows a switch at once,
    // and the spotlight stays on its target.
    static async Task LiveSwitch(MainWindow main)
    {
        Console.WriteLine("Switching while windows and the tour are open");
        Loc.Switch(Loc.English);
        Invoke(main, "NavigateHome");
        await Wait(300);
        Invoke(main, "StartTour", true);
        await Wait(1200);
        await ToStepKey("OpenProject");
        await Wait(800);
        OverlayManager.Route(Overlay.Reminders);
        await Wait(400);
        RemindersWindow reminders = Application.Current.Windows.OfType<RemindersWindow>().First();
        QuickNotesWindow.ShowOrFocus();
        await Wait(400);
        QuickNotesWindow notes = Application.Current.Windows.OfType<QuickNotesWindow>().First();
        SettingsWindow settings = new SettingsWindow(null, owner => false) { Owner = main };
        ((ListBox)settings.FindName("CategoryList")).SelectedIndex = 3; // Backup & transfer: the Restore row is checked below
        settings.Show();
        await Wait(300);
        main.Activate();

        foreach (string code in new[] { Loc.Hungarian, Loc.SerbianLatin, Loc.English })
        {
            Loc.Switch(code);
            await Wait(900);
            string title = CalloutTexts().FirstOrDefault() ?? "";
            Check(title == Loc.T("Tour.OpenProject.Title"), code + ": the tour's current step shows in the new language: '" + title + "'");
            FrameworkElement card = (GuidedTour.Find(main, "ProjectPanel") as Panel)?.Children.OfType<FrameworkElement>().FirstOrDefault();
            if (card != null)
                CheckHoleOn(main, card, code + ": the spotlight is still on the first card");
            Check(reminders.Title == Loc.T("Main.Help.Reminders"), code + ": Reminders window title " + reminders.Title);
            Check(Texts(reminders).Contains(Loc.T("Common.AddReminder")), code + ": Reminders window's button follows");
            Check(notes.Title == Loc.T("Main.Help.QuickNotes"), code + ": Quick Notes title " + notes.Title);
            Check(settings.Title == Loc.T("Settings.Title") && Texts(settings).Contains(Loc.T("Settings.Restore.Title")), code + ": Settings follows (" + settings.Title + ")");
            Check(Texts(main).Contains(Loc.T("Nav.Home")) && Texts(main).Contains(Loc.T("Nav.Collections").ToUpper(Loc.Culture)), code + ": the sidebar follows");
            Check(((TextBlock)main.FindName("LanguageCode")).Text == Loc.CurrentOption.ShortName, code + ": the switch shows " + Loc.CurrentOption.ShortName);
            RenderWindow(main, "live-" + (code == Loc.SerbianLatin ? "sr" : code) + "-tour-step");
        }

        settings.Close();
        GuidedTour.Current?.Stop(TourEnd.Skipped);
        await Wait(600);
    }

    static List<string> CalloutTexts()
    {

        FrameworkElement callout = Field<FrameworkElement>(GuidedTour.Current, "callout");
        return Descendants(callout).OfType<TextBlock>().Select(t => t.Text).Where(t => t.Length > 0).ToList();
    }

    // The sidebar's switch (its list of three, each in its own language, the current one ticked) and the Settings row.
    static async Task SwitcherControls(MainWindow main)
    {
        Console.WriteLine("The switch in the sidebar and in Settings");
        Loc.Switch(Loc.English);
        Button button = (Button)main.FindName("LanguageButton");
        Check(AutomationProperties.GetName(button) == "Language: English" && (string)button.ToolTip == "Language", "the button is named \"Language: English\" with a tooltip");
        Check(button.Focusable && button.IsTabStop, "the button takes the keyboard focus");
        Invoke(main, "LanguageButton_Click", button, new RoutedEventArgs());
        await Wait(500);
        ContextMenu menu = OpenMenus().FirstOrDefault();
        Check(menu != null, "a click opens the list");
        if (menu != null)
        {
            List<MenuItem> items = menu.Items.OfType<MenuItem>().ToList();
            Check(string.Join(", ", items.Select(i => (string)i.Header)) == "English, Magyar, Srpski", "it lists English, Magyar, Srpski");
            Check(items[0].IsChecked && !items[1].IsChecked && !items[2].IsChecked, "English is ticked");
            Check(menu.IsKeyboardFocusWithin || System.Windows.Input.Keyboard.FocusedElement is MenuItem || menu.IsFocused, "the list has the keyboard (arrows, Enter, Esc)");
            RenderPopup(menu, "switch-flyout-en");
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            menu.IsOpen = false;
            await Wait(500);
            Check(Loc.Current == Loc.Hungarian && AppSettings.Load().Language == Loc.Hungarian, "Magyar switches to Hungarian and is remembered");
            Check(((TextBlock)main.FindName("LanguageCode")).Text == "HU" && AutomationProperties.GetName(button) == "Nyelv: Magyar", "the button now says HU, \"Nyelv: Magyar\"");
        }

        SettingsWindow settings = new SettingsWindow(null, owner => false) { Owner = main };
        settings.Show();
        await Wait(300);
        List<RadioButton> choices = Descendants(settings).OfType<RadioButton>().ToList();
        Check(string.Join(", ", choices.Select(c => (string)c.Content)) == "English, Magyar, Srpski" && choices[1].IsChecked == true,
            "Settings lists the three, Magyar picked");
        choices[2].IsChecked = true;
        await Wait(500);
        Check(Loc.Current == Loc.SerbianLatin && settings.Title == "Podešavanja", "picking Srpski in Settings switches at once (" + settings.Title + ")");
        RenderWindow(settings, "settings-row-sr");
        choices[0].IsChecked = true;
        await Wait(300);
        Check(Loc.Current == Loc.English, "and English back");
        settings.Close();
    }

    static IEnumerable<ContextMenu> OpenMenus() =>
        PresentationSource.CurrentSources.OfType<HwndSource>().Select(s => s.RootVisual).Where(v => v != null)
            .SelectMany(v => Descendants(v).Prepend(v)).OfType<ContextMenu>().Where(m => m.IsOpen);

    // Message box buttons in the app's language, not Windows'.
    static async Task BoxButtons()
    {
        Console.WriteLine("Message box buttons");
        foreach ((string code, string yes, string no) in new[] { (Loc.Hungarian, "&Igen", "&Nem"), (Loc.SerbianLatin, "&Da", "&Ne"), (Loc.English, "&Yes", "&No") })
        {
            Loc.Switch(code);
            int before = boxes.Count;
            Msg.Show("Harness", "Harness " + code, MessageBoxButton.YesNo, MessageBoxImage.Question);
            await Wait(300);
            string seen = boxes.Skip(before).FirstOrDefault() ?? "";
            Check(seen.Contains("[" + yes + "][" + no + "]"), code + ": Yes/No read " + yes + " / " + no + " (" + seen + ")");
            before = boxes.Count;
            Msg.Show("Harness", "Harness OK " + code);
            await Wait(300);
            seen = boxes.Skip(before).FirstOrDefault() ?? "";
            Check(seen.Contains("[" + Loc.T("Common.Ok") + "]"), code + ": OK reads " + Loc.T("Common.Ok") + " (" + seen + ")");
        }
    }

    // ---- Backup and restore -------------------------------------------------

    // The language is this PC's: a restore never changes it, whatever the backup's settings say (or don't).
    static void BackupKeepsLanguage()
    {
        Console.WriteLine("Backup, restore and the language");
        string parent = Path.GetDirectoryName(AppPaths.DataFolder);
        int memories = ProfileManager.TryLoadProfiles(out List<Profile> p, out _) ? p.Count : -1;

        Loc.Switch(Loc.SerbianLatin);
        string withSerbian = Path.Combine(parent, "backup-sr.zip");
        DataBackup.Create(withSerbian);

        // A backup from before languages existed: settings.json without Language (the format is unchanged).
        string old = Path.Combine(parent, "backup-before-languages.zip");
        File.Copy(withSerbian, old, true);
        using (ZipArchive zip = ZipFile.Open(old, ZipArchiveMode.Update))
        {
            ZipArchiveEntry entry = zip.GetEntry("settings.json");
            JsonObject settings;
            using (StreamReader reader = new StreamReader(entry.Open()))
                settings = (JsonObject)JsonNode.Parse(reader.ReadToEnd());
            settings.Remove("Language");
            entry.Delete();
            using (StreamWriter writer = new StreamWriter(zip.CreateEntry("settings.json").Open()))
                writer.Write(settings.ToJsonString());
        }

        Loc.Switch(Loc.Hungarian);
        DataBackup.Restore(old, out _);
        Check(AppSettings.Load().Language == Loc.Hungarian, "a backup from before languages restores, and the language stays Hungarian");
        Check(ProfileManager.TryLoadProfiles(out List<Profile> back, out _) && back.Count == memories, "with all " + memories + " memories");

        Loc.Switch(Loc.English);
        DataBackup.Restore(withSerbian, out _);
        Check(AppSettings.Load().Language == Loc.English, "a backup made in Serbian restores, and the language stays English");

        // No language picked yet (following Windows): the backup's pick doesn't come along either.
        AppSettings.Update(s => s.Language = null);
        DataBackup.Restore(withSerbian, out _);
        Check(AppSettings.Load().Language == null, "with no language picked, a backup's pick isn't taken over");
        Loc.Switch(Loc.English);
    }

    // ---- Data ---------------------------------------------------------------

    // Our own things with every special letter, choice values, an expiry, a day plan, a reminder, a note, to-dos.
    static void SeedData(MainWindow main)
    {
        List<Profile> profiles = Profiles(main);
        Profile project = WelcomeMemory.Find(profiles, ProfileType.Project);
        List<Project> projects = ProjectStore.Load(project);
        projects.Add(new Project
        {
            Id = Guid.NewGuid(), Name = "Őszi kert – Čačak, Đurđevdan", TabId = projects[0].TabId, Order = 9, LastModified = DateTime.Now,
            Tasks = new List<TaskItem> { new TaskItem { Title = "Ültetés ő/ű", IsDone = true }, new TaskItem { Title = "Žetva šljiva", IsDone = false } }
        });
        ProjectStore.SaveOrWarn(project, projects);

        Profile collection = WelcomeMemory.Find(profiles, ProfileType.Collection);
        List<Entity> items = EntityStore.Load(collection);
        items.Insert(0, new Entity
        {
            Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = "Škoda Octavia", Plate = "ŽA-123", VehicleKind = "Car",
            InspectionExpires = DateTime.Today.AddDays(12), InsuranceExpires = DateTime.Today.AddDays(-3), TabId = items[0].TabId, Order = -1,
            Logs = new Dictionary<string, List<Dictionary<string, string>>>
            {
                ["Fuel"] = new List<Dictionary<string, string>>
                {
                    new Dictionary<string, string> { ["Date"] = "2026-09-01", ["Liters"] = "40", ["Km"] = "120000" },
                    new Dictionary<string, string> { ["Date"] = "2026-10-01", ["Liters"] = "38.5", ["Km"] = "120600" }
                }
            }
        });
        items[1].Sizes.Add(new SizeQuantity { Size = "XL", Condition = "To donate", Quantity = 1 });
        EntityStore.SaveOrWarn(collection, items);

        DayPlanStore.Save(new DayPlanBlock { Id = Guid.NewGuid(), Date = DateTime.Today, StartMinute = 8 * 60, EndMinute = 9 * 60 + 30, Title = "Jóga ő ű" });
        DayPlanStore.Save(new DayPlanBlock { Id = Guid.NewGuid(), Date = DateTime.Today, StartMinute = 9 * 60, EndMinute = 10 * 60, Title = "Čitanje – đak", Done = true });
        DayPlanStore.Save(new DayPlanBlock { Id = Guid.NewGuid(), Date = DateTime.Today, StartMinute = 23 * 60, EndMinute = 24 * 60, Title = "Esti séta" });
        CalendarService.SaveDay(DateTime.Today.AddDays(2), "#27AE60", "Fogorvos", "Ne felejtsd el – ćevapi");
        QuickTodoService.Add("Bevásárlás ő", DateTime.Today);
        QuickTodoService.Add("Kupi hleb ž", DateTime.Today.AddDays(1));
        Invoke(main, "BuildNav");
    }

    static Guid SeedReminderBlock()
    {
        DayPlanBlock block = new DayPlanBlock
        {
            Id = Guid.NewGuid(), Date = DateTime.Today.AddDays(3), StartMinute = 10 * 60, EndMinute = 11 * 60, Title = "Sastanak", RemindMinutesBefore = 15
        };
        DayPlanStore.Save(block);
        DayPlanReminders.Set(block);
        return block.Id;
    }

    // Every page and window opened and closed again, the way a user would look around.
    static async Task VisitEverything(MainWindow main)
    {
        Invoke(main, "NavigateHome");
        await Wait(300);
        main.ShowSample(ProfileType.Project);
        await Wait(300);
        main.ShowSample(ProfileType.Collection);
        await Wait(300);
        Entity first = EntityStore.Load(WelcomeMemory.Find(Profiles(main), ProfileType.Collection)).OrderBy(e => e.Order).First();
        DriveItem(main, w => Wait(200)); // its window (the item window pages are in the window list), closed again
        ((CollectionView)Content(main)).Reveal(first.Id);
        await Wait(300);
        main.ShowDayPlan(DateTime.Today);
        await Wait(300);
        main.OpenCalendar();
        await Wait(400);
        main.CloseCalendar();
        OverlayManager.Route(Overlay.Reminders);
        await Wait(400);
        OverlayManager.CloseAll();
        OverlayManager.Route(Overlay.QuickNotes);
        await Wait(400);
        OverlayManager.CloseAll();
        main.OpenSearch();
        await Wait(300);
        main.CloseSearch();
        // A project opened and closed without a change (notes and description are compared as stored text).
        main.ShowSample(ProfileType.Project);
        await Wait(300);
        main.OpenSampleNotebook();
        ProjectWindow project = await WaitFor(() => GuidedTour.Open<ProjectWindow>());
        Check(project != null, "the example project opened (" + Loc.Current + ")");
        await Wait(400);
        project?.Close();
        await Wait(400);
        Invoke(main, "NavigateHome");
        await Wait(300);
    }

    // Every data file but the logs and settings.json (the language's own file): its SHA-256.
    static Dictionary<string, string> HashData()
    {
        Dictionary<string, string> hashes = new Dictionary<string, string>();
        foreach (string file in Directory.GetFiles(AppPaths.DataFolder, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(AppPaths.DataFolder, file);
            if (relative.EndsWith(".log") || relative == "settings.json")
                continue;
            using (FileStream stream = File.OpenRead(file))
                hashes[relative] = Convert.ToHexString(SHA256.HashData(stream));
        }
        return hashes;
    }

    static void CompareHashes(Dictionary<string, string> before, Dictionary<string, string> after, string when)
    {
        List<string> changed = before.Keys.Union(after.Keys)
            .Where(k => !before.TryGetValue(k, out string b) || !after.TryGetValue(k, out string a) || a != b).ToList();
        Check(changed.Count == 0, "every data file byte for byte the same " + when + " (" + before.Count + " files)"
            + (changed.Count > 0 ? ": changed " + string.Join(", ", changed) : ""));
    }

    // ---- Screenshots and message boxes --------------------------------------

    // The window as drawn, adorners (the tour) included, at its own size.
    static void RenderWindow(Window window, string name)
    {
        try
        {
            window.UpdateLayout();
            FrameworkElement root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
            DpiScale dpi = VisualTreeHelper.GetDpi(window);
            RenderTargetBitmap bitmap = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            DrawingVisual back = new DrawingVisual();
            using (DrawingContext dc = back.RenderOpen())
                dc.DrawRectangle((Brush)Application.Current.FindResource("BgMain"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            bitmap.Render(back);
            bitmap.Render(root);
            SavePng(bitmap, name);
        }
        catch (Exception e)
        {
            Console.WriteLine("  (screenshot " + name + " failed: " + e.Message + ")");
        }
    }

    static void RenderPopup(FrameworkElement element, string name)
    {
        element.UpdateLayout();
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)Math.Max(1, element.ActualWidth), (int)Math.Max(1, element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        SavePng(bitmap, name);
    }

    static void SavePng(BitmapSource bitmap, string name)
    {
        PngBitmapEncoder png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(shots);
        using (FileStream file = File.Create(Path.Combine(shots, name + ".png")))
            png.Save(file);
    }

    // Like StartAnswering, but it also notes the buttons' labels; answers Yes where there is one, else OK.
    static void StartBoxWatcher()
    {
        uint self = (uint)Environment.ProcessId;
        Thread watcher = new Thread(() =>
        {
            while (true)
            {
                EnumWindows((hwnd, _) =>
                {
                    GetWindowThreadProcessId(hwnd, out uint pid);
                    StringBuilder cls = new StringBuilder(64);
                    GetClassName(hwnd, cls, 64);
                    if (pid != self || cls.ToString() != "#32770" || !IsWindowVisible(hwnd))
                        return true;
                    StringBuilder title = new StringBuilder(256), text = new StringBuilder(2048);
                    GetWindowText(hwnd, title, 256);
                    GetWindowText(GetDlgItem(hwnd, 0xFFFF), text, 2048);
                    string buttons = "";
                    foreach (int id in new[] { 6, 7, 1, 2 })
                    {
                        IntPtr b = GetDlgItem(hwnd, id);
                        if (b == IntPtr.Zero) continue;
                        StringBuilder label = new StringBuilder(64);
                        GetWindowText(b, label, 64);
                        buttons += "[" + label + "]";
                    }
                    int answer = new[] { 6, 1, 2 }.First(i => GetDlgItem(hwnd, i) != IntPtr.Zero || i == 2);
                    lock (boxes)
                        boxes.Add(title + " | " + text.ToString().Replace("\n", " ") + " | " + buttons);
                    PostMessage(GetDlgItem(hwnd, answer), 0x00F5, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(200);
                    return true;
                }, IntPtr.Zero);
                Thread.Sleep(150);
            }
        }) { IsBackground = true };
        watcher.Start();
    }
}
