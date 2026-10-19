using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

        private const string OverviewKey = "Overview";
        private const double WideHeader = 860;

        // The key facts the Overview lists (from the Specs sections).
        private static readonly string[] KeySpecs = { "Make", "Model", "Edition", "Year", "BodyType", "Fuel", "Displacement", "Power", "Gearbox", "Drive", "Vin" };

        private Entity vehicle;
        private readonly Host host;
        private readonly PictureBrowser gallery = new PictureBrowser(double.NaN, 80, canReorder: false, stripForOne: false);
        private string tab = OverviewKey;
        private VehicleCard? editing;
        private (RecordDefinition Records, EntityRecord Record)? adding;

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
            foreach (Deadline deadline in EntityTypes.Expiries(vehicle).Take(3))
                Badges.Children.Add(Badge(deadline));

            ShowPictures(gallery.Selected);
        }

        // "Inspection in 12 days" in its urgency's color and weight (Expiry), never by color alone.
        private static Border Badge(Deadline deadline)
        {
            ExpiryLevel level = Expiry.Level(deadline.Date, DateTime.Today);
            return new Border
            {
                Background = VehicleCard.Res("BgSubtle"),
                BorderBrush = VehicleCard.Res(level == ExpiryLevel.Later ? "Border" : level == ExpiryLevel.Expired ? "Danger" : "BtnHover"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(10, 3, 10, 4),
                Margin = new Thickness(0, 0, 8, 6),
                ToolTip = deadline.Date.ToString(Loc.T("Format.LongDate"), Loc.Culture),
                Child = new TextBlock
                {
                    Text = Loc.F("Entity.ExpiryLine", deadline.Name, Expiry.Describe(deadline.Date, DateTime.Today)),
                    FontSize = 12,
                    Foreground = Expiry.Brush(level),
                    FontWeight = Expiry.Weight(level)
                }
            };
        }

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
            if (tab == OverviewKey)
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

        // ---- Overview ----------------------------------------------------------------------------

        private IEnumerable<UIElement> OverviewCards()
        {
            yield return DeadlinesCard();
            yield return OdometerCard();
            yield return KeySpecsCard();
            yield return LastServiceCard();
            yield return SectionCardOf(VehicleSchema.Notes);
        }

        // Every dated document, soonest first, with how close it is.
        private VehicleCard DeadlinesCard()
        {
            VehicleCard card = new VehicleCard(Loc.T("Vehicle.Overview.Deadlines"));
            List<Deadline> deadlines = EntityTypes.Expiries(vehicle);
            if (deadlines.Count == 0)
            {
                card.ShowEmpty(Loc.T("Vehicle.Overview.NoDeadlines"), VehicleSchema.Documents.AddLabel, () => { SelectTab("Documents"); StartAdd(VehicleSchema.Documents); });
                return card;
            }
            card.Actions.Children.Add(VehicleCard.Link(Loc.T("Vehicle.Overview.AllDocuments"), () => SelectTab("Documents")));
            foreach (Deadline deadline in deadlines)
            {
                ExpiryLevel level = Expiry.Level(deadline.Date, DateTime.Today);
                Grid row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel what = new StackPanel();
                what.Children.Add(new TextBlock { Text = deadline.Name, FontSize = 13, Foreground = VehicleCard.Res("TextMain"), TextWrapping = TextWrapping.Wrap });
                what.Children.Add(VehicleCard.Muted(deadline.Date.ToString(Loc.T("Format.LongDate"), Loc.Culture), 12));
                row.Children.Add(what);
                TextBlock when = new TextBlock
                {
                    Text = Expiry.Describe(deadline.Date, DateTime.Today),
                    FontSize = 13,
                    Foreground = Expiry.Brush(level),
                    FontWeight = Expiry.Weight(level),
                    Margin = new Thickness(16, 0, 0, 0)
                };
                Grid.SetColumn(when, 1);
                row.Children.Add(when);
                card.Body.Children.Add(row);
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

        private VehicleCard LastServiceCard()
        {
            VehicleCard card = new VehicleCard(Loc.T("Vehicle.Overview.LastService"));
            LogDefinition service = VehicleSchema.Log("Service");
            Dictionary<string, string>? last = EntityTypes.Sorted(service, EntityTypes.Rows(vehicle, service)).FirstOrDefault();
            if (last == null)
            {
                card.ShowEmpty(Loc.T("Vehicle.Overview.NoService"), Loc.T("Log.AddRow"), () =>
                {
                    SelectTab("Service");
                    Cards.Children.OfType<LogCard>().First(c => c.Log == service).BeginEdit();
                });
                return card;
            }
            card.Actions.Children.Add(VehicleCard.Link(service.Label, () => SelectTab("Service")));
            card.Body.Children.Add(TextCopy.Selectable(EntityTypes.FormatRow(service, last), 13, VehicleCard.Res("TextMain")));
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

        // The list's heading with its Add button, a new record being added, then a card per record (or, with none,
        // what the list is for).
        private IEnumerable<UIElement> RecordCards(RecordDefinition records)
        {
            List<EntityRecord> list = records.Read(vehicle);
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
            add.Click += (s, e) => StartAdd(records);
            heading.Children.Add(add);
            CellPanel.SetWide(heading, true);
            yield return heading;

            if (adding is (RecordDefinition addingTo, EntityRecord fresh) && addingTo == records)
            {
                SectionCard card = RecordCard(records, fresh, isNew: true);
                card.BeginEdit();
                yield return card;
            }
            if (list.Count == 0)
            {
                VehicleCard empty = new VehicleCard(records.Label);
                empty.Body.Children.Add(VehicleCard.Muted(Loc.T("Entity.Records." + records.Key + ".Empty")));
                CellPanel.SetWide(empty, true);
                yield return empty;
            }
            foreach (EntityRecord record in list)
                yield return RecordCard(records, record, isNew: false);
        }

        private SectionCard RecordCard(RecordDefinition records, EntityRecord record, bool isNew)
        {
            SectionCard card = null!;
            card = new SectionCard(isNew ? records.AddLabel : records.Title(record), records.Fields, vehicle, f => record.Get(f.Key), record.File, Gate,
                editors => SaveRecord(records, record, isNew, editors))
            {
                Delete = isNew ? null : () => DeleteRecord(records, record),
                Cancelled = isNew ? () => { adding = null; editing = null; Cards.Children.Remove(card); } : null
            };
            return card;
        }

        private void StartAdd(RecordDefinition records)
        {
            if (!LeaveEdit())
                return;
            adding = (records, records.New());
            ShowTab();
            ScrollToTabs();
        }

        // The record's fields from the card; a picked file is copied in first (and removed again if the save fails).
        // The file it replaces or loses is deleted once the vehicle is saved without it (the collection view).
        private async Task<bool> SaveRecord(RecordDefinition records, EntityRecord record, bool isNew, IReadOnlyList<FieldEditor> editors)
        {
            FieldEditor? fileEditor = editors.FirstOrDefault(e => e.Field.Kind == ValueKind.File);
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

            // A new one stops being "being added" before the window is drawn again from the saved vehicle.
            var wasAdding = adding;
            if (isNew)
                adding = null;
            bool saved = Commit(v =>
            {
                List<EntityRecord> list = records.Read(v);
                EntityRecord target = isNew ? record : list.First(r => r.Id == record.Id);
                foreach (FieldEditor editor in editors.Where(e => e != fileEditor))
                    target.Set(editor.Field.Key, editor.Value);
                if (copied != null)
                    target.File = copied;
                else if (fileEditor?.FileRemoved == true)
                    target.File = null;
                if (isNew)
                    list.Add(target);
                records.Write(v, list);
            });
            if (!saved)
            {
                adding = wasAdding;
                if (copied != null)
                    AttachmentStore.Delete(new[] { copied });
            }
            return saved;
        }

        private void DeleteRecord(RecordDefinition records, EntityRecord record)
        {
            if (!LeaveEdit() || Msg.Show(this, Loc.F("Common.DeleteNamed", records.Title(record)), Loc.T("Common.ConfirmDelete"),
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
            Commit(v =>
            {
                List<EntityRecord> list = records.Read(v);
                list.RemoveAll(r => r.Id == record.Id);
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

        // Esc stops an edit (asking first if something was typed), else closes. An open dropdown takes its own Esc first.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled)
                return;
            e.Handled = true;
            if (editing != null && editing.IsEditing)
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
