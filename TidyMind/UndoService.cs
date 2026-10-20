using System.IO;
using System.Windows;

namespace TidyMind
{
    // Undo and redo of data changes (Ctrl+Z, Ctrl+Y, the toast's Undo). A change is a step (UndoStep) in a history
    // (UndoHistory); histories are kept in memory only and end with what they belong to: the page shown in the main
    // window (Page: a memory, Home or the Day Plan; another page ends it, the same one shown again keeps it), the
    // session (App: the sidebar's memories and the calendar), or their own window (Reminders, Project Details). The app
    // closing ends them all.
    //
    // A step holds the files it could bring back (a deleted item's pictures, a deleted memory's folder): every file
    // delete skips a held file (IsHeld), and once the step ends, each is deleted only if nothing saved and no other step
    // still refers to it (UndoFiles.Clean).
    public static class UndoService
    {
        private static readonly List<UndoHistory> live = new List<UndoHistory>();

        // Files held while a change is being recorded (UndoChange): its own deletes leave them for the step.
        private static readonly List<IReadOnlyCollection<HeldFile>> recording = new List<IReadOnlyCollection<HeldFile>>();

        private static long stamp;
        private static string? pageKey;

        public static UndoHistory App { get; private set; } = new UndoHistory("app");

        public static UndoHistory Page { get; private set; } = new UndoHistory("page");

        // A step was added, undone, redone or dropped, or a history ended: toasts and menus follow.
        public static event Action? Changed;

        // The main window shows another page (a memory by its Id, Home, the Day Plan): the page before it can't be
        // undone any more. The same page again (a memory renamed and shown anew) keeps its steps.
        public static void ShowPage(string key)
        {
            if (key == pageKey)
                return;
            pageKey = key;
            UndoHistory ended = Page;
            Page = new UndoHistory("page " + key);
            ended.End();
        }

        // The app is closing: every step ends, and the files no one needs any more go.
        public static void Shutdown()
        {
            Closed = true;
            EndAll();
        }

        // Every history ends now (Shutdown; the harness, to see what is deleted once nothing can be undone).
        public static void EndAll()
        {
            foreach (UndoHistory history in live.ToList())
                history.End();
            App = new UndoHistory("app");
            Page = new UndoHistory("page");
            pageKey = null;
        }

        // A restore replaced the data folder: no step may change it or delete from it any more. Nothing is cleaned up:
        // the files there now are the backup's.
        public static void Forget()
        {
            foreach (UndoHistory history in live.ToList())
                history.Abandon();
            App = new UndoHistory("app");
            Page = new UndoHistory("page");
            pageKey = null;
        }

        // Whether a live step, or a change being recorded, may still need this file.
        public static bool IsHeld(HeldFile file)
        {
            if (recording.Any(files => files.Contains(file)))
                return true;
            return live.Any(history => history.Steps.Any(step => step.Held.Contains(file)));
        }

        public static bool IsHeld(HeldKind kind, string? name) => !string.IsNullOrEmpty(name) && IsHeld(HeldFile.Of(kind, name));

        // The memories whose folder a live step (or a change being recorded) holds: deleted ones that can come back.
        internal static IEnumerable<Guid> HeldMemories() =>
            recording.SelectMany(files => files).Concat(live.SelectMany(history => history.Steps).SelectMany(step => step.Held))
                .Where(f => f.Kind == HeldKind.MemoryFolder)
                .Select(f => Guid.TryParseExact(f.Name, "N", out Guid id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty).Distinct().ToList();

        // The app is closing (EndAll ran): a change finishing after that (a dialog closed by the shutdown) isn't recorded,
        // so its deletes happen at once, as without undo, instead of waiting for a history that never ends.
        public static bool Closed { get; private set; }

        internal static long NextStamp() => ++stamp;

        internal static void Register(UndoHistory history) => live.Add(history);

        internal static void Unregister(UndoHistory history) => live.Remove(history);

        internal static void HoldWhileRecording(IReadOnlyCollection<HeldFile> files) => recording.Add(files);

        internal static void Release(IReadOnlyCollection<HeldFile> files) => recording.Remove(files);

        internal static void RaiseChanged() => Changed?.Invoke();
    }

    // One undoable change: done (it can be undone) or undone (it can be redone).
    public abstract class UndoStep
    {
        private bool ended;

        protected UndoStep(string label, bool destructive)
        {
            Label = label;
            Destructive = destructive;
        }

        // What it did, in the current language at the time ("“Car” deleted"), for the toast and the messages.
        public string Label { get; }

        // A delete: said in a toast with Undo right away.
        public bool Destructive { get; }

        // The collection item it changed, if one: the item's own window undoes only its own steps.
        public Guid? Target { get; init; }

        public UndoHistory? History { get; internal set; }

        public bool IsDone { get; internal set; } = true;

        // When it was last done, undone or redone: the newest of several histories goes first.
        internal long Stamp { get; set; }

        internal virtual IReadOnlyCollection<HeldFile> Held => Array.Empty<HeldFile>();

        // Takes the change back, or makes it again. Null if it went through; otherwise why not, with `conflict` when it
        // never can (what it changed was changed again since). Nothing is changed when it doesn't go through.
        internal abstract string? Revert(out bool conflict);

        internal abstract string? Reapply(out bool conflict);

        // What to tell after it went through (a reminder couldn't be set back), once.
        internal virtual string? TakeNote() => null;

        // Out of its history for good: the files it held go if nothing else needs them.
        internal void End(bool clean = true)
        {
            if (ended)
                return;
            ended = true;
            History = null;
            if (clean)
                UndoFiles.Clean(Held);
        }
    }

    // A change that lives in a window only (Project Details' tasks, pages and files until the window closes and
    // saves): undone and redone by the window itself.
    public sealed class LocalStep : UndoStep
    {
        private readonly Action undo, redo;

        public LocalStep(string label, bool destructive, Action undo, Action redo) : base(label, destructive)
        {
            this.undo = undo;
            this.redo = redo;
        }

        // A change made in a window with a history of its own (Project Details, a notebook): pushed there, and said in
        // the window's toast when it's a delete.
        public static void Record(UndoHistory history, string label, bool destructive, Action undo, Action redo)
        {
            LocalStep step = new LocalStep(label, destructive, undo, redo);
            history.Push(step);
            if (destructive)
                UndoHost.Announce(step);
        }

        internal override string? Revert(out bool conflict)
        {
            conflict = false;
            undo();
            return null;
        }

        internal override string? Reapply(out bool conflict)
        {
            conflict = false;
            redo();
            return null;
        }
    }

    // The steps of one place, newest last, at most Limit; a new step empties the redo list.
    public sealed class UndoHistory
    {
        public const int Limit = 50;

        private readonly List<UndoStep> done = new List<UndoStep>();
        private readonly List<UndoStep> undone = new List<UndoStep>();

        public UndoHistory(string name)
        {
            Name = name;
            UndoService.Register(this);
        }

        public string Name { get; }

        public bool HasEnded { get; private set; }

        public UndoStep? NextUndo => done.Count > 0 ? done[^1] : null;

        public UndoStep? NextRedo => undone.Count > 0 ? undone[^1] : null;

        public int Count => done.Count + undone.Count;

        internal IEnumerable<UndoStep> Steps => done.Concat(undone);

        public void Push(UndoStep step)
        {
            if (HasEnded)
            {
                step.End(); // nothing can undo it any more
                return;
            }
            step.History = this;
            step.IsDone = true;
            step.Stamp = UndoService.NextStamp();
            List<UndoStep> dropped = undone.ToList();
            undone.Clear();
            done.Add(step);
            if (done.Count > Limit)
            {
                dropped.Add(done[0]);
                done.RemoveAt(0);
            }
            EndAll(dropped, clean: true);
            UndoService.RaiseChanged();
        }

        // Several steps out at once: their files looked at in one go (each look reads every collection and project).
        private static void EndAll(List<UndoStep> steps, bool clean)
        {
            foreach (UndoStep step in steps)
                step.End(clean: false);
            if (clean)
                UndoFiles.Clean(steps.SelectMany(s => s.Held));
        }

        // Takes back (undo) or makes again (redo) the step, which must be this history's next that way. One that never can
        // any more (what it changed was changed again since) is dropped; one whose file couldn't be read or written stays,
        // to be tried again. Either way the user is told why, over `owner`, and nothing was changed.
        public bool Apply(UndoStep step, bool undo, Window? owner)
        {
            if (step != (undo ? NextUndo : NextRedo))
                return false;

            string? problem = undo ? step.Revert(out bool conflict) : step.Reapply(out conflict);
            if (problem != null)
            {
                if (conflict)
                {
                    (undo ? done : undone).Remove(step);
                    step.End();
                }
                UndoService.RaiseChanged();
                string text = conflict
                    ? Loc.T(undo ? "Undo.Conflict" : "Undo.RedoConflict") + "\n\n" + step.Label
                    : Loc.T(undo ? "Undo.Failed" : "Undo.RedoFailed") + "\n\n" + step.Label + "\n\n" + problem;
                Msg.Show(owner, text, Loc.T(undo ? "Undo.Caption" : "Undo.RedoCaption"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            (undo ? done : undone).Remove(step);
            (undo ? undone : done).Add(step);
            step.IsDone = !undo;
            step.Stamp = UndoService.NextStamp();
            UndoService.RaiseChanged();
            if (step.TakeNote() is string note)
                Msg.Show(owner, note, Loc.T(undo ? "Undo.Caption" : "Undo.RedoCaption"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return true;
        }

        // Its page or window is gone: every step ends (the files no one needs any more go).
        public void End() => Close(clean: true);

        // After a restore: every step ends without touching a file.
        internal void Abandon() => Close(clean: false);

        private void Close(bool clean)
        {
            if (HasEnded)
                return;
            HasEnded = true;
            List<UndoStep> all = Steps.ToList();
            done.Clear();
            undone.Clear();
            UndoService.Unregister(this);
            EndAll(all, clean);
            UndoService.RaiseChanged();
        }
    }

    // The kinds of file a step can hold, each in its own folder of the data folder.
    public enum HeldKind
    {
        ItemPicture, // EntityImages/: collection items' and their records' pictures
        Attachment,  // Attachments/: project files and record files (a vehicle document's scan)
        CardPicture, // CardImages/: project card pictures
        Background,  // BackgroundImages/: memory page backgrounds
        MemoryFolder // Memories/<Id>/: a deleted memory's own files, by its Id
    }

    // A file a step holds, by its stored name (compared without case, as Windows does).
    public readonly record struct HeldFile(HeldKind Kind, string Name)
    {
        public static HeldFile Of(HeldKind kind, string name) => new HeldFile(kind, Path.GetFileName(name).ToLowerInvariant());

        public static HeldFile Folder(Guid memoryId) => new HeldFile(HeldKind.MemoryFolder, memoryId.ToString("N"));

        // Stored names only: a legacy item picture (the user's own file by its full path) is never ours to delete.
        public static IEnumerable<HeldFile> All(HeldKind kind, IEnumerable<string?> names) =>
            names.Where(n => !string.IsNullOrEmpty(n) && !Path.IsPathRooted(n)).Select(n => Of(kind, n!));
    }

    // What becomes of the files a step held once it ends: each is deleted only if no other step holds it and no saved
    // data refers to it (every collection's items, every project memory's projects, the memories themselves). If any of
    // those can't be read, nothing of that kind is deleted: a file left behind is harmless, a deleted one in use isn't.
    public static class UndoFiles
    {
        public static void Clean(IEnumerable<HeldFile> files)
        {
            List<HeldFile> candidates = files.Distinct().Where(f => !UndoService.IsHeld(f)).ToList();
            if (candidates.Count == 0 || !ProfileManager.TryLoadProfiles(out List<Profile> profiles, out _))
                return;

            // A deleted memory a live step can still bring back counts as saved data: what was added to it between an undo
            // and a redo of its delete is no step's own, and must still be there when it comes back.
            List<Profile> kept = UndoService.HeldMemories().Where(id => profiles.All(p => p.Id != id))
                .SelectMany(id => new[] { new Profile { Id = id, Type = ProfileType.Collection }, new Profile { Id = id, Type = ProfileType.Project } })
                .ToList();
            bool itemsRead = TryReadItems(profiles.Concat(kept).ToList(), out List<Entity> items);
            bool projectsRead = TryReadProjects(profiles.Concat(kept).ToList(), out List<Project> projects);
            HashSet<string> Names(IEnumerable<string?> names) =>
                names.Where(n => !string.IsNullOrEmpty(n)).Select(n => Path.GetFileName(n!)).ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (IGrouping<HeldKind, HeldFile> kind in candidates.GroupBy(f => f.Kind))
            {
                HashSet<string>? used = kind.Key switch
                {
                    HeldKind.ItemPicture => itemsRead ? Names(items.SelectMany(EntityFiles.Pictures)) : null,
                    HeldKind.Attachment => itemsRead && projectsRead
                        ? Names(items.SelectMany(EntityFiles.Attachments)
                            .Concat(projects.SelectMany(p => p.Attachments ?? new List<Attachment>()).Select(a => a?.StoredFileName)))
                        : null,
                    HeldKind.CardPicture => projectsRead ? Names(projects.Select(p => p.CardBackgroundImagePath)) : null,
                    HeldKind.Background => Names(profiles.Select(p => p.BackgroundImagePath)),
                    _ => profiles.Select(p => p.Id.ToString("N")).ToHashSet(StringComparer.OrdinalIgnoreCase)
                };
                if (used == null)
                    continue;
                foreach (HeldFile file in kind.Where(f => !used.Contains(f.Name)))
                    Delete(file);
            }
        }

        // The files a memory owns, for holding while its delete can be undone: its folder, its page background, and
        // what its items or projects keep in the shared folders. `reminders`: the Ids its dated records' reminders have.
        public static List<HeldFile> OfMemory(Profile memory, out List<Guid> reminders)
        {
            List<HeldFile> files = new List<HeldFile> { HeldFile.Folder(memory.Id) };
            files.AddRange(HeldFile.All(HeldKind.Background, new[] { memory.BackgroundImagePath }));
            reminders = new List<Guid>();
            if (memory.Type == ProfileType.Collection)
            {
                List<Entity> items = EntityStore.LoadOrEmpty(memory);
                files.AddRange(items.SelectMany(OfItem));
                reminders.AddRange(items.SelectMany(EntityFiles.Records).Select(r => r.Id));
            }
            else
                files.AddRange(ProjectStore.LoadOrEmpty(memory).SelectMany(OfProject));
            return files;
        }

        public static IEnumerable<HeldFile> OfItem(Entity item) =>
            HeldFile.All(HeldKind.ItemPicture, EntityFiles.Pictures(item)).Concat(HeldFile.All(HeldKind.Attachment, EntityFiles.Attachments(item)));

        public static IEnumerable<HeldFile> OfProject(Project project) =>
            HeldFile.All(HeldKind.Attachment, (project.Attachments ?? new List<Attachment>()).Select(a => a?.StoredFileName))
                .Concat(HeldFile.All(HeldKind.CardPicture, new[] { project.CardBackgroundImagePath }));

        private static void Delete(HeldFile file)
        {
            switch (file.Kind)
            {
                case HeldKind.ItemPicture: ImageStore.Items.Delete(file.Name); break;
                case HeldKind.Attachment: AttachmentStore.Delete(new[] { new Attachment { StoredFileName = file.Name } }); break;
                case HeldKind.CardPicture: ImageStore.Cards.Delete(file.Name); break;
                case HeldKind.Background: ImageStore.Backgrounds.Delete(file.Name); break;
                case HeldKind.MemoryFolder:
                    if (Guid.TryParseExact(file.Name, "N", out Guid id))
                        MemoryFiles.DeleteFolder(id);
                    break;
            }
        }

        private static bool TryReadItems(List<Profile> profiles, out List<Entity> items)
        {
            items = new List<Entity>();
            foreach (Profile memory in profiles.Where(p => p.Type == ProfileType.Collection))
            {
                if (!EntityStore.TryLoad(memory, out List<Entity> some, out _))
                    return false;
                items.AddRange(some);
            }
            return true;
        }

        private static bool TryReadProjects(List<Profile> profiles, out List<Project> projects)
        {
            projects = new List<Project>();
            foreach (Profile memory in profiles.Where(p => p.Type == ProfileType.Project))
            {
                if (!ProjectStore.TryLoad(memory, out List<Project> some, out _))
                    return false;
                projects.AddRange(some);
            }
            return true;
        }
    }
}
