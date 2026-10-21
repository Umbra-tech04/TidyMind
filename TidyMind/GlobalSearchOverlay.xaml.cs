using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace TidyMind
{
    // Ctrl+Shift+F palette: type to search every memory at once, arrows + Enter (or a click) to jump there.
    public partial class GlobalSearchOverlay : UserControl
    {
        private const int MaxResults = 50;

        // Tallest the card gets (input + full results list + footer); used to keep the input from jumping.
        private const double MaxCardHeight = 470;

        private static readonly SolidColorBrush SubOnAccent = B("#D6E6F5");

        private List<SearchResult> index = new List<SearchResult>();
        private List<SearchResult> results = new List<SearchResult>();
        private readonly List<Border> rows = new List<Border>();
        private int highlighted = -1;
        private Action<SearchResult> onChoose;
        private IInputElement focusBeforeOpen;

        // Results follow a short pause in typing rather than every key (the index can hold long note pages);
        // Enter and the arrows first show what was typed (ApplyPendingSearch).
        private readonly DispatcherTimer typingPause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        public GlobalSearchOverlay()
        {
            InitializeComponent();
            typingPause.Tick += (s, e) => ApplyPendingSearch();
        }

        public bool IsOpen => Visibility == Visibility.Visible;

        public void Open(List<SearchResult> searchIndex, Action<SearchResult> choose)
        {
            if (IsOpen) return;

            index = searchIndex;
            onChoose = choose;
            focusBeforeOpen = Keyboard.FocusedElement;
            QueryBox.Text = "";
            ApplyPendingSearch(); // nothing left over from the last time it was open
            Visibility = Visibility.Visible;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));

            // Deferred so the key press that opened us has finished routing before focus moves.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                QueryBox.Focus();
                QueryBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        public void Close(bool restoreFocus)
        {
            if (!IsOpen) return;

            typingPause.Stop();
            BeginAnimation(OpacityProperty, null);
            Visibility = Visibility.Collapsed;

            if (restoreFocus && focusBeforeOpen is UIElement previous && previous.IsVisible)
                previous.Focus();
            focusBeforeOpen = null;
        }

        // ---- Results ------------------------------------------------------

        private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            Placeholder.Visibility = QueryBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            typingPause.Stop();
            typingPause.Start();
        }

        // Typed but not shown yet: shown now, so Enter or an arrow acts on what was typed.
        private void ApplyPendingSearch()
        {
            typingPause.Stop();
            ShowResults();
        }

        private void ShowResults()
        {
            string query = QueryBox.Text.Trim();
            results = SearchIndexBuilder.Filter(index, query, MaxResults);

            ResultsPanel.Children.Clear();
            rows.Clear();
            highlighted = -1;

            for (int i = 0; i < results.Count; i++)
            {
                Border row = CreateRow(results[i], query, i);
                rows.Add(row);
                ResultsPanel.Children.Add(row);
            }

            ResultsArea.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            ResultsScroller.Visibility = results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            MessageText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            MessageText.Text = Loc.F("Search.NoMatch", query);

            ResultsScroller.ScrollToTop();
            if (results.Count > 0)
                Highlight(0);
        }

        private Border CreateRow(SearchResult result, string query, int position)
        {
            Border row = new Border
            {
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 8, 12, 8),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            TextBlock icon = new TextBlock
            {
                Text = IconOf(result.Kind),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 14, 0)
            };
            grid.Children.Add(icon);

            TextBlock title = new TextBlock
            {
                FontSize = 14,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            // The words around the match when it's in the text, not in the title itself.
            bool inTitle = result.Text == null || TextSearch.Contains(result.Title, query);
            AddHighlightedText(title, inTitle ? result.Title ?? "" : AroundMatch(result, query), query);

            TextBlock location = new TextBlock
            {
                Text = result.Location,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(title);
            text.Children.Add(location);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            TextBlock kind = new TextBlock
            {
                Text = LabelOf(result.Kind),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            Grid.SetColumn(kind, 2);
            grid.Children.Add(kind);

            row.Child = grid;
            row.Tag = new RowParts { Icon = icon, Title = title, Location = location, Kind = kind };
            Paint(row, false);

            // MouseMove, not MouseEnter: rows scrolled under a still cursor by the arrow keys shouldn't steal the highlight.
            row.MouseMove += (s, e) => Highlight(position);
            CardEffects.AttachClick(row, () => Choose(position));

            return row;
        }

        // A note page shows the words around the match, on one line.
        // From the text prepared with the index, so the whole page isn't folded again for each row.
        private static string AroundMatch(SearchResult result, string query)
        {
            string text = result.Searchable.Text;
            int at = Math.Max(0, result.Searchable.IndexOf(TextSearch.FoldQuery(query), out int length));
            int start = Math.Max(0, at - 30);
            string around = text.Substring(start, Math.Min(text.Length - start, at - start + length + 80));
            around = string.Join(" ", around.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return (start > 0 ? "…" : "") + around;
        }

        private static void AddHighlightedText(TextBlock block, string text, string query)
        {
            // The match can differ in length from the query once accents are ignored, so its own length is used.
            int at = TextSearch.IndexOf(text, query, out int length);
            if (at < 0)
            {
                block.Text = text;
                return;
            }

            block.Inlines.Add(new Run(text.Substring(0, at)));
            block.Inlines.Add(new Run(text.Substring(at, length)) { FontWeight = FontWeights.Bold });
            block.Inlines.Add(new Run(text.Substring(at + length)));
        }

        private void Highlight(int position)
        {
            if (position == highlighted || position < 0 || position >= rows.Count) return;

            if (highlighted >= 0 && highlighted < rows.Count)
                Paint(rows[highlighted], false);

            highlighted = position;
            Paint(rows[highlighted], true);
            rows[highlighted].BringIntoView();
        }

        private static void Paint(Border row, bool active)
        {
            RowParts parts = (RowParts)row.Tag;
            Brush accent = (Brush)Application.Current.FindResource("Accent");
            Brush sub = (Brush)Application.Current.FindResource("TextSub");

            row.Background = active ? accent : Brushes.Transparent;
            parts.Icon.Foreground = active ? Brushes.White : accent;
            parts.Title.Foreground = active ? Brushes.White : (Brush)Application.Current.FindResource("TextMain");
            parts.Location.Foreground = active ? SubOnAccent : sub;
            parts.Kind.Foreground = active ? SubOnAccent : sub;
        }

        private void Choose(int position)
        {
            if (position < 0 || position >= results.Count) return;

            SearchResult result = results[position];
            Close(false);
            onChoose?.Invoke(result);
        }

        // ---- Input --------------------------------------------------------

        private void Overlay_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (typingPause.IsEnabled && (e.Key == Key.Enter || e.Key == Key.Up || e.Key == Key.Down))
                ApplyPendingSearch();

            switch (e.Key)
            {
                case Key.Escape:
                    Close(true);
                    e.Handled = true;
                    break;
                case Key.Down:
                    Highlight(Math.Min(highlighted + 1, rows.Count - 1));
                    e.Handled = true;
                    break;
                case Key.Up:
                    Highlight(Math.Max(highlighted - 1, 0));
                    e.Handled = true;
                    break;
                case Key.Enter:
                    Choose(highlighted);
                    e.Handled = true;
                    break;
            }
        }

        private void Backdrop_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Close(true);

        private void Card_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        // The card grows downward as results appear; its top stays put so the input never jumps.
        private void Backdrop_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            double top = Math.Max(40, (e.NewSize.Height - MaxCardHeight) / 2);
            Card.Margin = new Thickness(16, top, 16, 16);
        }

        private static string IconOf(SearchResultKind kind)
        {
            switch (kind)
            {
                case SearchResultKind.Memory: return "\uE8B7";  // folder
                case SearchResultKind.Project: return "\uE7C3"; // page
                case SearchResultKind.File: return "\uE723";    // paperclip
                case SearchResultKind.Note: return "\uE70B";    // note
                case SearchResultKind.MemoryNote: return "\uE736"; // notebook
                case SearchResultKind.PlanBlock: return "\uE823"; // clock
                default: return "\uE8EC";                       // tag
            }
        }

        private static string LabelOf(SearchResultKind kind)
        {
            switch (kind)
            {
                case SearchResultKind.Memory: return Loc.T("Search.Kind.Memory");
                case SearchResultKind.Project: return Loc.T("Search.Kind.Project");
                case SearchResultKind.File: return Loc.T("Search.Kind.File");
                case SearchResultKind.Note: return Loc.T("Search.Kind.Note");
                case SearchResultKind.MemoryNote: return Loc.T("Search.Kind.Notebook");
                case SearchResultKind.PlanBlock: return Loc.T("Search.Kind.Plan");
                default: return Loc.T("Search.Kind.Item");
            }
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }

        private class RowParts
        {
            public TextBlock Icon, Title, Location, Kind;
        }
    }
}
