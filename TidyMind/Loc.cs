using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Resources;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;

namespace TidyMind
{
    // One language TidyMind can be shown in. Name is written in the language itself, as the switcher lists it.
    public record LanguageOption(string Code, string Name, string ShortName);

    // The language every window is shown in: its texts (Strings.resx, with Strings.hu.resx and Strings.sr-Latn.resx
    // beside it) and its date and number formats. Only what is displayed comes from here: stored data is
    // culture-invariant, so a switch only redraws from what's on disk and changes no data file (settings.json keeps
    // the choice).
    //
    // Keys are named by area ("Home.Discovery.Title"). A count goes through Plural, never "s" glued on, and a sentence
    // is one string with {0} placeholders, so each language keeps its own word order.
    public static class Loc
    {
        public const string English = "en";
        public const string Hungarian = "hu";
        public const string SerbianLatin = "sr-Latn";

        public static readonly IReadOnlyList<LanguageOption> Languages = new[]
        {
            new LanguageOption(English, "English", "EN"),
            new LanguageOption(Hungarian, "Magyar", "HU"),
            new LanguageOption(SerbianLatin, "Srpski", "SR")
        };

        private static readonly ResourceManager Strings = new ResourceManager("TidyMind.Strings", typeof(Loc).Assembly);

        // Windows' own regional format, read before the culture is replaced (Apply only runs after this): typed dates
        // are read in its order, and English keeps its first day of the week, as before the app had languages.
        public static CultureInfo RegionalCulture { get; } = CultureInfo.CurrentCulture;
        private static readonly DayOfWeek windowsFirstDay = RegionalCulture.DateTimeFormat.FirstDayOfWeek;
        private static bool initialized;

        public static string Current { get; private set; } = English;
        public static LanguageOption CurrentOption => Languages.First(l => l.Code == Current);

        // Dates, numbers and sorting for the current language (also the thread's CurrentCulture and CurrentUICulture).
        public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

        public static DayOfWeek FirstDayOfWeek => Culture.DateTimeFormat.FirstDayOfWeek;

        // Name lists in the current language's alphabetical order (Hungarian ő after ö, Serbian č and ć after c).
        public static StringComparer Comparer { get; private set; } = StringComparer.Create(CultureInfo.GetCultureInfo("en-US"), true);

        // Raised on the UI thread after the language changed; Track is the leak-free way to listen from a window.
        public static event Action? LanguageChanged;

        // At startup, before any window or message: the saved choice, or else the Windows display language.
        public static void Initialize()
        {
            if (initialized)
                return;
            initialized = true;
            Apply(Normalize(AppSettings.Load().Language) ?? FromWindows(CultureInfo.CurrentUICulture));
        }

        // Hungarian and Serbian (either script, shown in Latin) have their own texts; everything else gets English.
        public static string FromWindows(CultureInfo uiCulture) => uiCulture.TwoLetterISOLanguageName switch
        {
            "hu" => Hungarian,
            "sr" => SerbianLatin,
            _ => English
        };

        // A stored code this version knows, or null.
        public static string? Normalize(string? code) => Languages.FirstOrDefault(l => l.Code == code)?.Code;

        // The user's pick: remembered in settings.json and shown at once in every open window. False if it couldn't be
        // remembered (it still applies until TidyMind closes).
        public static bool Switch(string code)
        {
            code = Normalize(code) ?? English;
            bool saved = AppSettings.Update(s => s.Language = code);
            if (code != Current)
            {
                Apply(code);
                LanguageChanged?.Invoke();
            }
            return saved;
        }

        private static void Apply(string code)
        {
            CultureInfo culture = code switch
            {
                Hungarian => new CultureInfo("hu-HU"),
                SerbianLatin => new CultureInfo("sr-Latn-RS"),
                _ => new CultureInfo("en-US")
            };
            culture.DateTimeFormat.FirstDayOfWeek = code == English ? windowsFirstDay : DayOfWeek.Monday;

            Current = code;
            Culture = culture;
            Comparer = StringComparer.Create(culture, ignoreCase: true);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            // WPF's own text-box menus (Cut / Copy / Paste) take their labels from these commands; WPF has no Hungarian
            // or Serbian texts of its own. The menus are built as they open, so the next one shows the new labels.
            ApplicationCommands.Cut.Text = T("Common.Cut");
            ApplicationCommands.Copy.Text = T("Common.Copy");
            ApplicationCommands.Paste.Text = T("Common.Paste");

            if (Application.Current != null)
                foreach (Window window in Application.Current.Windows)
                    SetLanguage(window);
            LocSource.Instance.Refresh();
        }

        // WPF's own language for a window and everything in it (bindings' formats, the date pickers). Each window
        // gets it as it loads (App), and every open one again on a switch.
        public static void SetLanguage(FrameworkElement element) =>
            element.Language = XmlLanguage.GetLanguage(Culture.IetfLanguageTag);

        // A date picker's calendar starts the week where the app's calendar does (for English, where Windows does), and
        // an empty one says "Select a date" in the app's language: WPF's own text is English for Hungarian and Serbian.
        // The text is set on the template's watermark part (a local value, so it wins over WPF's), bound so it follows
        // a switch.
        public static void PrepareDatePicker(DatePicker picker)
        {
            picker.FirstDayOfWeek = FirstDayOfWeek;
            if (picker.Template?.FindName("PART_TextBox", picker) is DatePickerTextBox box)
            {
                box.ApplyTemplate();
                if (box.Template?.FindName("PART_Watermark", box) is ContentControl watermark)
                    watermark.SetBinding(ContentControl.ContentProperty, Bind("Common.SelectDate"));
            }
        }

        // ---- Texts ----------------------------------------------------------

        // The text for a key in the current language. A key not translated yet comes back in English; a key that
        // doesn't exist at all (a bug) comes back as itself, logged in debug builds.
        public static string T(string key)
        {
            string? text = Strings.GetString(key, Culture);
            if (text == null)
            {
                Debug.WriteLine("Loc: no text for key '" + key + "'");
                return key;
            }
            CheckTranslated(key);
            return text;
        }

        // The text for a key built from data (a stored choice value), or null if there is no such key.
        public static string? Find(string key) => Strings.GetString(key, Culture);

        // A binding to a key's text that follows the language ({local:Tr Key} in XAML is the same).
        public static Binding Bind(string key) =>
            new Binding("[" + key + "]") { Source = LocSource.Instance, Mode = BindingMode.OneWay };

        // A sentence with placeholders: {0}, {1}… filled in, numbers and dates in the current language's format.
        public static string F(string key, params object?[] args) => string.Format(Culture, T(key), args);

        // A sentence about a count. The count is {0}, `args` follow as {1}, {2}… The key has one text per plural form:
        //   English    key.One (1)               key.Other (0, 2, 3…)
        //   Hungarian  key.Other only: a noun after a number stays singular ("3 feladat")
        //   Serbian    key.One (1, 21, 31…)      key.Few (2–4, 22–24…)   key.Other (0, 5–20, 25–30…, and 11–14)
        public static string Plural(string key, long count, params object?[] args)
        {
            // The exact form from this language's own texts: an English ".One" must never stand in for a missing
            // Serbian one, so a form the language lacks falls back to its ".Other".
            string form = key + "." + PluralForm(Current, count);
            string? text = (Current == English ? Strings.GetString(form, Culture) : OwnTexts()?.GetString(form))
                           ?? Strings.GetString(key + ".Other", Culture);
            if (text == null)
            {
                Debug.WriteLine("Loc: no plural text for key '" + key + "'");
                return count.ToString(Culture);
            }
            object?[] all = new object?[args.Length + 1];
            all[0] = count;
            args.CopyTo(all, 1);
            return string.Format(Culture, text, all);
        }

        public static string PluralForm(string language, long count)
        {
            long n = Math.Abs(count);
            switch (language)
            {
                case Hungarian:
                    return "Other";
                case SerbianLatin:
                    long last = n % 10, lastTwo = n % 100;
                    if (last == 1 && lastTwo != 11) return "One";
                    if (last >= 2 && last <= 4 && (lastTwo < 12 || lastTwo > 14)) return "Few";
                    return "Other";
                default:
                    return n == 1 ? "One" : "Other";
            }
        }

        // Hungarian's or Serbian's own texts, without the English fallback.
        private static ResourceSet? OwnTexts() =>
            Strings.GetResourceSet(new CultureInfo(Current), createIfNotExists: true, tryParents: false);

        // The key has a text of its own in the current language (English is the fallback for the others).
        [Conditional("DEBUG")]
        private static void CheckTranslated(string key)
        {
            if (Current != English && OwnTexts()?.GetString(key) == null)
                Debug.WriteLine("Loc: '" + key + "' isn't translated to " + Current + "; showing English");
        }

        // ---- Listening for a switch ------------------------------------------

        // Calls `refresh` whenever the language changes while `owner` is on screen, and once when it comes back after
        // a change it missed. Held only while loaded, so a closed window or a replaced view is never kept alive.
        public static void Track(FrameworkElement owner, Action refresh)
        {
            string shown = Current;
            void Changed()
            {
                if (shown == Current)
                    return;
                shown = Current;
                refresh();
            }

            owner.Loaded += (s, e) =>
            {
                LanguageChanged -= Changed;
                LanguageChanged += Changed;
                Changed();
            };
            owner.Unloaded += (s, e) => LanguageChanged -= Changed;
            if (owner.IsLoaded)
                LanguageChanged += Changed;
        }
    }

    // What {local:Tr Key} binds to: one indexer for every key, refreshed as a whole when the language changes.
    public sealed class LocSource : INotifyPropertyChanged
    {
        public static LocSource Instance { get; } = new LocSource();

        public string this[string key] => Loc.T(key);

        public event PropertyChangedEventHandler? PropertyChanged;

        internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Binding.IndexerName));
    }

    // A translated text in XAML that follows the language live: Text="{local:Tr Settings.Title}".
    [MarkupExtensionReturnType(typeof(string))]
    public class TrExtension : MarkupExtension
    {
        public TrExtension() { }

        public TrExtension(string key) => Key = key;

        [ConstructorArgument("key")]
        public string Key { get; set; } = "";

        public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Bind(Key).ProvideValue(serviceProvider);
    }
}
