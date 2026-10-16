using System;
using System.Collections.Generic;
using System.Windows;

namespace TidyMind
{
    // What the day plan does to a block, shared by its editor, timeline and list so each asks the same question and
    // says the same thing, like ReminderActions. Each returns whether something changed, so the caller can redraw.
    public static class DayPlanActions
    {
        public const int DefaultLength = 60; // a block made from a to-do, or from Add block

        // Saves a new or edited block, then sets its reminder, or removes the one `previous` (the block as it was
        // stored, null for a new one) had. False (after saying so) if the block couldn't be saved; nothing changed then.
        public static bool Save(DayPlanBlock block, bool remind, DayPlanBlock? previous)
        {
            if (!DayPlanStore.Save(block))
            {
                WarnNotSaved(Loc.T("Plan.Block.NotSaved"));
                return false;
            }
            string? note = DayPlanReminders.Try(() =>
            {
                if (remind)
                    DayPlanReminders.Set(block);
                else if (previous != null)
                    DayPlanReminders.Remove(previous);
                return null;
            }, out bool failed);
            Tell(note, failed, Loc.T("Plan.Block.Saved"));
            return true;
        }

        // A drag on the timeline: new times, and the reminder moves along.
        public static bool Move(DayPlanBlock block, int start, int end)
        {
            if (!DayPlanStore.Change(block.Id, b => { b.StartMinute = start; b.EndMinute = end; }, out DayPlanBlock? moved))
            {
                WarnNotSaved(Loc.T("Plan.Block.NotMoved"));
                return false;
            }
            string? note = DayPlanReminders.Try(() => DayPlanReminders.Follow(moved!), out bool failed);
            Tell(note, failed, Loc.T("Plan.Block.Moved"));
            return true;
        }

        // A linked block is ticked through its to-do, which ticks every block that is time for it (DayPlanTodos).
        // `linked`: the block's to-do if it still exists.
        public static bool SetDone(DayPlanBlock block, bool done, PlanTodo? linked, IEnumerable<Profile> memories)
        {
            if (linked != null)
            {
                if (DayPlanTodos.SetDone(linked.Link, memories, done))
                    return true;
                Msg.Show(DayPlanTodos.CantChange(linked) + " " + Loc.T("Plan.Block.NotChangedEither"), Loc.T("Common.CouldntSave"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            if (DayPlanStore.Change(block.Id, b => b.Done = done, out _))
                return true;
            WarnNotSaved(Loc.T("Plan.Block.NotChanged"));
            return false;
        }

        // A block for that to-do: its text as the title, an hour from `start` (earlier if the day ends first). Null
        // (after saying so) if it couldn't be saved.
        public static DayPlanBlock? AddFromTodo(PlanTodo todo, DateTime date, int start)
        {
            start = Math.Max(0, Math.Min(start, DayPlanBlock.DayEnd - DefaultLength));
            DayPlanBlock block = new DayPlanBlock
            {
                Id = Guid.NewGuid(),
                Date = date.Date,
                StartMinute = start,
                EndMinute = start + DefaultLength,
                Title = todo.Text.Trim(),
                Todo = todo.Link
            };
            if (DayPlanStore.Save(block))
                return block;
            WarnNotSaved(Loc.T("Plan.Todo.NotAdded"));
            return null;
        }

        // The block goes, its reminder first: if that can't be removed, the block stays, so no reminder is left
        // pointing at nothing. A linked to-do is never touched.
        public static bool ConfirmDelete(DayPlanBlock block)
        {
            if (Msg.Show(Loc.F("Plan.Delete.Confirm", block.Title, block.TimeRange()), Loc.T("Common.ConfirmDelete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;

            string? problem = DayPlanReminders.Try(() =>
            {
                DayPlanReminders.Remove(block);
                return null;
            }, out _);
            if (problem != null)
            {
                Msg.Show(problem + "\n\n" + Loc.T("Plan.Block.NotDeleted"), Loc.T("Common.CouldntDelete"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return false;
            }
            if (DayPlanStore.Delete(block.Id))
                return true;
            Msg.Show(Loc.F("Plan.Delete.Failed", DayPlanStore.FilePath)
                + (block.RemindMinutesBefore != null ? "\n\n" + Loc.T("Plan.Delete.ReminderRemoved") : ""),
                Loc.T("Common.CouldntDelete"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // Where a block goes when no place was pointed at: today the next quarter hour from now, another day 09:00.
        public static int DefaultStart(DateTime date)
        {
            if (date.Date != DateTime.Today)
                return 9 * 60;
            int now = (int)DateTime.Now.TimeOfDay.TotalMinutes;
            return Math.Min((now + 14) / 15 * 15, DayPlanBlock.DayEnd - DefaultLength);
        }

        private static void WarnNotSaved(string consequence)
        {
            Msg.Show(Loc.F("Plan.SaveFailed", DayPlanStore.FilePath, consequence), Loc.T("Common.CouldntSave"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // A note about the block's reminder, after the block itself went through (lead: that sentence).
        private static void Tell(string? note, bool failed, string lead)
        {
            if (note != null)
                Msg.Show(lead + " " + note, Loc.T("Plan.Mark.Reminder"), MessageBoxButton.OK,
                    failed ? MessageBoxImage.Warning : MessageBoxImage.Information);
        }
    }
}
