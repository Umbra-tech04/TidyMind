using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TidyMind
{
    // The editor for one day plan block: title, times, color, notes, its to-do, done, and an optional reminder. Saving
    // and deleting go through DayPlanActions, like the timeline's and the list's own actions.
    public partial class DayPlanBlockDialog : Window
    {
        public enum Outcome { None, Saved, Deleted }

        private readonly DayPlanBlock? editing; // null when adding
        private readonly DateTime date;
        private readonly IEnumerable<Profile> memories;
        private readonly PlanTodo? linked;       // the block's to-do, if it still exists
        private readonly SwatchRow swatches;
        private readonly string opened;
        private Outcome outcome;
        private bool todoChanged;

        // Fixed for this window: saving again after a failure updates the same block rather than adding a second.
        private readonly Guid newId = Guid.NewGuid();

        // choices: the to-dos it could be linked to (those not yet on this day's plan).
        private DayPlanBlockDialog(DayPlanBlock? existing, DateTime date, int start, int end, IEnumerable<Profile> memories,
            List<PlanTodo> choices, PlanTodo? linked, bool reminderSet)
        {
            InitializeComponent();
            editing = existing;
            this.date = date.Date;
            this.memories = memories;
            this.linked = linked;

            Title = Loc.T(existing == null ? "Plan.Dialog.NewWindow" : "Plan.Dialog.EditWindow");
            HeadingText.Text = Loc.T(existing == null ? "Plan.Dialog.New" : "Plan.Dialog.Edit");
            SubheadingText.Text = this.date.ToString(Loc.T("Format.FullDate"), Loc.Culture)
                + (this.date == DateTime.Today ? "  ·  " + Loc.T("Common.Today") : "");
            DeleteButton.Visibility = existing == null ? Visibility.Collapsed : Visibility.Visible;

            TitleInput.Text = existing?.Title ?? "";
            NotesInput.Text = existing?.Notes ?? "";
            DayPicker.SelectedDate = this.date;
            ReminderForm.FillTimeBoxes(StartHourBox, StartMinuteBox, this.date.AddMinutes(start));
            ReminderForm.FillTimeBoxes(EndHourBox, EndMinuteBox, this.date.AddMinutes(end % DayPlanBlock.DayEnd));
            EndHourBox.Items.Add(new ComboBoxItem { Content = "24" }); // a block may end at midnight
            if (end == DayPlanBlock.DayEnd)
                EndHourBox.SelectedIndex = 24;
            swatches = new SwatchRow(SwatchPanel, existing?.ColorHex);

            TodoBox.Items.Add(new ComboBoxItem { Content = Loc.T("Plan.Dialog.NotLinked") });
            foreach (PlanTodo todo in (linked == null ? choices : choices.Prepend(linked)))
                TodoBox.Items.Add(new ComboBoxItem { Content = todo.Text + "  ·  " + todo.Where, Tag = todo });
            TodoBox.SelectedIndex = linked == null ? 0 : 1;
            todoChanged = false;

            DoneCheck.IsChecked = linked?.Done ?? existing?.Done ?? false;

            foreach (int lead in DayPlanReminders.Leads)
                LeadBox.Items.Add(new ComboBoxItem { Content = DayPlanReminders.LeadText(lead) });
            LeadBox.SelectedIndex = Math.Max(0, Array.IndexOf(DayPlanReminders.Leads, existing?.RemindMinutesBefore ?? 0));
            RemindCheck.IsChecked = reminderSet;
            LeadBox.IsEnabled = reminderSet;

            opened = CurrentInput();
            Loaded += (s, e) =>
            {
                TitleInput.Focus();
                TitleInput.CaretIndex = TitleInput.Text.Length;
            };
        }

        // existing null: a new block from `start` to `end` (minutes). What happened comes back, so the view redraws.
        public static Outcome Show(DependencyObject owner, DayPlanBlock? existing, DateTime date, int start, int end,
            IEnumerable<Profile> memories, List<PlanTodo> choices, PlanTodo? linked, bool reminderSet)
        {
            DayPlanBlockDialog dialog = new DayPlanBlockDialog(existing, date, start, end, memories, choices, linked, reminderSet);
            Window ownerWindow = owner as Window ?? Window.GetWindow(owner);
            if (ownerWindow != null)
                dialog.Owner = ownerWindow;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.ShowDialog();
            return dialog.outcome;
        }

        private string CurrentInput() => string.Join("\n", TitleInput.Text, NotesInput.Text, swatches.Selected, DayPicker.SelectedDate,
            StartHourBox.SelectedIndex, StartMinuteBox.SelectedIndex, EndHourBox.SelectedIndex, EndMinuteBox.SelectedIndex,
            TodoBox.SelectedIndex, DoneCheck.IsChecked, RemindCheck.IsChecked, LeadBox.SelectedIndex);

        // Closing without saving (Esc, Cancel, X) asks first when something was changed; Save and Delete don't.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (DialogResult != true && CurrentInput() != opened
                && Msg.Show(this, Loc.T("Common.DiscardChanges"), Loc.T("Common.UnsavedChanges"), MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;

            base.OnClosing(e);
        }

        private void TitleInput_TextChanged(object sender, TextChangedEventArgs e) =>
            TitlePlaceholder.Visibility = TitleInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void NotesInput_TextChanged(object sender, TextChangedEventArgs e) =>
            NotesPlaceholder.Visibility = NotesInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        // A picked to-do brings its done state (linking alone never ticks it) and, for a block without a title yet,
        // its text.
        private void TodoBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            todoChanged = true;
            if (!(Picked() is PlanTodo todo))
                return;
            DoneCheck.IsChecked = todo.Done;
            if (TitleInput.Text.Trim().Length == 0)
                TitleInput.Text = todo.Text;
        }

        private PlanTodo? Picked() => (TodoBox.SelectedItem as ComboBoxItem)?.Tag as PlanTodo;

        private void RemindCheck_Changed(object sender, RoutedEventArgs e) => LeadBox.IsEnabled = RemindCheck.IsChecked == true;

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            string title = TitleInput.Text.Trim();
            if (title.Length == 0)
            {
                Warn(Loc.T("Plan.Problem.TitleCaption"), Loc.T("Plan.Problem.Title"));
                return;
            }

            if (DayPicker.SelectedDate == null)
            {
                Warn(Loc.T("Plan.Problem.DayCaption"), Loc.T("Plan.Problem.Day"));
                return;
            }

            int start = StartHourBox.SelectedIndex * 60 + StartMinuteBox.SelectedIndex;
            int end = EndHourBox.SelectedIndex * 60 + EndMinuteBox.SelectedIndex;
            string? problem = DayPlanBlock.CheckTimes(start, end);
            if (problem != null)
            {
                Warn(Loc.T("Plan.Problem.TimesCaption"), problem);
                return;
            }

            bool remind = RemindCheck.IsChecked == true;
            string notes = NotesInput.Text.Trim();
            DayPlanBlock block = new DayPlanBlock
            {
                Id = editing?.Id ?? newId,
                Date = DayPicker.SelectedDate.Value.Date, // another day moves the block (and its reminder) there
                StartMinute = start,
                EndMinute = end,
                Title = title,
                Notes = notes.Length == 0 ? null : notes,
                ColorHex = swatches.Selected,
                Done = DoneCheck.IsChecked == true,
                // Left as it was unless another to-do (or none) was picked: a deleted to-do's link stays on the block.
                Todo = todoChanged ? Picked()?.Link : editing?.Todo,
                RemindMinutesBefore = remind ? DayPlanReminders.Leads[LeadBox.SelectedIndex] : null,
                ExtensionData = editing?.ExtensionData
            };

            if (remind && DayPlanReminders.FireTime(block) <= DateTime.Now)
            {
                Warn(Loc.T("Reminder.Problem.PastTitle"), Loc.T("Plan.Problem.ReminderPast"));
                return;
            }

            // The to-do is ticked with the block (and ticks its other blocks too); if that fails, nothing is saved.
            PlanTodo? todo = todoChanged ? Picked() : linked;
            if (todo != null && todo.Done != block.Done && !DayPlanTodos.SetDone(todo.Link, memories, block.Done))
            {
                Warn(Loc.T("Common.CouldntSave"), DayPlanTodos.CantChange(todo) + " " + Loc.T("Plan.Block.NotSaved"));
                return;
            }

            if (!DayPlanActions.Save(block, remind, editing))
                return; // said so; the window stays open, so nothing typed is lost
            outcome = Outcome.Saved;
            DialogResult = true;
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (editing == null || !DayPlanActions.ConfirmDelete(editing))
                return;
            outcome = Outcome.Deleted;
            DialogResult = true;
        }

        private void Warn(string title, string text) =>
            Msg.Show(this, text, title, MessageBoxButton.OK, MessageBoxImage.Warning);

        // Ctrl+Enter saves from anywhere (Enter is a new line in the notes).
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Save();
                e.Handled = true;
            }
        }
    }
}
