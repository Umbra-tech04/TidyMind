using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TidyMind
{
    // A reminder whose Windows notification didn't go through is never lost: the reminder process hands it to the open
    // TidyMind, which shows it in a small window of its own; with TidyMind closed, the reminder process shows that
    // window itself. Handed over through files in the temp folder (same user) and a named signal.
    public static class ReminderPopups
    {
        private const string SignalName = @"Local\TidyMind_ReminderPopups";
        private static string Folder => Path.Combine(Path.GetTempPath(), "TidyMind-reminder-popups");

        // Held for the app's whole life, like App's activation signal.
        private static EventWaitHandle signal;
        private static RegisteredWaitHandle wait;

        // In the open app, at startup: show what's handed over, now and whenever the signal comes.
        public static void Listen(Dispatcher dispatcher)
        {
            signal = new EventWaitHandle(false, EventResetMode.AutoReset, SignalName);
            wait = ThreadPool.RegisterWaitForSingleObject(signal,
                (state, timedOut) => dispatcher.BeginInvoke(new Action(ShowHandedOver)), null, Timeout.Infinite, executeOnlyOnce: false);
            dispatcher.BeginInvoke(new Action(ShowHandedOver), DispatcherPriority.ApplicationIdle);
        }

        public static void StopListening() => wait?.Unregister(null);

        // In the reminder process: false if TidyMind isn't open (or the handover couldn't be written).
        public static bool HandToApp(string title, string message)
        {
            if (!EventWaitHandle.TryOpenExisting(SignalName, out EventWaitHandle open))
                return false;
            using (open)
            {
                Directory.CreateDirectory(Folder);
                if (!AtomicFile.TryWriteAllText(Path.Combine(Folder, Guid.NewGuid().ToString("N") + ".json"),
                        JsonSerializer.Serialize(new[] { title, message })))
                    return false;
                open.Set();
                return true;
            }
        }

        // In the reminder process, with TidyMind closed: until it's closed (or the task's time limit ends the process).
        public static void ShowHere(string title, string message) => Window(title, message).ShowDialog();

        private static void ShowHandedOver()
        {
            if (!Directory.Exists(Folder))
                return;
            foreach (string file in Directory.GetFiles(Folder, "*.json").OrderBy(File.GetCreationTimeUtc))
            {
                string[] parts;
                try
                {
                    parts = JsonSerializer.Deserialize<string[]>(File.ReadAllText(file));
                    File.Delete(file);
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
                {
                    continue; // still being written, or not ours: the next signal tries again
                }
                if (parts != null && parts.Length == 2)
                    Window(parts[0], parts[1]).Show();
            }
        }

        // A plain, always-on-top card: the reminder's title, its message and OK.
        private static Window Window(string title, string message)
        {
            Button ok = new Button { Content = Loc.T("Common.Ok"), MinWidth = 92, Height = 34, Padding = new Thickness(14, 0, 14, 0),
                HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, Margin = new Thickness(0, 18, 0, 0) };
            Window window = new Window
            {
                Title = title,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                Topmost = true,
                ShowActivated = true,
                Background = (Brush)Application.Current.FindResource("BgMain"),
                Content = new StackPanel
                {
                    Margin = new Thickness(24, 20, 24, 20),
                    Children =
                    {
                        new TextBlock { Text = "🔔  " + title, FontSize = 16, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap,
                            Foreground = (Brush)Application.Current.FindResource("TextMain") },
                        new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
                            Foreground = (Brush)Application.Current.FindResource("TextBody") },
                        ok
                    }
                }
            };
            ok.Click += (s, e) => window.Close();
            return window;
        }
    }
}
