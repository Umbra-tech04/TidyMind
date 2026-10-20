using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // A vehicle's own window (opened from its card): the pictures and key facts on top, then tabs: an Overview (what's
    // coming up, the odometer, key specs, the last service, notes) and the type's tabs (EntityTypes.Tabs) of cards. A
    // card is edited in place and saved on its own, at once, through the collection view (Host.Save), which saves the
    // item as an edit from the item form is saved; one card is edited at a time. Esc stops an edit, else closes; Left
    // and Right browse the pictures. Its size is remembered.
    public partial class VehicleWindow : Window
    {
        // What the collection view does for it. Save: stores the edited copy, false if it couldn't (the user was told).
        // EditBasics: the item form for the name and pictures, the item as saved or null. Delete: true once it's gone.
        public sealed class Host
        {
            public Func<Entity, bool> Save { get; init; } = _ => false;
            public Func<Entity, Entity?> EditBasics { get; init; } = _ => null;
            public Func<Entity, bool> Delete { get; init; } = _ => false;
        }

        private const string OverviewKey = VehicleSearch.OverviewTab;
        private const double WideHeader = 860;

        // The key facts the Overview lists (from the Specs sections).
        private static readonly string[] KeySpecs = { "Make", "Model", "Edition", "Year", "BodyType", "Fuel", "Displacement", "Power", "Gearbox", "Drive", "Vin" };

        private Entity vehicle;
        private readonly Host host;
        private readonly PictureBrowser gallery = new PictureBrowser(double.NaN, 80, canReorder: false, stripForOne: false);
        private string tab = OverviewKey;
        private VehicleCard? editing;
        private (RecordDefinition Records, EntityRecord Record, Guid? Parent)? adding;

        // The kind each flat list shows (RecordDefinition.FilterKey) by its key; none: all.
        private readonly Dictionary<string, string> filters = new Dictionary<string, string>();

        // The nested list's (parts') outline: which record is shown and which are open, kept across redraws.
        private Guid? selectedPart;
        private readonly HashSet<Guid> openParts = new HashSet<Guid>();

        public VehicleWindow(Entity vehicle, Host host)
        {
            InitializeComponent();
            this.vehicle = vehicle;
            this.host = host;
            Gallery.Content = gallery;
            gallery.Picked += index => ShowPictures(index);

            AppSettings settings = AppSettings.Load();
            Width = Math.Min(settings.VehicleWindowWidth ?? 1080, SystemParameters.WorkArea.Width - 40);
            Height = Math.Min(settings.VehicleWindowHeight ?? 780, SystemParameters.WorkArea.Height - 40);
            SizeChanged += (s, e) => LayoutHeader();

            Loc.Track(this, Redraw);
            Redraw();
        }

        // ---- Drawing --------------------------------------------------------------------------

        private void Redraw()
        {
            Title = vehicle.Name;
            ShowHeader();
            ShowTabs();
            ShowTab();
        }

        private void LayoutHeader()
        {
            bool wide = ActualWidth >= WideHeader;
            FactsColumn.Width = wide ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
            Grid.SetColumn(Facts, wide ? 1 : 0);
            Grid.SetRow(Facts, wide ? 0 : 1);
            Facts.Margin = wide ? new Thickness(24, 0, 0, 0) : new Thickness(0, 16, 0, 0);
            GalleryFrame.Height = wide ? 280 : 220;
        }

        private void ShowHeader()
        {
            NameText.Text = vehicle.Name;
            FieldDefinition kind = Field("Kind");
            string makeModel = string.Join(" ", new[] { "Make", "Model", "Edition" }.Select(k => Field(k).Read(vehicle)?.Trim()).Where(v => !string.IsNullOrEmpty(v)));
            SubtitleText.Text = string.Join(" · ", new[] { makeModel, vehicle.Year?.ToString(), kind.Show(kind.Read(vehicle), vehicle) }
                .Where(v => !string.IsNullOrEmpty(v)));
            SubtitleText.Visibility = SubtitleText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            PlateText.Text = vehicle.Plate ?? "";
            PlateChip.Visibility = string.IsNullOrWhiteSpace(vehicle.Plate) ? Visibility.Collapsed : Visibility.Visible;
            OdometerText.Text = vehicle.OdometerKm is int km ? Units.Format(km, Units.Distance, vehicle) : Loc.T("Vehicle.Odometer.None");
            OdometerText.Foreground = VehicleCard.Res(vehicle.OdometerKm == null ? "TextSub" : "TextMain");

            Badges.Children.Clear();
            foreach (Due due in Dues().Take(3))
                Badges.Children.Add(Badge(due));

            ShowPictures(gallery.Selected);
        }

        // What comes due, the most urgent first: readings close or passed (EntityTypes.DistanceDues), then the dates
        // (EntityTypes.Expiries) by how close they are. Shown as badges, on the Overview and on the card.
        private sealed record Due(string Name, string When, string Detail, ExpiryLevel Level, string Tab);

        private List<Due> Dues()
        {
            string TabOf(RecordDefinition kind) => EntityTypes.Tabs(vehicle.EntityType).First(t => t.Records.Contains(kind)).Key;
            IEnumerable<Due> readings = EntityTypes.DistanceDues(vehicle).Select(d => new Due(d.Name, d.Describe(vehicle),
                Loc.F("Expiry.AtReading", Units.Format(d.Remaining + vehicle.OdometerKm!.Value, Units.Distance, vehicle)), d.Level, TabOf(d.Kind)));
            IEnumerable<Due> dates = EntityTypes.Expiries(vehicle).Select(d => new Due(d.Name, Expiry.Describe(d.Date, DateTime.Today),
                d.Date.ToString(Loc.T("Format.LongDate"), Loc.Culture), Expiry.Level(d.Date, DateTime.Today), TabOf(d.Kind)));
            return readings.Concat(dates).OrderByDescending(d => d.Level).ToList(); // stable: each kind keeps its own order
        }

        // "Inspection in 12 days" in its urgency's color and weight (Expiry), never by color alone.
        private static Border Badge(Due due) => new Border
        {
            Background = VehicleCard.Res("BgSubtle"),
            BorderBrush = VehicleCard.Res(due.Level == ExpiryLevel.Later ? "Border" : due.Level == ExpiryLevel.Expired ? "Danger" : "BtnHover"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 3, 10, 4),
            Margin = new Thickness(0, 0, 8, 6),
            ToolTip = due.Detail,
            Child = new TextBlock
            {
                Text = Loc.F("Entity.ExpiryLine", due.Name, due.When),
                FontSize = 12,
                Foreground = Expiry.Brush(due.Level),
                FontWeight = Expiry.Weight(due.Level)
            }
        };

        // The cover first, then whichever is picked (arrows, keys, strip); decoded off the UI thread.
        private void ShowPictures(int index)
        {
            List<string> names = EntityImages.Names(vehicle);
            NoPictures.Visibility = names.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            gallery.Visibility = names.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            Entity shown = vehicle;
            gallery.ShowStored(names, index, () => vehicle == shown && IsVisible);
        }

        private void ShowTabs()
        {
            TabsPanel.Children.Clear();
            foreach ((string key, string label) in EntityTypes.Tabs(vehicle.EntityType).Select(t => (t.Key, t.Label)).Prepend((OverviewKey, Loc.T("Entity.Tab.Overview"))))
            {
                RadioButton button = new RadioButton
                {
                    Content = label,
                    Style = (Style)FindResource("SegmentStyle"),
                    GroupName = "VehicleTabs",
                    Height = 32,
                    FontSize = 13,
                    Padding = new Thickness(14, 0, 14, 0),
                    Margin = new Thickness(0, 0, 4, 0),
                    IsChecked = key == tab
                };
                button.Checked += (s, e) =>
                {
                    if (key == tab)
                        return;
                    if (!LeaveEdit())
                    {
                        SelectTab(tab); // back to the tab being edited
                        return;
                    }
                    tab = key;
                    adding = null;
                    ShowTab();
                    ScrollToTabs();
                };
                button.Tag = key;
                TabsPanel.Children.Add(button);
            }
        }

        // A tab's cards start at the top: the page goes back up to the tabs if it was further down.
        private void ScrollToTabs()
        {
            double tabs = TabsPanel.TranslatePoint(new Point(0, 0), (UIElement)ContentScroll.Content).Y;
            if (ContentScroll.VerticalOffset > tabs)
                ContentScroll.ScrollToVerticalOffset(tabs);
        }

        public void SelectTab(string key) =>
            TabsPanel.Children.OfType<RadioButton>().First(b => (string)b.Tag == key).IsChecked = true;

        private void ShowTab()
        {
            editing = null;
            Cards.Children.Clear();
            IEnumerable<UIElement> cards;
            if (SearchBox.Text.Trim().Length > 0)
                cards = new[] { SearchResults(SearchBox.Text.Trim()) };
            else if (tab == OverviewKey)
                cards = OverviewCards();
            else
            {
                TabDefinition definition = EntityTypes.Tabs(vehicle.EntityType).First(t => t.Key == tab);
                cards = definition.Sections.Select(SectionCardOf).Cast<UIElement>()
                    .Concat(definition.Records.SelectMany(RecordCards))
                    .Concat(definition.Logs.Select(LogCardOf));
            }
            foreach (UIElement card in cards)
                Cards.Children.Add(card);
        }

        // ---- Search -------------------------------------------------------------------------------

        // While the box has text the page lists what matches, in place of the tab. An edit going on is stopped first
        // (asking if something was typed); kept, the search is emptied again.
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!LeaveEdit())
            {
                SearchBox.Clear();
                return;
            }
            adding = null;
            ShowTab();
        }

        private VehicleCard SearchResults(string query)
        {
            List<VehicleMatch> matches = VehicleSearch.Find(vehicle, query);
            VehicleCard card = new VehicleCard(matches.Count == 0 ? Loc.F("Vehicle.Search.None", query) : Loc.Plural("Vehicle.Search.Found", matches.Count));
            CellPanel.SetWide(card, true);
            foreach (VehicleMatch match in matches)
            {
                StackPanel lines = new StackPanel();
                lines.Children.Add(VehicleCard.Muted(match.Where, 11));
                lines.Children.Add(new TextBlock { Text = match.Text, FontSize = 13, Foreground = VehicleCard.Res("TextMain"), TextWrapping = TextWrapping.Wrap });
                Button row = new Button
                {
                    Template = PictureBrowser.FlatTemplate,
                    Cursor = Cursors.Hand,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Margin = new Thickness(0, 0, 0, 10),
                    Content = lines
                };
                AutomationProperties.SetName(row, match.Where + ": " + match.Text);
                row.Click += (s, e) => GoTo(match);
                card.Body.Children.Add(row);
            }
            return card;
        }

        // The match's tab, with its record shown (a part selected in the outline, the ones above it opened) and in view.
        private void GoTo(VehicleMatch match)
        {
            if (match.Record is Guid id)
            {
                RecordDefinition? nested = EntityTypes.Records(vehicle.EntityType).FirstOrDefault(r => r.Nested && RecordTree.Find(r.Read(vehicle), id) != null);
                if (nested != null)
                {
                    selectedPart = id;
                    foreach (EntityRecord above in RecordTree.Walk(nested.Read(vehicle)).First(x => x.Record.Id == id).Above)
                        openParts.Add(above.Id);
                }
            }
            tab = match.Tab;
            SearchBox.Clear(); // draws the tab
            SelectTab(match.Tab);
            if (match.Record is Guid shown)
                Dispatcher.BeginInvoke(new Action(() =>
                    Cards.Children.OfType<FrameworkElement>().FirstOrDefault(c => shown.Equals(c.Tag))?.BringIntoView()),
                    System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---- Overview ----------------------------------------------------------------------------

        private IEnumerable<UIElement> OverviewCards()
        {
            yield return DeadlinesCard();
            yield return OdometerCard();
            yield return KeySpecsCard();
            yield return LastServiceCard();
            yield return SectionCardOf(VehicleSchema.Notes);
        }

        // Everything that comes due, the most urgent first, with how close it is; a click opens its tab.
        private VehicleCard DeadlinesCard()
        {
            VehicleCard card = new VehicleCard(Loc.T("Vehicle.Overview.Deadlines"));
            List<Due> dues = Dues();
            if (dues.Count == 0)
            {
                card.ShowEmpty(Loc.T("Vehicle.Overview.NoDeadlines"), VehicleSchema.Documents.AddLabel, () => { SelectTab("Documents"); StartAdd(VehicleSchema.Documents, null); });
                return card;
            }
            card.Actions.Children.Add(VehicleCard.Link(Loc.T("Vehicle.Overview.AllDocuments"), () => SelectTab("Documents")));
            foreach (Due due in dues)
            {
                Grid row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel what = new StackPanel();
                what.Children.Add(new TextBlock { Text = due.Name, FontSize = 13, Foreground = VehicleCard.Res("TextMain"), TextWrapping = TextWrapping.Wrap });
                what.Children.Add(VehicleCard.Muted(due.Detail, 12));
                row.Children.Add(what);
                TextBlock when = new TextBlock
                {
                    Text = due.When,
                    FontSize = 13,
                    Foreground = Expiry.Brush(due.Level),
                    FontWeight = Expiry.Weight(due.Level),
                    Margin = new Thickness(16, 0, 0, 0)
                };
                Grid.SetColumn(when, 1);
                row.Children.Add(when);
                Button open = new Button { Template = PictureBrowser.FlatTemplate, Cursor = Cursors.Hand, Content = row, Margin = new Thickness(0, 0, 0, 8),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(open, due.Name + ", " + due.When);
                open.Click += (s, e) => SelectTab(due.Tab);
                card.Body.Children.Add(open);
            }
            return card;
        }

        // The reading, when it was taken, Update odometer, and the readings before it.
        private VehicleCard OdometerCard()
        {
            VehicleCard card = new VehicleCard(VehicleSchema.OdometerSection.Label);
            // Newest first; two of a day, the later one entered first.
            LogDefinition log = VehicleSchema.Log(VehicleSchema.OdometerLog);
            List<Dictionary<string, string>> readings = EntityTypes.Sorted(log, Enumerable.Reverse(EntityTypes.Rows(vehicle, log))).ToList();
            if (vehicle.OdometerKm is not int km)
            {
                card.ShowEmpty(Loc.T("Vehicle.Odometer.None"), Loc.T("Vehicle.Odometer.Update"), UpdateOdometer);
                return card;
            }
            card.Actions.Children.Add(VehicleCard.Link(Loc.T("Vehicle.Odometer.Update"), UpdateOdometer));
            card.Body.Children.Add(new TextBlock { Text = Units.Format(km, Units.Distance, vehicle), FontSize = 22, FontWeight = FontWeights.Bold, Foreground = VehicleCard.Res("TextMain") });
            foreach (Dictionary<string, string> reading in readings.Take(5))
            {
                string date = Values.ParseDate(reading.GetValueOrDefault("Date"))?.ToString(Loc.T("Format.LongDate"), Loc.Culture) ?? "";
                string value = Values.Number(reading.GetValueOrDefault("Reading")) is double n ? Units.Format(n, Units.Distance, vehicle) : "";
                card.Body.Children.Add(VehicleCard.Muted(string.Join(" · ", new[] { date, value }.Where(t => t.Length > 0)), 12));
            }
            return card;
        }

        private VehicleCard KeySpecsCard()
        {
            VehicleCard card = new VehicleCard(Loc.T("Vehicle.Overview.KeySpecs"));
            List<UIElement> cells = KeySpecs.Select(Field)
                .Select(f => (Field: f, Text: f.Show(f.Read(vehicle), vehicle)))
                .Where(x => x.Text.Length > 0)
                .Select(x => (UIElement)VehicleCard.Cell(x.Field.Label, TextCopy.Selectable(x.Text, 13, VehicleCard.Res("TextMain"))))
                .ToList();
            if (cells.Count == 0)
            {
                card.ShowEmpty(Loc.T("Vehicle.Overview.NoSpecs"), Loc.T("Vehicle.Overview.AddSpecs"), () => SelectTab("Specs"));
                return card;
            }
            card.Actions.Children.Add(VehicleCard.Link(Loc.T("Vehicle.Overview.AllSpecs"), () => SelectTab("Specs")));
            CellPanel panel = new CellPanel { MinColumn = 150, RowGap = 10 };
            foreach (UIElement cell in cells)
                panel.Children.Add(cell);
            card.Body.Children.Add(panel);
            return card;
        }

        // The newest service in the log, what it cost and when the next one is due.
        private VehicleCard LastServiceCard()
        {
            VehicleCard card = new VehicleCard(Loc.T("Vehicle.Overview.LastService"));
            RecordDefinition log = VehicleSchema.ServiceLog;
            EntityRecord? last = log.Read(vehicle).Where(e => (e.Get(VehicleService.TypeKey) ?? VehicleService.Types[0]) == VehicleService.Types[0])
                .OrderByDescending(e => VehicleService.DateOf(e) ?? DateTime.MinValue).FirstOrDefault();
            if (last == null)
            {
                card.ShowEmpty(Loc.T("Vehicle.Overview.NoService"), log.AddLabel, () => { SelectTab("Service"); StartAdd(log, null); });
                return card;
            }
            card.Actions.Children.Add(VehicleCard.Link(log.Label, () => SelectTab("Service")));
            card.Body.Children.Add(TextCopy.Selectable(log.Line(last, vehicle), 13, VehicleCard.Res("TextMain")));
            return card;
        }

        // ---- Sections, records, logs -------------------------------------------------------------

        private static FieldDefinition Field(string key) =>
            VehicleSchema.Tabs.SelectMany(t => t.Sections).SelectMany(s => s.Fields).First(f => f.Key == key);

        private SectionCard SectionCardOf(SectionDefinition section) =>
            new SectionCard(section.Label, section.Fields, vehicle, f => f.Read(vehicle), null, Gate,
                editors => Task.FromResult(Commit(v =>
                {
                    foreach (FieldEditor editor in editors)
                        editor.Field.Write(v, editor.Value);
                })));

        private VehicleCard LogCardOf(LogDefinition log)
        {
            LogCard card = new LogCard(log, EntityTypes.Rows(vehicle, log), Gate, rows => Commit(v =>
            {
                if (rows.Count > 0)
                    (v.Logs ??= new Dictionary<string, List<Dictionary<string, string>>>())[log.Key] = rows;
                else if (v.Logs != null && v.Logs.Remove(log.Key) && v.Logs.Count == 0)
                    v.Logs = null;
            }));
            CellPanel.SetWide(card, true);
            return card;
        }

        // A flat list: its heading with the Add button, then a card per record (or what the list is for). A nested one: its
        // outline card (Add in its corner) beside the selected record's card.
        private IEnumerable<UIElement> RecordCards(RecordDefinition records)
        {
            List<EntityRecord> list = records.Read(vehicle);
            if (records.Nested)
            {
                foreach (UIElement card in OutlineCards(records, list))
                    yield return card;
                yield break;
            }

            Grid heading = new Grid { Margin = new Thickness(2, 0, 0, 0) };
            heading.Children.Add(new TextBlock
            {
                Text = records.Label,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = VehicleCard.Res("TextMain"),
                VerticalAlignment = VerticalAlignment.Center
            });
            Button add = new Button { Content = "+  " + records.AddLabel, Height = 34, Padding = new Thickness(14, 0, 14, 0), HorizontalAlignment = HorizontalAlignment.Right };
            add.Click += (s, e) => StartAdd(records, null);
            heading.Children.Add(add);
            CellPanel.SetWide(heading, true);
            yield return heading;

            if (list.Count == 0 && adding?.Records != records)
            {
                VehicleCard empty = new VehicleCard(records.Label);
                CellPanel.SetWide(empty, true);
                empty.Body.Children.Add(VehicleCard.Muted(Loc.T("Entity.Records." + records.Key + ".Empty")));
                yield return empty;
            }
            if (records.TotalKey != null && TotalsCard(records, list) is VehicleCard totals)
                yield return totals;

            // Newest first, and only the chosen kind (FilterKey) when one is chosen.
            IEnumerable<EntityRecord> shown = records.NewestFirstKey == null ? list
                : list.OrderByDescending(r => Values.ParseDate(r.Get(records.NewestFirstKey)) ?? DateTime.MinValue);
            if (records.FilterKey != null && list.Count > 0)
            {
                yield return FilterRow(records, list);
                if (filters.GetValueOrDefault(records.Key) is string kind)
                {
                    shown = shown.Where(r => r.Get(records.FilterKey) == kind);
                    if (!shown.Any())
                    {
                        TextBlock none = VehicleCard.Muted(Loc.T("Entity.Records.NoMatch"));
                        CellPanel.SetWide(none, true);
                        yield return none;
                    }
                }
            }
            if (adding is { } fresh && fresh.Records == records)
                yield return NewRecordCard(fresh.Records, fresh.Record, null, records.AddLabel);
            foreach (EntityRecord record in shown)
                yield return RecordCard(records, record, records.Title(record));
        }

        // "All" and each kind the list has (FilterKey's choices, in their order); picking one shows only those.
        private UIElement FilterRow(RecordDefinition records, List<EntityRecord> list)
        {
            FieldDefinition field = records.Fields.First(f => f.Key == records.FilterKey);
            HashSet<string> present = list.Select(r => r.Get(field.Key) ?? "").ToHashSet();
            string? chosen = filters.GetValueOrDefault(records.Key);
            WrapPanel row = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            CellPanel.SetWide(row, true);
            foreach ((string? kind, string label) in field.Choices.Where(present.Contains).Select(c => ((string?)c, field.ChoiceLabel(c)))
                         .Prepend((null, Loc.T("Entity.Filter.All"))))
            {
                RadioButton button = new RadioButton
                {
                    Content = label,
                    Style = (Style)FindResource("SegmentStyle"),
                    GroupName = "Filter" + records.Key,
                    Height = 28,
                    Margin = new Thickness(0, 0, 4, 4),
                    IsChecked = kind == chosen
                };
                button.Checked += (s, e) =>
                {
                    if (kind == filters.GetValueOrDefault(records.Key) || !LeaveEdit())
                        return;
                    if (kind == null)
                        filters.Remove(records.Key);
                    else
                        filters[records.Key] = kind;
                    adding = null;
                    ShowTab();
                };
                row.Children.Add(button);
            }
            return row;
        }

        // The sums of TotalKey per kind (FilterKey) and per year (NewestFirstKey's date), and in all; null with nothing to
        // add up.
        private VehicleCard? TotalsCard(RecordDefinition records, List<EntityRecord> list)
        {
            List<(EntityRecord Record, double Amount)> amounts = list.Select(r => (r, Values.Number(r.Get(records.TotalKey!))))
                .Where(x => x.Item2 != null).Select(x => (x.r, x.Item2!.Value)).ToList();
            if (amounts.Count == 0)
                return null;

            FieldDefinition? kinds = records.Fields.FirstOrDefault(f => f.Key == records.FilterKey);
            VehicleCard card = new VehicleCard(Loc.T("Entity.Costs.Title"));
            CellPanel.SetWide(card, true);
            CellPanel panel = new CellPanel { MinColumn = 220, RowGap = 12 };
            if (kinds != null)
                panel.Children.Add(Amounts(Loc.T("Entity.Costs.ByKind"), amounts.GroupBy(x => x.Record.Get(kinds.Key) ?? "")
                    .OrderBy(g => Array.IndexOf(kinds.Choices, g.Key)).Select(g => (kinds.ChoiceLabel(g.Key), g.Sum(x => x.Amount)))));
            if (records.NewestFirstKey != null)
                panel.Children.Add(Amounts(Loc.T("Entity.Costs.ByYear"), amounts.GroupBy(x => Values.ParseDate(x.Record.Get(records.NewestFirstKey))?.Year)
                    .OrderByDescending(g => g.Key ?? 0).Select(g => (g.Key?.ToString() ?? Loc.T("Entity.Costs.NoDate"), g.Sum(x => x.Amount)))));
            panel.Children.Add(Amounts(Loc.T("Entity.Costs.Total"), new[] { (records.Label, amounts.Sum(x => x.Amount)) }));
            card.Body.Children.Add(panel);
            return card;
        }

        // A heading over "label   amount" lines, the amounts right-aligned in the currency.
        private UIElement Amounts(string heading, IEnumerable<(string Label, double Amount)> lines)
        {
            Grid table = new Grid();
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            foreach ((string label, double amount) in lines)
            {
                int row = table.RowDefinitions.Count;
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBlock name = new TextBlock { Text = label, FontSize = 13, Foreground = VehicleCard.Res("TextMain"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 16, 4) };
                TextBox sum = TextCopy.Selectable(Units.Format(amount, Units.Money, vehicle), 13, VehicleCard.Res("TextMain"), FontWeights.SemiBold);
                sum.TextWrapping = TextWrapping.NoWrap;
                Grid.SetRow(name, row);
                Grid.SetRow(sum, row);
                Grid.SetColumn(sum, 1);
                table.Children.Add(name);
                table.Children.Add(sum);
            }
            return VehicleCard.Cell(heading, table);
        }

        // A nested list: the outline beside the selected record's card (the first one until another is picked). Empty, what
        // it is for and the usual records to start with (VehicleSchema.PartsTemplate).
        private IEnumerable<UIElement> OutlineCards(RecordDefinition records, List<EntityRecord> list)
        {
            VehicleCard outline = new VehicleCard(records.Label);
            outline.Actions.Children.Add(VehicleCard.Link("+ " + records.AddLabel, () => StartAdd(records, null)));
            CellPanel.SetStartsRow(outline, true);
            if (list.Count == 0 && adding?.Records != records)
            {
                CellPanel.SetWide(outline, true);
                outline.ShowEmpty(Loc.T("Entity.Records." + records.Key + ".Empty"), Loc.T("Entity.Records." + records.Key + ".Template"),
                    () => AddTemplate(records));
                yield return outline;
                yield break;
            }
            Guid? shown = adding?.Records == records ? null : RecordTree.Find(list, selectedPart ?? Guid.Empty)?.Id ?? list.FirstOrDefault()?.Id;
            outline.Body.Children.Add(new RecordOutline(records, list, shown, openParts, id =>
            {
                if (!LeaveEdit())
                    return;
                adding = null;
                selectedPart = id;
                ShowTab();
            }, id =>
            {
                if (!LeaveEdit())
                    return;
                if (!openParts.Remove(id))
                    openParts.Add(id);
                ShowTab();
            }));
            yield return outline;

            if (adding is { } fresh && fresh.Records == records)
            {
                EntityRecord? parent = fresh.Parent == null ? null : RecordTree.Find(list, fresh.Parent.Value);
                yield return NewRecordCard(records, fresh.Record, fresh.Parent,
                    parent == null ? records.AddLabel : Loc.F("Entity.Records." + records.Key + ".NewUnder", records.Title(parent)));
            }
            else if (shown != null)
            {
                (EntityRecord record, IReadOnlyList<EntityRecord> above) = RecordTree.Walk(list).First(x => x.Record.Id == shown);
                yield return RecordCard(records, record, string.Join(" › ", above.Append(record).Select(records.Title)),
                    WithHistory(records, record, Moves(records, list, record, above.Count)));
            }
        }

        private SectionCard RecordCard(RecordDefinition records, EntityRecord record, string title, UIElement? footer = null) =>
            new SectionCard(title, records.Fields, vehicle, f => record.Get(f.Key), record, Gate,
                editors => SaveRecord(records, record, null, isNew: false, editors))
            {
                Delete = () => DeleteRecord(records, record),
                Status = records.CurrentKey == null ? null
                    : records.IsCurrent(vehicle, record) ? (Loc.T("Entity.Records." + records.Key + ".Current"), null)
                    : (Loc.T("Entity.Records." + records.Key + ".MakeCurrent"), () => Commit(v => records.SetCurrent(v, record.Id))),
                Footer = footer,
                InHeading = records.TitleKeys,
                Tag = record.Id
            };

        // A record being added, in edit at once; Cancel takes it away again.
        private SectionCard NewRecordCard(RecordDefinition records, EntityRecord record, Guid? parent, string title)
        {
            SectionCard card = null!;
            card = new SectionCard(title, records.Fields, vehicle, f => record.Get(f.Key), record, Gate,
                editors => SaveRecord(records, record, parent, isNew: true, editors))
            {
                Cancelled = () => { adding = null; editing = null; Cards.Children.Remove(card); }
            };
            card.BeginEdit();
            return card;
        }

        // The records of other lists that point at this one (a part's services, RecordDefinition fields of the kind Links),
        // newest first, over `under`.
        private UIElement WithHistory(RecordDefinition records, EntityRecord record, UIElement under)
        {
            List<string> lines = EntityTypes.Records(vehicle.EntityType)
                .SelectMany(def => def.Fields.Where(f => f.Kind == ValueKind.Links && f.LinkTo == records.Key)
                    .SelectMany(f => def.Read(vehicle).Where(r => FieldDefinition.Items(r.Get(f.Key)).Contains(record.Id.ToString()))
                        .OrderByDescending(r => def.NewestFirstKey == null ? null : Values.ParseDate(r.Get(def.NewestFirstKey)))
                        .Select(r => def.Line(r, vehicle))))
                .ToList();
            if (lines.Count == 0)
                return under;
            StackPanel footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            footer.Children.Add(new TextBlock { Text = Loc.T("Entity.Records." + records.Key + ".History"), FontSize = 11,
                Foreground = VehicleCard.Res("TextSub"), Margin = new Thickness(0, 0, 0, 4) });
            foreach (string line in lines)
            {
                TextBox text = TextCopy.Selectable(line, 13, VehicleCard.Res("TextMain"));
                text.Margin = new Thickness(0, 0, 0, 4);
                footer.Children.Add(text);
            }
            footer.Children.Add(under);
            return footer;
        }

        // Under a nested record's fields: a record under it, and moving it up, down, into the one above or out a level.
        private UIElement Moves(RecordDefinition records, List<EntityRecord> list, EntityRecord record, int depth)
        {
            List<EntityRecord> holder = RecordTree.Holder(list, record.Id)!;
            int at = holder.IndexOf(record);
            WrapPanel moves = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
            void Add(string key, bool can, Action act)
            {
                Button button = new Button
                {
                    Content = Loc.T("Entity.Records." + records.Key + "." + key),
                    Style = (Style)FindResource("GhostButtonStyle"),
                    Margin = new Thickness(0, 0, 8, 6),
                    IsEnabled = can,
                    Opacity = can ? 1 : 0.45 // the ghost style has no look of its own for "off"
                };
                button.Click += (s, e) => act();
                moves.Children.Add(button);
            }
            Add("AddUnder", depth + 1 < RecordTree.MaxDepth, () => StartAdd(records, record.Id));
            Add("Up", at > 0, () => Move(records, list => RecordTree.Move(list, record.Id, -1)));
            Add("Down", at < holder.Count - 1, () => Move(records, list => RecordTree.Move(list, record.Id, +1)));
            Add("In", RecordTree.CanIndent(list, record.Id), () => Move(records, list => RecordTree.Indent(list, record.Id)));
            Add("Out", holder != list, () => Move(records, list => RecordTree.Outdent(list, record.Id)));
            return moves;
        }

        private void Move(RecordDefinition records, Func<List<EntityRecord>, bool> move)
        {
            if (!LeaveEdit())
                return;
            Commit(v =>
            {
                List<EntityRecord> list = records.Read(v);
                if (move(list))
                    records.Write(v, list);
            });
        }

        // The usual systems as top-level records, named in the current language (the user's own from then on).
        private void AddTemplate(RecordDefinition records)
        {
            if (!LeaveEdit())
                return;
            Commit(v => records.Write(v, VehicleSchema.PartsTemplate.Select(key =>
            {
                EntityRecord system = records.New(vehicle);
                system.Set("Name", Loc.T("Entity.Parts.Template." + key));
                return system;
            }).ToList()));
        }

        private void StartAdd(RecordDefinition records, Guid? parent)
        {
            if (!LeaveEdit())
                return;
            adding = (records, records.New(vehicle), parent);
            if (parent != null)
                openParts.Add(parent.Value);
            ShowTab();
            ScrollToTabs();
        }

        // The record's fields from the card. A picked file and picked pictures are copied in first (and removed again if
        // the save fails); what it replaces or loses is deleted once the vehicle is saved without it (the collection
        // view). A new record goes at the end of its list, or under `parent`, and is the one shown after.
        private async Task<bool> SaveRecord(RecordDefinition records, EntityRecord record, Guid? parent, bool isNew, IReadOnlyList<FieldEditor> editors)
        {
            FieldEditor? fileEditor = editors.FirstOrDefault(e => e.Field.Kind == ValueKind.File);
            FieldEditor? picturesEditor = editors.FirstOrDefault(e => e.Field.Kind == ValueKind.Pictures);
            FieldEditor? specsEditor = editors.FirstOrDefault(e => e.Field.Kind == ValueKind.Specs);
            Attachment? copied = null;
            if (fileEditor?.PickedFile != null)
            {
                copied = await AttachmentStore.ImportAsync(fileEditor.PickedFile, null, CancellationToken.None);
                if (copied == null)
                {
                    Msg.Show(this, Loc.F("Entity.File.CouldntCopy", System.IO.Path.GetFileName(fileEditor.PickedFile), AttachmentStore.Folder),
                        Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }
            List<string> imported = new List<string>();
            List<string>? pictures = null;
            if (picturesEditor != null)
            {
                pictures = EntityImages.Import(picturesEditor.PictureSlots, out imported, out List<string> failed);
                if (failed.Count > 0)
                    Msg.Show(this, Loc.F("Collection.PictureFailed", string.Join(", ", failed.Select(System.IO.Path.GetFileName)), EntityImages.Store.Folder),
                        Loc.T("Collection.PictureFailed.Caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // A new one stops being "being added", and becomes the one shown, before the window is drawn again.
            var wasAdding = adding;
            Guid? wasSelected = selectedPart;
            if (isNew)
            {
                adding = null;
                if (records.Nested)
                    selectedPart = record.Id;
            }
            bool saved = Commit(v =>
            {
                List<EntityRecord> list = records.Read(v);
                EntityRecord target = isNew ? record : RecordTree.Find(list, record.Id)!;
                foreach (FieldEditor editor in editors.Where(e => e != fileEditor && e != picturesEditor && e != specsEditor))
                    target.Set(editor.Field.Key, editor.Value);
                if (copied != null)
                    target.File = copied;
                else if (fileEditor?.FileRemoved == true)
                    target.File = null;
                if (pictures != null)
                    target.Pictures = pictures.Count > 0 ? pictures : null;
                if (specsEditor != null)
                    target.Specs = specsEditor.Specs.Count > 0 ? specsEditor.Specs : null;
                if (isNew)
                    (parent == null ? list : RecordTree.ChildrenOf(RecordTree.Find(list, parent.Value)!)).Add(target);
                records.Write(v, list);
                records.Saved?.Invoke(v, target);
            });
            if (!saved)
            {
                adding = wasAdding;
                selectedPart = wasSelected;
                if (copied != null)
                    AttachmentStore.Delete(new[] { copied });
                foreach (string name in imported)
                    EntityImages.DeleteCopy(name);
            }
            return saved;
        }

        // Asked first; a record holding others says how many go with it. The one in use (CurrentKey) is no longer.
        private void DeleteRecord(RecordDefinition records, EntityRecord record)
        {
            int under = RecordTree.CountUnder(record);
            string question = under == 0 ? Loc.F("Common.DeleteNamed", records.Title(record))
                : Loc.Plural("Entity.Records." + records.Key + ".DeleteWithChildren", under, records.Title(record));
            if (!LeaveEdit() || Msg.Show(this, question, Loc.T("Common.ConfirmDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            Commit(v =>
            {
                List<EntityRecord> list = records.Read(v);
                RecordTree.Holder(list, record.Id)?.RemoveAll(r => r?.Id == record.Id);
                foreach (EntityRecord emptied in RecordTree.Walk(list).Select(x => x.Record).Where(r => r.Children?.Count == 0))
                    emptied.Children = null;
                if (records.IsCurrent(v, record))
                    records.SetCurrent(v, null);
                records.Write(v, list);
            });
        }

        // ---- Editing and saving ------------------------------------------------------------------

        // One card is edited at a time: another one starting asks the one being edited to stop first.
        private bool Gate(VehicleCard card)
        {
            if (editing != null && editing != card && !LeaveEdit())
                return false;
            editing = card;
            return true;
        }

        // Stops the edit going on (asking first if something was typed); false if the user wants to go on with it.
        private bool LeaveEdit()
        {
            if (editing == null || !editing.IsEditing)
                return true;
            if (!editing.ConfirmLeave())
                return false;
            VehicleCard left = editing;
            editing = null;
            left.CancelEdit();
            return true;
        }

        // The change, made on a copy (fields this version doesn't know come along), saved by the collection view; then
        // everything is drawn from it. False if it couldn't be saved: the window still shows what is stored.
        private bool Commit(Action<Entity> change)
        {
            Entity edited = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(vehicle))!;
            change(edited);
            if (!host.Save(edited))
                return false;
            vehicle = edited;
            Redraw();
            return true;
        }

        // ---- Header actions ------------------------------------------------------------------------

        private void UpdateOdometer_Click(object sender, RoutedEventArgs e) => UpdateOdometer();

        // A new reading: stored as the current one and added to the readings (dated today). One lower than the last
        // is asked about first (a typo, or a new instrument cluster).
        private void UpdateOdometer()
        {
            if (!LeaveEdit())
                return;
            string unit = Units.Label(Units.Distance, vehicle);
            string typed = vehicle.OdometerKm?.ToString(Loc.Culture) ?? "";
            string? stored;
            while (true)
            {
                string? answer = InputDialog.Prompt(this, Loc.T("Vehicle.Odometer.Update"), Loc.F("Vehicle.Odometer.Prompt", unit), typed, Loc.T("Common.Save"));
                if (answer == null)
                    return;
                stored = Values.Normalize(ValueKind.Number, answer);
                if (!string.IsNullOrEmpty(stored) && Values.Number(stored) >= 0)
                    break;
                Msg.Show(this, Loc.F("Entity.NeedsNumber", VehicleSchema.Odometer.Label), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                typed = answer;
            }

            if (vehicle.OdometerKm is int now && Values.Number(stored) < now
                && Msg.Show(this, Loc.F("Vehicle.Odometer.Lower", Units.Format(now, Units.Distance, vehicle)), Loc.T("Vehicle.Odometer.Update"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            Commit(v =>
            {
                VehicleSchema.Odometer.Write(v, stored);
                v.Logs ??= new Dictionary<string, List<Dictionary<string, string>>>();
                if (!v.Logs.TryGetValue(VehicleSchema.OdometerLog, out List<Dictionary<string, string>>? readings) || readings == null)
                    v.Logs[VehicleSchema.OdometerLog] = readings = new List<Dictionary<string, string>>();
                readings.Add(new Dictionary<string, string> { ["Date"] = Values.FromDate(DateTime.Today)!, ["Reading"] = stored });
            });
        }

        private void EditBasics_Click(object sender, RoutedEventArgs e)
        {
            if (!LeaveEdit())
                return;
            if (host.EditBasics(vehicle) is Entity saved)
            {
                vehicle = saved;
                Redraw();
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (LeaveEdit() && host.Delete(vehicle))
                Close();
        }

        // ---- Keys and closing ----------------------------------------------------------------------

        // Left and Right browse the pictures unless the keyboard is in a box or list that uses them.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if ((e.Key == Key.Left || e.Key == Key.Right) && Keyboard.Modifiers == ModifierKeys.None
                && !(e.OriginalSource is TextBoxBase) && !InList(e.OriginalSource as DependencyObject)
                && gallery.Browse(e.Key == Key.Left ? -1 : +1))
                e.Handled = true;
        }

        private static bool InList(DependencyObject? source)
        {
            for (DependencyObject? at = source; at != null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
                if (at is ComboBox || at is DatePicker || at is RadioButton)
                    return true;
            return false;
        }

        // Ctrl+F goes to the search box. Esc empties the search (back to the tab), or stops an edit (asking first if something was typed),
        // or closes. An open dropdown takes its own Esc first.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
            {
                SearchBox.Focus();
                SearchBox.SelectAll();
                e.Handled = true;
            }
            if (e.Key != Key.Escape || e.Handled)
                return;
            e.Handled = true;
            if (SearchBox.Text.Length > 0)
                SearchBox.Clear();
            else if (editing != null && editing.IsEditing)
                LeaveEdit();
            else
                Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!LeaveEdit())
            {
                e.Cancel = true;
                return;
            }
            Rect size = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            AppSettings.Update(s =>
            {
                s.VehicleWindowWidth = Math.Round(size.Width);
                s.VehicleWindowHeight = Math.Round(size.Height);
            });
            base.OnClosing(e);
        }
    }
}
