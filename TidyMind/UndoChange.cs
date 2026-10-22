using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace TidyMind
{
    // Records one undoable change to the stored data around the code that makes it:
    //
    //     using UndoChange change = UndoChange.Begin(UndoService.Page, UndoParts.Entities(memory));
    //     ... the change as it always was: change, save, clean up ...
    //     change.Commit(label, destructive: true);
    //
    // Begin reads the files involved (the parts), Commit reads them again and keeps what differs as one step
    // (StoredStep): only the items that changed, before and after, and the order of the rest. Nothing changed (asked
    // and cancelled, the save failed): no step. Meanwhile every file the parts' items name is held, so the change's own
    // deletes (EntityFiles, AttachmentStore) leave them for the step; once it ends, UndoFiles decides.
    public sealed class UndoChange : IDisposable
    {
        private readonly UndoHistory? history;
        private readonly UndoPart[] parts;
        private readonly HashSet<HeldFile> held = new HashSet<HeldFile>();
        private readonly List<HeldFile> extra = new List<HeldFile>();
        private readonly HashSet<Guid> reminders = new HashSet<Guid>();
        private readonly bool readable;
        private bool finished;

        private UndoChange(UndoHistory? history, UndoPart[] parts)
        {
            this.history = UndoService.Closed ? null : history;
            history = this.history;
            this.parts = parts;
            if (history == null)
                return;
            List<HeldFile> files = new List<HeldFile>();
            readable = parts.All(p => p.CaptureBefore(files));
            held.UnionWith(files);
            UndoService.HoldWhileRecording(held);
        }

        // history: where the step goes (null, or the app closing: nothing is recorded, the change just runs).
        public static UndoChange Begin(UndoHistory? history, params UndoPart[] parts) => new UndoChange(history, parts);

        // Files the parts don't name but the change deletes (a deleted memory's folder and what its items own): held like
        // theirs, and by the step.
        public void Hold(IEnumerable<HeldFile> files)
        {
            List<HeldFile> more = files.ToList();
            extra.AddRange(more);
            held.UnionWith(more);
        }

        // Reminders the parts don't name but the change removes (a deleted memory's vehicles' dates).
        public void WatchReminders(IEnumerable<Guid> ids) => reminders.UnionWith(ids);

        // Run after each undo (true) or redo (false) of the step, once its files are written: what the parts can't know
        // (a deleted memory changed between an undo and a redo). Returns what to tell, or null.
        public Func<StoredStep, bool, string?>? AfterMove { get; set; }

        // The step, or null if nothing changed (or it couldn't be recorded). destructive: a delete, said in a toast with
        // Undo. target: the collection item it changed.
        public UndoStep? Commit(string label, bool destructive = false, Guid? target = null)
        {
            if (finished || history == null)
                return null;
            finished = true;
            try
            {
                bool recorded = readable && parts.All(p => p.CaptureAfter());
                if (!recorded || !parts.Any(p => p.Primary && p.Changed))
                {
                    if (!recorded || parts.Any(p => p.Changed))
                        Unrecorded();
                    return null;
                }

                HashSet<Guid> watched = new HashSet<Guid>(reminders);
                foreach (UndoPart part in parts)
                    watched.UnionWith(part.ChangedReminderIds());
                foreach (ReminderPart part in parts.OfType<ReminderPart>())
                    part.Narrow(watched);

                List<UndoPart> changed = parts.Where(p => p.Changed).ToList();
                HashSet<HeldFile> files = new HashSet<HeldFile>(extra);
                foreach (UndoPart part in changed)
                    files.UnionWith(part.ChangedFiles());
                StoredStep step = new StoredStep(label, destructive, changed, files) { Target = target, After = AfterMove };
                history.Push(step); // before the change's own hold goes: no moment without one
                if (destructive)
                    UndoHost.Announce(step);
                return step;
            }
            finally
            {
                UndoService.Release(held);
            }
        }

        // Not committed (the change gave up, or threw): if it changed anything after all, that can't be undone, and the
        // files it would have deleted go now, as they would have without undo.
        public void Dispose()
        {
            if (finished || history == null)
                return;
            finished = true;
            if (readable && parts.All(p => p.CaptureAfter()) && !parts.Any(p => p.Changed))
            {
                UndoService.Release(held);
                return;
            }
            Unrecorded();
        }

        private void Unrecorded()
        {
            UndoService.Release(held);
            UndoFiles.Clean(held);
        }
    }

    // One stored list a change can touch, read whole and written whole through its store.
    public abstract class UndoPart
    {
        // The file, named when it can't be read or written.
        internal abstract string File { get; }

        // A change counts only if a primary part changed (a to-do ticked, not just the plan blocks that follow it).
        internal bool Primary { get; set; } = true;

        internal abstract bool CaptureBefore(List<HeldFile> files);

        internal abstract bool CaptureAfter();

        internal abstract bool Changed { get; }

        internal virtual IEnumerable<HeldFile> ChangedFiles() => Enumerable.Empty<HeldFile>();

        internal virtual IEnumerable<Guid> ChangedReminderIds() => Enumerable.Empty<Guid>();

        // Reads the file as it is now and works out what it becomes (to the state before the change, or after it). Null
        // if it can; else why not, `conflict` when what the change touched was changed again since.
        internal abstract string? Prepare(bool toBefore, out bool conflict);

        internal abstract bool Write();

        // Puts back what Write replaced (another part of the same step couldn't be written).
        internal abstract void Restore();
    }

    // A list of items with a key each (an Id; a calendar day's date). Only the items that differ are kept, as JSON,
    // before and after, with the order of every key. Going back (or forward) reads the list fresh, checks that every
    // changed item is still as the change left it, puts the other state's items back after the item that came before
    // them there, and saves through the store. An item changed in place gets back only the properties the change
    // touched: one changed elsewhere since (a folded to-do panel, another field) doesn't stand in the way.
    public sealed class ListPart<T> : UndoPart where T : class
    {
        private readonly string file;
        private readonly Func<List<T>?> load;
        private readonly Func<List<T>, bool> save;
        private readonly Func<T, string> key;
        private readonly Func<T, IEnumerable<HeldFile>>? files;
        private readonly Func<T, IEnumerable<Guid>>? reminderIds;

        private Dictionary<string, string>? before;
        private List<string> beforeKeys = new List<string>(), afterKeys = new List<string>();
        private readonly Dictionary<string, (string? Before, string? After)> changes = new Dictionary<string, (string?, string?)>();
        private List<T>? current, target;

        // file: named in messages. load: null if it can't be read. save: false if it couldn't be written.
        public ListPart(string file, Func<List<T>?> load, Func<List<T>, bool> save, Func<T, string> key,
            Func<T, IEnumerable<HeldFile>>? files = null, Func<T, IEnumerable<Guid>>? reminderIds = null)
        {
            this.file = file;
            this.load = load;
            this.save = save;
            this.key = key;
            this.files = files;
            this.reminderIds = reminderIds;
        }

        // A part whose change alone isn't one (the plan blocks a to-do's tick takes along).
        public ListPart<T> Secondary()
        {
            Primary = false;
            return this;
        }

        internal override string File => file;

        internal override bool Changed => changes.Count > 0;

        internal override bool CaptureBefore(List<HeldFile> held)
        {
            List<T>? items = load();
            if (items == null || !Keyed(items, out beforeKeys, out before))
                return false;
            if (files != null)
                held.AddRange(items.SelectMany(files));
            return true;
        }

        internal override bool CaptureAfter()
        {
            if (before == null)
                return false;
            List<T>? items = load();
            if (items == null || !Keyed(items, out afterKeys, out Dictionary<string, string> after))
                return false;

            foreach (string k in before.Keys.Union(after.Keys))
            {
                before.TryGetValue(k, out string? was);
                after.TryGetValue(k, out string? now);
                if (was != now)
                    changes[k] = (was, now);
            }
            // Items left as they were but in another order (a drag): their places are part of the change.
            List<string> keptBefore = beforeKeys.Where(k => after.ContainsKey(k) && !changes.ContainsKey(k)).ToList();
            List<string> keptAfter = afterKeys.Where(k => before.ContainsKey(k) && !changes.ContainsKey(k)).ToList();
            if (!keptBefore.SequenceEqual(keptAfter))
                foreach (string k in keptBefore)
                    changes[k] = (before[k], after[k]);
            before = null;
            return true;
        }

        internal override IEnumerable<HeldFile> ChangedFiles() =>
            files == null ? Enumerable.Empty<HeldFile>() : ChangedItems().SelectMany(files);

        internal override IEnumerable<Guid> ChangedReminderIds() =>
            reminderIds == null ? Enumerable.Empty<Guid>() : ChangedItems().SelectMany(reminderIds);

        private IEnumerable<T> ChangedItems() =>
            changes.Values.SelectMany(c => new[] { c.Before, c.After }).Where(j => j != null).Select(j => JsonSerializer.Deserialize<T>(j!)!);

        internal override string? Prepare(bool toBefore, out bool conflict)
        {
            conflict = false;
            current = load();
            if (current == null || !Keyed(current, out _, out Dictionary<string, string> now))
                return Loc.F("Undo.FileProblem", file);

            Dictionary<string, T> back = new Dictionary<string, T>();
            foreach ((string k, (string? was, string? became)) in changes)
            {
                string? from = toBefore ? became : was, to = toBefore ? was : became;
                now.TryGetValue(k, out string? present);
                if (from == null)
                {
                    // It wasn't there: it mustn't be now (it would come back twice).
                    if (present != null)
                        return Conflict(out conflict);
                }
                else if (present == null || (to == null && present != from))
                    return Conflict(out conflict); // gone since, or changed since and about to be taken out
                if (to == null)
                    continue;
                string? patched = from == null ? to : Patch(present!, from, to);
                if (patched == null)
                    return Conflict(out conflict);
                back[k] = JsonSerializer.Deserialize<T>(patched)!;
            }

            // The items the change left alone stay where they are now; each of the others goes back after the item that
            // came before it in the other state (or first).
            List<string> order = toBefore ? beforeKeys : afterKeys;
            List<T> result = current.Where(i => !changes.ContainsKey(key(i))).ToList();
            List<string> keys = result.Select(key).ToList();
            for (int i = 0; i < order.Count; i++)
            {
                if (!back.TryGetValue(order[i], out T? item))
                    continue;
                int at = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int found = keys.IndexOf(order[j]);
                    if (found >= 0)
                    {
                        at = found + 1;
                        break;
                    }
                }
                result.Insert(at, item);
                keys.Insert(at, order[i]);
            }
            target = result;
            return null;
        }

        private static string Conflict(out bool conflict)
        {
            conflict = true;
            return "conflict";
        }

        internal override bool Write() => save(target!);

        internal override void Restore() => save(current!);

        // Every item by its key, as the store writes it. False if two share a key: such a list isn't safe to piece back.
        private bool Keyed(List<T> items, out List<string> keys, out Dictionary<string, string> json)
        {
            keys = new List<string>(items.Count);
            json = new Dictionary<string, string>(items.Count);
            foreach (T item in items)
            {
                string k = key(item);
                if (json.ContainsKey(k))
                    return false;
                keys.Add(k);
                json[k] = JsonSerializer.Serialize(item);
            }
            return true;
        }

        // The item as it is now with the top-level properties that differ between `from` and `to` set as in `to`; null if
        // one of them isn't as in `from` any more.
        private static string? Patch(string present, string from, string to)
        {
            if (present == from)
                return to;
            if (!(JsonNode.Parse(present) is JsonObject now) || !(JsonNode.Parse(from) is JsonObject was) || !(JsonNode.Parse(to) is JsonObject will))
                return null;
            foreach (string name in was.Select(p => p.Key).Union(will.Select(p => p.Key)).ToList())
            {
                bool inFrom = was.TryGetPropertyValue(name, out JsonNode? f);
                bool inTo = will.TryGetPropertyValue(name, out JsonNode? t);
                if (inFrom == inTo && JsonNode.DeepEquals(f, t))
                    continue;
                bool inNow = now.TryGetPropertyValue(name, out JsonNode? n);
                if (inNow != inFrom || !JsonNode.DeepEquals(n, f))
                    return null;
                if (inTo)
                    now[name] = t?.DeepClone();
                else
                    now.Remove(name);
            }
            return now.ToJsonString();
        }
    }

    // reminders.json, for the reminders a change touched (a deleted reminder, a vehicle record's date, a plan block's).
    // Each goes back only if it is still as the change left it, checked and changed under the reminders' own lock
    // (ReminderManager.SetIf: the entry and its Task Scheduler task, by its Id, never two). A one-off that fired meanwhile,
    // or one changed since, is left alone, so nothing is shown twice; one whose time has passed is scheduled for its next
    // time, never in the past. As the Reminders window's own change (primary) it all goes through or none does; along with
    // other data it is the last thing done, and a reminder that couldn't be set back is said (Undo.RemindersNote).
    public sealed class ReminderPart : UndoPart
    {
        private const int ReadAttempts = 5;

        private sealed class Planned
        {
            public Guid Id;
            public string? From, To, Written;
            public (string? Before, string? After) Original; // the change as recorded, before a moved-on time replaced it
            public bool Done;
        }

        private Dictionary<Guid, string>? before;
        private readonly Dictionary<Guid, (string? Before, string? After)> changes = new Dictionary<Guid, (string?, string?)>();
        private readonly HashSet<Guid>? only;
        private List<Planned> plan = new List<Planned>();
        private bool toBefore;
        private bool unknown;  // reminders.json couldn't be read while the change was recorded: its reminders aren't known
        private bool skipped;  // a reminder changed since was left alone

        // ids: the reminders the change is about (the Reminders window: the ones deleted); null: those of the other parts.
        public ReminderPart(bool primary, IEnumerable<Guid>? ids = null)
        {
            Primary = primary;
            only = ids?.ToHashSet();
        }

        internal override string File => "reminders.json";

        // Unknown counts as changed: the undo then says that the reminders may not be as they were.
        internal override bool Changed => changes.Count > 0 || unknown;

        // A reminders.json that can't be read doesn't keep the rest of the change from being undone.
        internal override bool CaptureBefore(List<HeldFile> files)
        {
            unknown = !TryRead(out before);
            return true;
        }

        internal override bool CaptureAfter()
        {
            if (unknown)
                return true;
            if (!TryRead(out Dictionary<Guid, string>? after))
            {
                unknown = true;
                return true;
            }
            foreach (Guid id in before!.Keys.Union(after!.Keys).Where(id => only == null || only.Contains(id)))
            {
                before.TryGetValue(id, out string? was);
                after.TryGetValue(id, out string? now);
                if (was != now)
                    changes[id] = (was, now);
            }
            before = null;
            return true;
        }

        // Only these Ids count (the other parts' records, a deleted memory's): another reminder that fired meanwhile isn't
        // part of the change.
        internal void Narrow(ISet<Guid> watched)
        {
            if (only != null)
                return;
            foreach (Guid id in changes.Keys.Where(id => !watched.Contains(id)).ToList())
                changes.Remove(id);
        }

        internal override string? Prepare(bool toBefore, out bool conflict)
        {
            conflict = false;
            this.toBefore = toBefore;
            plan = new List<Planned>();
            skipped = unknown;
            if (unknown)
                return null;
            if (!TryRead(out Dictionary<Guid, string>? now))
                return Loc.F("Undo.FileProblem", File);
            foreach ((Guid id, (string? was, string? became)) in changes)
            {
                string? from = toBefore ? became : was, to = toBefore ? was : became;
                now!.TryGetValue(id, out string? present);
                if (present == from)
                    plan.Add(new Planned { Id = id, From = from, To = to, Original = (was, became) });
                else if (Primary)
                    return Conflict(out conflict);
                else
                    skipped = true;
            }
            return null;
        }

        // False if a reminder couldn't be set back (or was left alone): along with data that is said; as the window's own
        // change (Primary), what was done is put back first.
        internal override bool Write()
        {
            foreach (Planned step in plan)
            {
                if (!Set(step.Id, step.From, step.To, out string? written))
                {
                    if (!Primary)
                    {
                        skipped = true;
                        continue;
                    }
                    Restore();
                    return false;
                }
                step.Written = written;
                step.Done = true;
                // What is stored now is what the other way starts from (a passed time was moved on).
                changes[step.Id] = toBefore ? (written, changes[step.Id].After) : (changes[step.Id].Before, written);
            }
            return !skipped;
        }

        internal override void Restore()
        {
            foreach (Planned step in Enumerable.Reverse(plan).Where(s => s.Done))
            {
                Set(step.Id, step.Written, step.From, out _);
                step.Done = false;
                changes[step.Id] = step.Original;
            }
        }

        // The reminder made `to` if it is still `from`; `written`: as stored then (null if deleted). False if it wasn't as
        // expected any more, or couldn't be changed.
        private static bool Set(Guid id, string? from, string? to, out string? written)
        {
            written = null;
            try
            {
                Reminder? stored = ReminderManager.SetIf(id, r => (r == null ? null : JsonSerializer.Serialize(r)) == from,
                    to == null ? null : JsonSerializer.Deserialize<Reminder>(to), out bool notAsExpected);
                if (notAsExpected)
                    return false;
                written = stored == null ? null : JsonSerializer.Serialize(stored);
                return true;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException
                                      || e is System.Runtime.InteropServices.COMException)
            {
                UnhandledErrors.LogQuietly("undo", e);
                return false;
            }
        }

        private static string Conflict(out bool conflict)
        {
            conflict = true;
            return "conflict";
        }

        // Read again a few times: the reminder process replaces the file now and then, and a read at that moment fails.
        private static bool TryRead(out Dictionary<Guid, string>? reminders)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    reminders = ReminderManager.LoadReminders().GroupBy(r => r.Id).ToDictionary(g => g.Key, g => JsonSerializer.Serialize(g.First()));
                    return true;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
                {
                    if (attempt >= ReadAttempts || e is JsonException)
                    {
                        reminders = null;
                        return false;
                    }
                    System.Threading.Thread.Sleep(100);
                }
            }
        }
    }


    // A recorded change: its parts, and the files it holds (every file its items name before and after).
    public sealed class StoredStep : UndoStep
    {
        private readonly List<UndoPart> parts;
        private readonly HashSet<HeldFile> held;
        private string? note;

        internal StoredStep(string label, bool destructive, List<UndoPart> parts, HashSet<HeldFile> held) : base(label, destructive)
        {
            this.parts = parts;
            this.held = held;
        }

        internal Func<StoredStep, bool, string?>? After { get; init; }

        internal override IReadOnlyCollection<HeldFile> Held => held;

        // More files to keep while the step lives (what a deleted memory gained between an undo and a redo).
        public void Hold(IEnumerable<HeldFile> files) => held.UnionWith(files);

        internal override string? Revert(out bool conflict) => Move(toBefore: true, out conflict);

        internal override string? Reapply(out bool conflict) => Move(toBefore: false, out conflict);

        internal override string? TakeNote()
        {
            string? said = note;
            note = null;
            return said;
        }

        // Every part checked first, then written one after the other; if one can't be written, the ones before it are
        // put back, so nothing changed. The reminders that go along come last.
        private string? Move(bool toBefore, out bool conflict)
        {
            conflict = false;
            note = null;
            List<UndoPart> data = parts.Where(p => p.Primary || !(p is ReminderPart)).ToList();
            List<ReminderPart> along = parts.OfType<ReminderPart>().Where(p => !p.Primary).ToList();
            foreach (UndoPart part in data.Concat(along))
            {
                string? problem = part.Prepare(toBefore, out conflict);
                if (problem != null)
                    return problem;
            }

            List<UndoPart> written = new List<UndoPart>();
            foreach (UndoPart part in data)
            {
                if (!part.Write())
                {
                    foreach (UndoPart back in Enumerable.Reverse(written))
                        back.Restore();
                    return Loc.F("Undo.FileProblem", part.File);
                }
                written.Add(part);
            }
            foreach (ReminderPart reminders in along)
                if (!reminders.Write())
                    note = Loc.T("Undo.RemindersNote");
            if (After?.Invoke(this, toBefore) is string said)
                note = said;
            return null;
        }
    }

    // The parts for each stored list, as its store reads and writes it.
    public static class UndoParts
    {
        public static ListPart<Entity> Entities(Profile memory) => new ListPart<Entity>(MemoryFiles.Items(memory),
            () => EntityStore.TryLoad(memory, out List<Entity> items, out _) ? items : null,
            items => EntityStore.TrySave(memory, items), e => e.Id.ToString(),
            UndoFiles.OfItem, e => EntityFiles.Records(e).Select(r => r.Id));

        public static ListPart<Project> Projects(Profile memory) => new ListPart<Project>(MemoryFiles.Items(memory),
            () => ProjectStore.TryLoad(memory, out List<Project> projects, out _) ? projects : null,
            projects => ProjectStore.TrySave(memory, projects), p => p.Id.ToString(), UndoFiles.OfProject);

        public static ListPart<MemoryTab> Tabs(Profile memory) => new ListPart<MemoryTab>(MemoryFiles.Tabs(memory),
            () => TabStore.TryRead(memory, out List<MemoryTab> tabs) ? tabs : null,
            tabs => TabStore.TrySave(memory, tabs), t => t.Id.ToString());

        public static ListPart<TodoEntry> Todos(Profile memory) => new ListPart<TodoEntry>(MemoryFiles.Todos(memory),
            () => MemoryTodoService.TryLoad(memory, out List<TodoEntry> todos) ? todos : null,
            todos => MemoryTodoService.TrySave(memory, todos), t => t.Id.ToString());

        // A memory's notebook as one item (no file: none), so a change to it goes back whole.
        public static ListPart<MemoryNotebook> Notebook(Profile memory) => new ListPart<MemoryNotebook>(MemoryFiles.Notebook(memory),
            () => !MemoryNotebookStore.TryLoad(memory, out MemoryNotebook notebook, out _) ? null
                : File.Exists(MemoryFiles.Notebook(memory)) ? new List<MemoryNotebook> { notebook } : new List<MemoryNotebook>(),
            list => MemoryNotebookStore.TrySave(memory, list.Count > 0 ? list[0] : new MemoryNotebook()), n => "notebook");

        // A custom collection's layout as one item (no file: not custom), so a change to it goes back whole.
        public static ListPart<CustomLayout> Layout(Profile memory) => new ListPart<CustomLayout>(MemoryFiles.Layout(memory),
            () => !CustomLayoutStore.TryLoad(memory, out CustomLayout? layout, out _) ? null
                : layout == null ? new List<CustomLayout>() : new List<CustomLayout> { layout },
            list => list.Count > 0 ? CustomLayoutStore.TrySave(memory, list[0]) : CustomLayoutStore.TryDelete(memory.Id), l => "layout");

        public static ListPart<QuickTodo> QuickTodos() => new ListPart<QuickTodo>(QuickTodoService.FilePath,
            () => QuickTodoService.TryRead(out List<QuickTodo> todos) ? todos : null,
            QuickTodoService.TryWrite, t => t.Id.ToString());

        // The blocks of dayplan.json; the rest of the file (fields of other versions) as it is when written.
        public static ListPart<DayPlanBlock> DayPlan()
        {
            DayPlanFile? file = null;
            return new ListPart<DayPlanBlock>(DayPlanStore.FilePath,
                () => DayPlanStore.TryLoad(out file) ? file.Blocks : null,
                blocks =>
                {
                    file!.Blocks = blocks;
                    return DayPlanStore.TryWrite(file);
                },
                b => b.Id.ToString(), reminderIds: b => new[] { b.Id });
        }

        public static ListPart<CalendarDay> Calendar() => new ListPart<CalendarDay>(CalendarService.FilePath,
            () => CalendarService.TryRead(out List<CalendarDay> days) ? days : null,
            CalendarService.TryWrite, d => d.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        public static ListPart<Profile> Profiles() => new ListPart<Profile>(AppPaths.Data("profiles.json"),
            () => ProfileManager.TryLoadProfiles(out List<Profile> profiles, out _) ? profiles : null,
            ProfileManager.TrySaveProfiles, p => p.Id.ToString(),
            p => HeldFile.All(HeldKind.Background, new[] { p.BackgroundImagePath }));

        // Along with other data: the reminders of their changed items. Primary (the Reminders window): `ids` themselves.
        public static ReminderPart Reminders(IEnumerable<Guid>? ids = null) => new ReminderPart(ids != null, ids);
    }
}
