using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace TidyMind
{
    // Ctrl+Shift+C: a month calendar that slides in from the right edge, over the main window.
    public partial class CalendarPanel : UserControl
    {
        private const double HiddenOffset = 440; // sheet width + its shadow
        private static readonly CultureInfo English = CultureInfo.InvariantCulture;
        private static readonly SolidColorBrush ReminderDot = B("#4A4945");

        private readonly DayOfWeek firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        private DateTime shownMonth;
        private bool open;
        private DateTime? highlightedDate;
        private IInputElement focusBeforeOpen;

        public CalendarPanel()
        {
            InitializeComponent();
            BuildWeekdayRow();
        }

        public bool IsOpen => open;

        public void Open()
        {
            if (open) return;
            open = true;

            highlightedDate = null;
            shownMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            Render();

            focusBeforeOpen = Keyboard.FocusedElement;
            Visibility = Visibility.Visible;

            // No From values: toggling mid-animation continues from wherever the sheet currently is.
            Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
            Slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

            Dispatcher.BeginInvoke(new Action(() => Sheet.Focus()), DispatcherPriority.Input);
        }

        public void Close()
        {
            if (!open) return;
            open = false;

            Backdrop.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)));
            DoubleAnimation slideOut = new DoubleAnimation(HiddenOffset, TimeSpan.FromMilliseconds(220))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };
            slideOut.Completed += (s, e) =>
            {
                if (!open)
                    Visibility = Visibility.Collapsed;
            };
            Slide.BeginAnimation(TranslateTransform.XProperty, slideOut);

            if (focusBeforeOpen is UIElement previous && previous.IsVisible)
                previous.Focus();
            focusBeforeOpen = null;
        }

        // ---- Rendering ----------------------------------------------------

        private void BuildWeekdayRow()
        {
            for (int i = 0; i < 7; i++)
            {
                DayOfWeek day = (DayOfWeek)(((int)firstDayOfWeek + i) % 7);
                WeekdayRow.Children.Add(new TextBlock
                {
                    Text = English.DateTimeFormat.GetAbbreviatedDayName(day).ToUpperInvariant(),
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (Brush)FindResource("TextSub"),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
            }
        }

        private void Render()
        {
            MonthText.Text = shownMonth.ToString("MMMM yyyy", English);

            List<CalendarDay> days = CalendarService.LoadDays();
            List<Reminder> reminders = ReminderManager.TryLoadReminders();

            DayGrid.Children.Clear();
            int lead = ((int)shownMonth.DayOfWeek - (int)firstDayOfWeek + 7) % 7;
            DateTime start = shownMonth.AddDays(-lead);
            for (int i = 0; i < 42; i++)
            {
                DateTime date = start.AddDays(i);
                DayGrid.Children.Add(CreateDayCell(date, CalendarService.GetDay(days, date), CalendarService.RemindersOn(reminders, date)));
            }

            RenderMonthList(days, reminders);
        }

        private FrameworkElement CreateDayCell(DateTime date, CalendarDay day, List<Reminder> reminders)
        {
            bool inMonth = date.Month == shownMonth.Month;
            bool today = date == DateTime.Today;
            Color? color = ParseColor(day?.ColorHex);
            bool highlighted = date == highlightedDate;
            Brush restingBorder = highlighted ? CardEffects.Accent : color.HasValue ? new SolidColorBrush(color.Value) : B("#E6E6E3");

            Border cell = new Border
            {
                Height = 46,
                Margin = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(highlighted ? 2 : 1),
                BorderBrush = restingBorder,
                Background = color.HasValue ? new SolidColorBrush(Color.FromArgb(0x40, color.Value.R, color.Value.G, color.Value.B)) : Brushes.White,
                Cursor = Cursors.Hand,
                Opacity = inMonth ? 1 : 0.45
            };

            Grid content = new Grid();

            if (today)
            {
                content.Children.Add(new Ellipse
                {
                    Width = 30,
                    Height = 30,
                    Stroke = CardEffects.Accent,
                    StrokeThickness = 2,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            content.Children.Add(new TextBlock
            {
                Text = date.Day.ToString(),
                FontSize = 13,
                FontWeight = today ? FontWeights.Bold : FontWeights.Normal,
                Foreground = today ? CardEffects.Accent : (Brush)FindResource("TextMain"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (reminders.Count > 0)
            {
                content.Children.Add(new Ellipse
                {
                    Width = 6,
                    Height = 6,
                    Fill = ReminderDot,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 5, 5, 0)
                });
            }

            if (day != null && day.HasEntry())
            {
                content.Children.Add(new Rectangle
                {
                    Width = 12,
                    Height = 2,
                    RadiusX = 1,
                    RadiusY = 1,
                    Fill = (Brush)FindResource("TextSub"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Margin = new Thickness(0, 0, 0, 5)
                });
            }

            cell.Child = content;
            cell.ToolTip = CreateTooltip(date, day, reminders);
            ToolTipService.SetInitialShowDelay(cell, 300);

            cell.MouseEnter += (s, e) => cell.BorderBrush = CardEffects.Accent;
            cell.MouseLeave += (s, e) => cell.BorderBrush = restingBorder;
            CardEffects.AttachClick(cell, () => EditDay(date));

            cell.ContextMenu = AgendaMenus.ForDay(this, date, DayAsText(day, reminders), AfterChange);

            return cell;
        }

        // The note, then each reminder as "HH:mm  message"; null for a day with neither.
        private static string DayAsText(CalendarDay day, List<Reminder> reminders)
        {
            List<string> lines = new List<string>();
            if (day != null && day.HasEntry())
            {
                lines.Add(day.Heading());
                string body = day.Body();
                if (body.Length > 0)
                    lines.Add(body);
            }
            lines.AddRange(reminders.Select(r => r.NextFireTime.ToString("HH:mm") + "  " + r.Message));

            return lines.Count == 0 ? null : string.Join(Environment.NewLine, lines);
        }

        // Note + that day's reminders; nothing for an empty day.
        private static object CreateTooltip(DateTime date, CalendarDay day, List<Reminder> reminders)
        {
            bool hasNote = day != null && day.HasEntry();
            if (!hasNote && reminders.Count == 0)
                return null;

            StackPanel tip = new StackPanel { MaxWidth = 280, Margin = new Thickness(2) };
            tip.Children.Add(new TextBlock
            {
                Text = date.ToString("dddd, MMMM d", English),
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4)
            });

            if (hasNote)
            {
                string body = day.Body();
                tip.Children.Add(new TextBlock { Text = day.Heading(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, body.Length > 0 ? 1 : 4) });
                if (body.Length > 0)
                    tip.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 4) });
            }

            foreach (Reminder reminder in reminders)
            {
                tip.Children.Add(new TextBlock
                {
                    Text = "🔔  " + reminder.NextFireTime.ToString("HH:mm") + "   " + reminder.Message,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85
                });
            }

            return tip;
        }

        private void RenderMonthList(List<CalendarDay> days, List<Reminder> reminders)
        {
            string monthName = shownMonth.ToString("MMMM", English);
            MonthListHeader.Text = "IN " + monthName.ToUpperInvariant();
            MonthList.Children.Clear();

            for (DateTime date = shownMonth; date.Month == shownMonth.Month; date = date.AddDays(1))
            {
                CalendarDay day = CalendarService.GetDay(days, date);
                List<Reminder> onDay = CalendarService.RemindersOn(reminders, date);
                if (day == null && onDay.Count == 0)
                    continue;

                MonthList.Children.Add(CreateMonthRow(date, day, onDay));
            }

            if (MonthList.Children.Count == 0)
            {
                MonthList.Children.Add(new TextBlock
                {
                    Text = "Nothing planned in " + monthName + " yet.",
                    FontSize = 12,
                    FontStyle = FontStyles.Italic,
                    Foreground = (Brush)FindResource("TextSub"),
                    Margin = new Thickness(2, 0, 0, 0)
                });
            }
        }

        private FrameworkElement CreateMonthRow(DateTime date, CalendarDay day, List<Reminder> reminders)
        {
            Color? color = ParseColor(day?.ColorHex);

            Border row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(8, 6, 8, 6),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };
            row.MouseEnter += (s, e) => row.Background = Brushes.White;
            row.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            CardEffects.AttachClick(row, () => EditDay(date));

            // Same menus as the Home week: the note and each reminder line have their own (Edit / Delete /
            // Add Reminder / Copy); the rest of the row is the day (Add Reminder / Copy).
            row.ContextMenu = AgendaMenus.ForDay(this, date, DayAsText(day, reminders), AfterChange);

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(54) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            grid.Children.Add(new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = color.HasValue ? new SolidColorBrush(color.Value) : Brushes.Transparent,
                Stroke = color.HasValue ? null : B("#D5D5D2"),
                StrokeThickness = 1,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 0, 0)
            });

            TextBlock dateText = new TextBlock
            {
                Text = date.ToString("ddd d", English),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = date == DateTime.Today ? CardEffects.Accent : (Brush)FindResource("TextMain")
            };
            Grid.SetColumn(dateText, 1);
            grid.Children.Add(dateText);

            StackPanel lines = new StackPanel();
            if (day != null && day.HasEntry())
            {
                string body = day.Body();
                lines.Children.Add(new TextBlock
                {
                    Text = day.Heading(),
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextMain"),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Background = Brushes.Transparent, // the whole line, not just the letters, answers a right-click
                    ContextMenu = AgendaMenus.ForNote(this, date,
                        body.Length > 0 ? day.Heading() + Environment.NewLine + body : day.Heading(), AfterChange)
                });
            }
            foreach (Reminder reminder in reminders)
            {
                string text = reminder.NextFireTime.ToString("HH:mm", English) + "  " + reminder.Message;
                StackPanel line = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 1, 0, 0),
                    Background = Brushes.Transparent,
                    ContextMenu = AgendaMenus.ForReminder(this, reminder.Id, date, reminder.Message, AfterChange)
                };
                line.Children.Add(new Ellipse { Width = 5, Height = 5, Fill = ReminderDot, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
                line.Children.Add(new TextBlock
                {
                    Text = text,
                    FontSize = 12,
                    Foreground = (Brush)FindResource("TextSub"),
                    TextTrimming = TextTrimming.CharacterEllipsis
                });
                lines.Children.Add(line);
            }
            if (lines.Children.Count == 0)
                lines.Children.Add(new TextBlock { Text = "Colored", FontSize = 12, Foreground = (Brush)FindResource("TextSub") });
            Grid.SetColumn(lines, 2);
            grid.Children.Add(lines);

            row.Child = grid;
            return row;
        }

        // ---- Actions ------------------------------------------------------

        private void EditDay(DateTime date)
        {
            if (DayEditDialog.Edit(this, date))
                Render();
            Sheet.Focus(); // so Esc closes the panel again
        }

        // After a right-click action changed a day: redraw, and give the panel focus back so Esc closes it again.
        private void AfterChange()
        {
            Render();
            Sheet.Focus();
        }

        // Opened from somewhere pointing at a day (e.g. a dashboard note): its month, with that day marked.
        public void ShowDate(DateTime date)
        {
            highlightedDate = date.Date;
            ShowMonth(date);
        }

        private void ShowMonth(DateTime month)
        {
            shownMonth = new DateTime(month.Year, month.Month, 1);
            Render();
        }

        private void PrevMonth_Click(object sender, RoutedEventArgs e) => ShowMonth(shownMonth.AddMonths(-1));
        private void NextMonth_Click(object sender, RoutedEventArgs e) => ShowMonth(shownMonth.AddMonths(1));
        private void PrevYear_Click(object sender, RoutedEventArgs e) => ShowMonth(shownMonth.AddYears(-1));
        private void NextYear_Click(object sender, RoutedEventArgs e) => ShowMonth(shownMonth.AddYears(1));
        private void MonthLabel_Click(object sender, RoutedEventArgs e) => ShowMonth(DateTime.Today);

        private void DayGrid_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            ShowMonth(shownMonth.AddMonths(e.Delta > 0 ? -1 : 1));
            e.Handled = true;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close();

        private void Panel_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
            else if (Keyboard.FocusedElement == Sheet && (e.Key == Key.Left || e.Key == Key.Right))
            {
                ShowMonth(shownMonth.AddMonths(e.Key == Key.Left ? -1 : 1));
                e.Handled = true;
            }
        }

        // ---- Helpers ------------------------------------------------------

        private static Color? ParseColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return null;
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
