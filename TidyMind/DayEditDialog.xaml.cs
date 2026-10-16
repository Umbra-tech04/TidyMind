using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace TidyMind
{
    // The colour + note (title and what to do) editor for one calendar day. Only ever writes calendar.json:
    // reminders are a separate thing, added from a day's right-click menu (in the calendar or on Home).
    public partial class DayEditDialog : Window
    {
        private readonly DateTime date;
        private readonly SwatchRow swatches;

        private DayEditDialog(DateTime date)
        {
            InitializeComponent();
            this.date = date.Date;

            // A note from before titles existed opens split: its first line as the title, the rest as the note.
            CalendarDay day = CalendarService.GetDay(CalendarService.LoadDays(), this.date);
            TitleInput.Text = day?.Heading() ?? "";
            NoteInput.Text = day?.Body() ?? "";
            ClearButton.Visibility = day != null ? Visibility.Visible : Visibility.Collapsed;

            Title = this.date.ToString(Loc.T("Format.LongDate"), Loc.Culture);
            HeadingText.Text = this.date.ToString(Loc.T("Format.WeekdayDayMonth"), Loc.Culture);
            SubheadingText.Text = this.date.Year.ToString(CultureInfo.InvariantCulture)
                + (this.date == DateTime.Today ? "  ·  " + Loc.T("Common.Today") : "");
            DayPlanStore.TryLoad(out DayPlanFile plan);
            int planned = plan.Blocks.Count(b => b.Date.Date == this.date);
            OpenPlanButton.Content = planned == 0 ? Loc.T("Day.PlanThisDay") : Loc.Plural("Day.OpenPlan", planned);

            swatches = new SwatchRow(SwatchPanel, day?.ColorHex);
            opened = CurrentInput();

            Loaded += (s, e) =>
            {
                TitleInput.Focus();
                TitleInput.CaretIndex = TitleInput.Text.Length;
            };
        }

        // What the dialog showed when it opened, to tell whether Esc, Cancel or X would throw anything away.
        private readonly string opened;

        private string CurrentInput() => swatches.Selected + "\n" + TitleInput.Text + "\n" + NoteInput.Text;

        // Closing without saving (Esc, Cancel, X) asks first when something was changed; Save and Clear don't.
        protected override void OnClosing(CancelEventArgs e)
        {
            if (DialogResult != true && CurrentInput() != opened
                && Msg.Show(this, Loc.T("Common.DiscardChanges"), Loc.T("Common.UnsavedChanges"), MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;

            base.OnClosing(e);
        }

        // Returns true if the day was saved or cleared.
        public static bool Edit(DependencyObject owner, DateTime date)
        {
            DayEditDialog dialog = new DayEditDialog(date);
            Window ownerWindow = owner as Window ?? Window.GetWindow(owner);
            if (ownerWindow != null)
                dialog.Owner = ownerWindow;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            bool saved = dialog.ShowDialog() == true;
            if (dialog.openPlan)
                ((MainWindow)Application.Current.MainWindow).ShowDayPlan(date);
            return saved;
        }

        private bool openPlan;

        // Closes like Cancel (asking first if something was changed), then the day's plan opens.
        private void OpenPlanButton_Click(object sender, RoutedEventArgs e)
        {
            openPlan = true;
            Close();
            if (IsVisible)
                openPlan = false; // "Discard changes?" was answered No: stay here
        }

        // ---- Title and note -----------------------------------------------

        // Enter in the title moves on to the note (where Enter is a new line); Ctrl+Enter saves from either.
        private void TitleInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                NoteInput.Focus();
                NoteInput.CaretIndex = NoteInput.Text.Length;
                e.Handled = true;
            }
        }

        private void TitleInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            TitlePlaceholder.Visibility = TitleInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NoteInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            NotePlaceholder.Visibility = NoteInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e) => Save();

        private void Save()
        {
            if (CalendarService.SaveDay(date, swatches.Selected, TitleInput.Text, NoteInput.Text))
                DialogResult = true;
            else
                ShowSaveFailed();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (CalendarService.SaveDay(date, null, null, null))
                DialogResult = true;
            else
                ShowSaveFailed();
        }

        // The dialog stays open, so the typed note isn't lost and saving can be tried again.
        private void ShowSaveFailed()
        {
            Msg.Show(this, Loc.T("Day.SaveFailed"), Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Enter belongs to the note (new line); Ctrl+Enter saves.
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
