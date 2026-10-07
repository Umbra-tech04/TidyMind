using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TidyMind
{
    // The colour + note (title and what to do) editor for one calendar day. Only ever writes calendar.json:
    // reminders are a separate thing, added from a day's right-click menu (in the calendar or on Home).
    public partial class DayEditDialog : Window
    {
        public static readonly string[] Palette =
        {
            "#2E7BC4", "#27AE60", "#1ABC9C", "#F2C94C", "#F2994A", "#EB5757", "#E86FB0", "#9B51E0", "#8A8A87"
        };

        private readonly DateTime date;
        private string selectedColor;
        private readonly List<KeyValuePair<Border, string>> swatches = new List<KeyValuePair<Border, string>>();

        private DayEditDialog(DateTime date)
        {
            InitializeComponent();
            this.date = date.Date;

            // A note from before titles existed opens split: its first line as the title, the rest as the note.
            CalendarDay day = CalendarService.GetDay(CalendarService.LoadDays(), this.date);
            selectedColor = day?.ColorHex;
            TitleInput.Text = day?.Heading() ?? "";
            NoteInput.Text = day?.Body() ?? "";
            ClearButton.Visibility = day != null ? Visibility.Visible : Visibility.Collapsed;

            Title = this.date.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
            HeadingText.Text = this.date.ToString("dddd, MMMM d", CultureInfo.InvariantCulture);
            SubheadingText.Text = this.date.Year + (this.date == DateTime.Today ? "  ·  Today" : "");

            AddSwatch(null);
            foreach (string hex in Palette)
                AddSwatch(hex);
            PaintSwatches();

            Loaded += (s, e) =>
            {
                TitleInput.Focus();
                TitleInput.CaretIndex = TitleInput.Text.Length;
            };
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

            return dialog.ShowDialog() == true;
        }

        private void AddSwatch(string hex)
        {
            Border fill = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = hex == null ? Brushes.White : B(hex)
            };

            if (hex == null)
            {
                // "No colour": a white dot with a thin diagonal slash.
                fill.BorderBrush = B("#D5D5D2");
                fill.BorderThickness = new Thickness(1);
                fill.Child = new Line
                {
                    X1 = 16, Y1 = 5, X2 = 5, Y2 = 16,
                    Stroke = B("#BDBDB9"),
                    StrokeThickness = 1.5
                };
            }

            Border ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 4, 0),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                ToolTip = hex == null ? "No color" : null,
                Child = fill
            };

            CardEffects.AttachClick(ring, () =>
            {
                selectedColor = hex;
                PaintSwatches();
            });

            swatches.Add(new KeyValuePair<Border, string>(ring, hex));
            SwatchPanel.Children.Add(ring);
        }

        private void PaintSwatches()
        {
            foreach (KeyValuePair<Border, string> swatch in swatches)
            {
                bool selected = string.Equals(swatch.Value, selectedColor, StringComparison.OrdinalIgnoreCase);
                swatch.Key.BorderBrush = selected ? (Brush)FindResource("TextMain") : Brushes.Transparent;
            }
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
            if (CalendarService.SaveDay(date, selectedColor, TitleInput.Text, NoteInput.Text))
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
            MessageBox.Show(this, "Couldn't save: calendar.json can't be read or written. Your other calendar days were left untouched.",
                "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
