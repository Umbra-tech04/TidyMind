using System;
using System.IO;

namespace TidyMind
{
    // Where everything TidyMind stores lives: %LOCALAPPDATA%\TidyMind. Every data file and folder is built from here,
    // never from the working folder, which depends on how the app was started (a shortcut's "Start in", a terminal,
    // the reminder task) and which file dialogs can change while the app runs.
    public static class AppPaths
    {
        public static string DataFolder { get; } =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyMind");

        // A file or folder directly inside the data folder.
        public static string Data(string name) => Path.Combine(DataFolder, name);
    }
}
