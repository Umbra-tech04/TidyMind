using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace TidyMind
{
    // The UI side of TransferPackage: "Export for transfer…" on a project (its card menu, Project Details), a
    // collection (its sidebar menu) and a collection item (its card menu), and Settings > Import. Picking the file,
    // a progress window while files are packed or unpacked, the preview, and saying how it went.
    public static class TransferUI
    {
        // "Export for transfer…" in a right-click menu (project cards, sidebar collections, collection items).
        public static MenuItem MenuEntry(Action export)
        {
            MenuItem item = new MenuItem { Header = Loc.T("Transfer.ExportMenu") };
            item.Click += (s, e) => export();
            return item;
        }

        public static void ExportProject(Window? owner, Project project)
        {
            string? path = AskWhere(owner, project.Name);
            if (path == null)
                return;
            int writtenPages = TransferPackage.WrittenPages(project); // here: it reads formatted pages (UI thread)
            Export(owner, project.Name, path, (progress, cancel) => TransferPackage.ExportProject(project, writtenPages, path, progress, cancel));
        }

        // Read from its files, as the collection view shows it, and only read (exporting never writes a tabs file); an
        // unreadable file is said, not exported around.
        public static void ExportCollection(Window? owner, Profile memory)
        {
            List<MemoryTab> tabs = new List<MemoryTab>();
            bool readable = EntityStore.TryLoad(memory, out List<Entity> entities, out string problem);
            try
            {
                if (readable && File.Exists(MemoryFiles.Tabs(memory)))
                    tabs = (JsonSerializer.Deserialize<List<MemoryTab>>(File.ReadAllText(MemoryFiles.Tabs(memory))) ?? tabs)
                        .Where(t => t != null).OrderBy(t => t.Order).ToList();
            }
            catch (Exception e) when (e is JsonException || e is IOException || e is UnauthorizedAccessException)
            {
                readable = false;
                problem = e.Message;
            }
            if (!readable)
            {
                Msg.Show(owner, Loc.F("Transfer.Export.Unreadable", memory.Name, problem), Loc.T("Transfer.Export.Caption"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string? path = AskWhere(owner, memory.Name);
            if (path != null)
                Export(owner, memory.Name, path, (progress, cancel) => TransferPackage.ExportCollection(memory, tabs, entities, path, progress, cancel));
        }

        // One item of a collection, as its card shows it (items are saved as soon as they change).
        public static void ExportItem(Window? owner, Entity entity)
        {
            string? path = AskWhere(owner, entity.Name);
            if (path != null)
                Export(owner, entity.Name, path, (progress, cancel) => TransferPackage.ExportItem(entity, path, progress, cancel));
        }

        private static string? AskWhere(Window? owner, string name)
        {
            SaveFileDialog dialog = new SaveFileDialog
            {
                FileName = ExportMenu.SafeFileName(name) + TransferPackage.Extension,
                Filter = Loc.T("Transfer.Filter"),
                DefaultExt = TransferPackage.Extension
            };
            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }

        private static void Export(Window? owner, string name, string path, Action<IProgress<double>, CancellationToken> write)
        {
            bool existed = File.Exists(path);
            Exception? failure = Run(owner, Loc.T("Transfer.Export.Caption"), Loc.F("Transfer.Progress.Export", name), write);
            if (failure is OperationCanceledException)
                return;
            if (failure != null)
            {
                LogIfUnexpected(failure);
                Msg.Show(owner, Loc.F("Transfer.Export.Failed", failure.Message) + (existed ? "\n\n" + Loc.T("Export.ExistingKept") : ""),
                    Loc.T("Transfer.Export.Caption"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            Msg.Show(owner, Loc.F("Transfer.Export.Done", path), Loc.T("Transfer.Export.Caption"), MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        // ---- Import -------------------------------------------------------

        // Asks for the file, then ImportFile. Returns the memory to show afterwards (the one the project went into, or
        // the new collection), or null if nothing was imported.
        public static Profile? Import(Window owner, List<Profile> profiles, Profile? current, Func<string> nextColor)
        {
            OpenFileDialog dialog = new OpenFileDialog { Filter = Loc.T("Transfer.Filter") };
            return dialog.ShowDialog(owner) == true ? ImportFile(owner, dialog.FileName, profiles, current, nextColor) : null;
        }

        // Checks the package, shows what's in it (and, for a project or an item, asks which memory it goes into: the
        // open one if it is of that kind), then stages and adds it. Any problem is said in a message and nothing is
        // added.
        public static Profile? ImportFile(Window owner, string path, List<Profile> profiles, Profile? current, Func<string> nextColor)
        {
            OpenedPackage package;
            try
            {
                package = TransferPackage.Open(path);
            }
            catch (TransferException e)
            {
                Msg.Show(owner, e.Message, Loc.T("Transfer.Import.Caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            ImportDialog preview = new ImportDialog(package, profiles, current) { Owner = owner };
            if (preview.ShowDialog() != true)
                return null;
            Profile? target = preview.Target;

            StagedImport? staged = null;
            Exception? failure = Run(owner, Loc.T("Transfer.Import.Caption"), Loc.F("Transfer.Progress.Import", package.Name),
                (progress, cancel) => staged = TransferPackage.Stage(package, progress, cancel));
            try
            {
                if (failure is OperationCanceledException)
                    return null;
                if (failure != null)
                {
                    LogIfUnexpected(failure);
                    Msg.Show(owner, Loc.F("Transfer.Import.Failed", failure.Message), Loc.T("Transfer.Import.Caption"), MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return null;
                }
                Profile into = package.IsCollection ? TransferPackage.CommitCollection(staged!, profiles, nextColor())
                    : package.IsItem ? TransferPackage.CommitItem(staged!, target, profiles, nextColor(), Loc.T("Transfer.Import.NewCollectionName"))
                    : CommitInto(staged!, target!);
                // Imported documents get their reminders as if they had just been entered here.
                if (staged!.Added.Select(item => VehicleReminders.Follow(null, item)).ToList().FirstOrDefault(p => p != null) is string problem)
                    Msg.Show(owner, problem, Loc.T("Transfer.Import.Caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return into;
            }
            catch (Exception e) when (e is TransferException || e is IOException || e is UnauthorizedAccessException)
            {
                Msg.Show(owner, Loc.F("Transfer.Import.Failed", e.Message), Loc.T("Transfer.Import.Caption"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return null;
            }
            finally
            {
                staged?.Dispose();
            }
        }

        private static Profile CommitInto(StagedImport staged, Profile target)
        {
            TransferPackage.CommitProject(staged, target);
            return target;
        }

        // A package that can't be read or written is the user's to know about; anything else is a bug, and goes to
        // error.log as well as into the message.
        private static void LogIfUnexpected(Exception failure)
        {
            if (!(failure is TransferException || failure is IOException || failure is UnauthorizedAccessException
                  || failure is InvalidDataException))
                UnhandledErrors.LogQuietly("transfer", failure);
        }

        // ---- Progress -----------------------------------------------------

        // `work` on a worker thread while a small window shows how far it is, with Cancel. Returns what it threw, or
        // null; OperationCanceledException whenever Cancel was pressed, even if the work got to its end first (an
        // import then adds nothing).
        internal static Exception? Run(Window? owner, string caption, string text, Action<IProgress<double>, CancellationToken> work)
        {
            CancellationTokenSource cancel = new CancellationTokenSource();
            ProgressBar bar = new ProgressBar { Height = 6, Maximum = 1, Margin = new Thickness(0, 12, 0, 16) };
            Button cancelButton = new Button
            {
                Content = Loc.T("Common.Cancel"),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"),
                Width = 92,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                IsCancel = true
            };
            Window window = new Window
            {
                Title = caption,
                Width = 380,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                Owner = owner,
                Background = (System.Windows.Media.Brush)Application.Current.FindResource("BgMain"),
                Content = new StackPanel
                {
                    Margin = new Thickness(24, 20, 24, 18),
                    Children =
                    {
                        new TextBlock
                        {
                            Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13,
                            Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("TextMain")
                        },
                        bar,
                        cancelButton
                    }
                }
            };

            Progress<double> progress = new Progress<double>(value => bar.Value = value);
            Exception? failure = null;
            bool finished = false;
            cancelButton.Click += (s, e) => window.Close();
            window.Closing += (s, e) =>
            {
                if (finished) return;
                e.Cancel = true; // closes once the work has stopped
                cancelButton.IsEnabled = false;
                cancel.Cancel();
            };
            window.Loaded += async (s, e) =>
            {
                try
                {
                    await Task.Run(() => work(progress, cancel.Token));
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finished = true;
                window.Close();
            };
            window.ShowDialog();
            return cancel.IsCancellationRequested ? new OperationCanceledException() : failure;
        }
    }
}
