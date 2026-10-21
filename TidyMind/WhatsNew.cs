using System;
using System.Linq;
using System.Windows;

namespace TidyMind
{
    // "What's new": a few plain points about the release (WhatsNew.Points in the Strings files, one per line), shown
    // once after an update to the version they describe, and any time from the ? button. A fresh install doesn't get it.
    public static class WhatsNew
    {
        // The version WhatsNew.Points describe. When a release gets new points, write them in all three Strings files and
        // set this to its <Version> (publish-release.ps1 warns if they differ; the note then simply isn't shown).
        public const string NotesVersion = "1.4.0";

        public static string[] Points => Loc.T("WhatsNew.Points").Split('\n').Select(p => p.Trim()).Where(p => p.Length > 0).ToArray();

        // At start, once the main window is up. hasData: there are memories, so this is an update of a copy in use (one
        // from before LastVersion was kept), not a fresh install.
        public static void ShowIfNew(Window owner, bool hasData) => ShowIfNew(owner, hasData, Updates.CurrentVersion.ToString(3));

        // current: the running version (the harness passes others). True if the note was shown.
        internal static bool ShowIfNew(Window owner, bool hasData, string current)
        {
            AppSettings settings = AppSettings.Load(out bool readable);
            if (!readable || settings.LastVersion == current)
                return false;
            bool due = current == NotesVersion && (settings.LastVersion != null || hasData);
            AppSettings.Update(s => s.LastVersion = current);
            if (due)
                Show(owner);
            return due;
        }

        public static void Show(Window owner)
        {
            WhatsNewWindow? open = Application.Current.Windows.OfType<WhatsNewWindow>().FirstOrDefault();
            if (open != null)
                open.Activate();
            else
                new WhatsNewWindow { Owner = owner }.Show();
        }
    }
}
