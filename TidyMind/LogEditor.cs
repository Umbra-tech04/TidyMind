using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace TidyMind
{
    // A log field in the item form (LogDefinition): a compact table, newest first. A cell is edited in place like an
    // inline to-do (TextEditing.InlineEditor: Enter or clicking away saves it, Esc puts the old value back); a
    // column header sorts by that column. Works on its own copy of the rows (Rows), which the form stores on Save.
    public class LogEditor : StackPanel
    {
        private readonly LogDefinition log;
        private readonly Grid table = new Grid();
        private readonly TextBlock problem;
        private readonly TextBlock summary;
        private readonly Button addButton;

        // Each row's first cell to type into (after the date), to put the cursor there when a row is added.
        private readonly Dictionary<Dictionary<string, string>, TextBox> firstCell = new Dictionary<Dictionary<string, string>, TextBox>();

        private string sortKey;
        private bool descending = true;

        public List<Dictionary<string, string>> Rows { get; } = new List<Dictionary<string, string>>();

        // A value was just refused (shown under the table): the form doesn't save or close quietly over it.
        public bool HasProblem { get; private set; }

        // The rows worth storing: a row with nothing but its date (Add row, then nothing typed) is left out.
        public List<Dictionary<string, string>> FilledRows =>
            Rows.Where(r => log.Columns.Any(c => c.Kind != ValueKind.Date && !string.IsNullOrWhiteSpace(r.GetValueOrDefault(c.Key)))).ToList();

        public LogEditor(LogDefinition log)
        {
            this.log = log;
            sortKey = DateColumn()?.Key ?? log.Columns[0].Key;

            Children.Add(new Border
            {
                Background = Res("BgPanel"),
                BorderBrush = Res("Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 4, 4, 4),
                Child = table
            });

            problem = new TextBlock
            {
                FontSize = 12,
                Foreground = Res("Danger"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };
            Children.Add(problem);

            Grid footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            addButton = new Button
            {
                Content = Loc.T("Log.AddRow"),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"),
                Height = 30,
                Padding = new Thickness(12, 0, 12, 0)
            };
            AutomationProperties.SetName(addButton, Loc.F("Log.AddRowTo", log.Label));
            addButton.Click += (s, e) => AddRow();
            footer.Children.Add(addButton);
            summary = new TextBlock
            {
                FontSize = 12,
                Foreground = Res("TextSub"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(summary, 1);
            footer.Children.Add(summary);
            Children.Add(footer);

            Render();
        }

        // A copy of the stored rows, so nothing changes the item until the form is saved.
        public void Load(IEnumerable<Dictionary<string, string>> stored)
        {
            Rows.Clear();
            Rows.AddRange(stored.Where(row => row != null).Select(row => new Dictionary<string, string>(row)));
            Render();
        }

        private LogColumn? DateColumn() => log.Columns.FirstOrDefault(c => c.Kind == ValueKind.Date);

        private void Render()
        {
            // A cell being edited is left first, so it stores what was typed before its row is rebuilt.
            if (table.IsKeyboardFocusWithin)
                addButton.Focus();

            table.Children.Clear();
            table.RowDefinitions.Clear();
            table.ColumnDefinitions.Clear();
            firstCell.Clear();

            // Text columns take the spare width; a log without one (Fuel) shares it among its number columns.
            bool hasText = log.Columns.Any(c => c.Kind == ValueKind.Text);
            foreach (LogColumn column in log.Columns)
                table.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = column.Kind == ValueKind.Date ? new GridLength(92)
                        : column.Kind == ValueKind.Text || !hasText ? new GridLength(1, GridUnitType.Star)
                        : new GridLength(68)
                });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // delete buttons

            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (int i = 0; i < log.Columns.Length; i++)
                Place(Header(log.Columns[i]), 0, i);

            if (Rows.Count == 0)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBlock empty = new TextBlock
                {
                    Text = Loc.T("Log.Empty"),
                    FontSize = 12,
                    Foreground = Res("TextSub"),
                    Margin = new Thickness(0, 4, 0, 6)
                };
                Grid.SetColumnSpan(empty, log.Columns.Length + 1);
                Place(empty, 1, 0);
            }

            foreach (Dictionary<string, string> row in SortedRows())
            {
                int at = table.RowDefinitions.Count;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                Border line = new Border { BorderBrush = Res("Border"), BorderThickness = new Thickness(0, 1, 0, 0) };
                Grid.SetColumnSpan(line, log.Columns.Length + 1);
                Place(line, at, 0);

                for (int i = 0; i < log.Columns.Length; i++)
                {
                    TextBox cell = Cell(row, log.Columns[i]);
                    Place(cell, at, i);
                    if (!firstCell.ContainsKey(row) && log.Columns[i].Kind != ValueKind.Date)
                        firstCell[row] = cell;
                }

                Button delete = new Button
                {
                    Style = (Style)Application.Current.FindResource("RowDeleteStyle"),
                    Focusable = true, // reachable with Tab, unlike the hover-only row buttons elsewhere
                    ToolTip = Loc.T("Log.DeleteRow"),
                    VerticalAlignment = VerticalAlignment.Center
                };
                AutomationProperties.SetName(delete, Loc.T("Log.DeleteRow"));
                delete.Click += (s, e) => DeleteRow(row);
                Place(delete, at, log.Columns.Length);
            }

            UpdateSummary();
        }

        private void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            table.Children.Add(element);
        }

        // Click to sort by the column; again to turn the order around. Dates and numbers start biggest first.
        private Button Header(LogColumn column)
        {
            bool sorted = column.Key == sortKey;
            Button header = new Button
            {
                Content = column.Label + (sorted ? (descending ? " ↓" : " ↑") : ""),
                Style = (Style)Application.Current.FindResource("LinkButtonStyle"),
                Foreground = Res("TextSub"),
                FontWeight = sorted ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 2, 8, 2),
                ToolTip = Loc.F("Log.SortBy", column.Label)
            };
            header.Click += (s, e) =>
            {
                if (sorted)
                    descending = !descending;
                else
                {
                    sortKey = column.Key;
                    descending = column.Kind != ValueKind.Text;
                }
                Render();
            };
            return header;
        }

        private TextBox Cell(Dictionary<string, string> row, LogColumn column)
        {
            TextBox cell = TextEditing.InlineEditor(
                () => row.GetValueOrDefault(column.Key) ?? "",
                edited => Store(row, column, edited),
                () => addButton.Focus(),
                allowEmpty: true);
            cell.FontSize = 13;
            cell.Foreground = Res("TextMain");
            cell.Margin = new Thickness(0, 6, 8, 6);
            cell.VerticalAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(cell, column.Label);
            return cell;
        }

        // A value that doesn't fit the column is refused (the cell shows the old one again) and the reason shown.
        private bool Store(Dictionary<string, string> row, LogColumn column, string edited)
        {
            string? value = Values.Normalize(column.Kind, edited);
            if (value == null)
            {
                problem.Text = Loc.F(column.Kind == ValueKind.Date ? "Log.NeedsDate" : "Log.NeedsNumber", column.Label);
                problem.Visibility = Visibility.Visible;
                HasProblem = true;
                return false;
            }

            if (value.Length == 0)
                row.Remove(column.Key);
            else
                row[column.Key] = value;
            problem.Visibility = Visibility.Collapsed;
            HasProblem = false;
            UpdateSummary();
            return true;
        }

        private void UpdateSummary()
        {
            string? line = log.Summary?.Invoke(Rows);
            summary.Text = line ?? "";
            summary.Visibility = line == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // Rows with no value in the sort column go last either way.
        private IEnumerable<Dictionary<string, string>> SortedRows()
        {
            LogColumn column = log.Columns.First(c => c.Key == sortKey);
            List<Dictionary<string, string>> filled = Rows.Where(r => !string.IsNullOrWhiteSpace(r.GetValueOrDefault(column.Key))).ToList();
            IEnumerable<Dictionary<string, string>> blank = Rows.Except(filled);

            IEnumerable<Dictionary<string, string>> ordered;
            if (column.Kind == ValueKind.Text)
            {
                Func<Dictionary<string, string>, string> text = r => r[column.Key];
                ordered = descending ? filled.OrderByDescending(text, Loc.Comparer)
                                     : filled.OrderBy(text, Loc.Comparer);
            }
            else
            {
                Func<Dictionary<string, string>, double> number = column.Kind == ValueKind.Date
                    ? r => Values.ParseDate(r[column.Key])?.Ticks ?? 0
                    : r => Values.Number(r[column.Key]) ?? 0;
                ordered = descending ? filled.OrderByDescending(number) : filled.OrderBy(number);
            }
            return ordered.Concat(blank);
        }

        // Dated today; the cursor goes to the first cell to fill in.
        private void AddRow()
        {
            Dictionary<string, string> row = new Dictionary<string, string>();
            LogColumn? date = DateColumn();
            if (date != null)
                row[date.Key] = Values.FromDate(DateTime.Today)!;
            Rows.Add(row);
            Render();

            if (firstCell.TryGetValue(row, out TextBox? cell))
                Dispatcher.BeginInvoke(new Action(() => cell.Focus()), DispatcherPriority.Input);
        }

        // Only a row with something besides its date asks first.
        private void DeleteRow(Dictionary<string, string> row)
        {
            bool hasContent = log.Columns.Any(c => c.Kind != ValueKind.Date && !string.IsNullOrWhiteSpace(row.GetValueOrDefault(c.Key)));
            if (hasContent && Msg.Show(Window.GetWindow(this), Loc.T("Log.DeleteConfirm"), Loc.T("Common.ConfirmDelete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            Rows.Remove(row);
            Render();
            addButton.Focus();
        }

        private static Brush Res(string key) => (Brush)Application.Current.FindResource(key);
    }
}
