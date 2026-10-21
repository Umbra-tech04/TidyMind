using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // Tips & shortcuts, from the ? button: Tips.All grouped by where they work, a gesture and a sentence each. Typing in
    // the box keeps the entries whose keys, sentence or group match (accent-insensitive, TextSearch). One at a time.
    public partial class TipsWindow : Window
    {
        public TipsWindow()
        {
            InitializeComponent();
            Render();
            Loc.Track(this, Render);
            Loaded += (s, e) => FilterBox.Focus();
        }

        // The open one is brought to the front instead of a second being opened.
        public static void ShowFor(Window owner)
        {
            TipsWindow? open = Application.Current.Windows.OfType<TipsWindow>().FirstOrDefault();
            if (open != null)
            {
                open.Activate();
                return;
            }
            new TipsWindow { Owner = owner }.Show();
        }

        private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => Render();

        // Esc empties the filter first, then closes.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled)
                return;
            e.Handled = true;
            if (FilterBox.Text.Length > 0)
                FilterBox.Clear();
            else
                Close();
        }

        private void Render()
        {
            Groups.Children.Clear();
            string filter = FilterBox.Text.Trim();
            foreach (IGrouping<string, Tip> group in Tips.All.GroupBy(t => t.Group))
            {
                Tip[] shown = group.Where(t => filter.Length == 0 || TextSearch.Contains(t.Keys + " " + t.Text + " " + t.GroupTitle, filter)).ToArray();
                if (shown.Length == 0)
                    continue;

                Groups.Children.Add(new TextBlock
                {
                    Text = shown[0].GroupTitle,
                    Style = (Style)FindResource("SectionLabelStyle")
                });
                StackPanel rows = new StackPanel();
                for (int i = 0; i < shown.Length; i++)
                    rows.Children.Add(Row(shown[i], first: i == 0));
                Groups.Children.Add(new Border { Style = (Style)FindResource("CardStyle"), Child = rows });
            }
            NothingFound.Visibility = Groups.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // The keys on the left, in the column's width; the sentence beside them. A hairline between rows.
        private UIElement Row(Tip tip, bool first)
        {
            Grid row = new Grid { Margin = new Thickness(14, 0, 14, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            TextBlock keys = new TextBlock
            {
                Text = tip.Keys,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextMain"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 16, 0)
            };
            TextBlock text = new TextBlock
            {
                Text = tip.Text,
                FontSize = 13,
                Foreground = (Brush)FindResource("TextBody"),
                TextWrapping = TextWrapping.Wrap
            };
            Grid.SetColumn(text, 1);
            row.Children.Add(keys);
            row.Children.Add(text);
            return new Border
            {
                BorderBrush = (Brush)FindResource("Border"),
                BorderThickness = new Thickness(0, first ? 0 : 1, 0, 0),
                Padding = new Thickness(0, 10, 0, 10),
                Child = row
            };
        }
    }
}
