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
    // A collection item's own window (opened from its card), the same for every type: its pictures large on top, a
    // title block (what it is, its name, a line made of its main fields, its key facts and what comes due as chips),
    // then its sections. What it shows comes from the type's definition (EntityTypes: tabs, sections, record lists,
    // logs, EntityTypes.Headline); a type with one tab is a single page, one with several gets an Overview and a list
    // of pages beside the content (a dropdown above it when the window is narrow). Fields are edited in place, one
    // section at a time; a record or a log in a dialog. Every change is saved at once through the collection view
    // (Host.Save). Esc stops an edit, empties the search, else closes; Left and Right browse the pictures. Its size is
    // remembered.
    public partial class ItemWindow : Window
    {
        // What the collection view does for it. Save: stores the edited copy, false if it couldn't (the user was told).
        // EditBasics: the item form for the name and pictures, the item as saved or null. Delete: true once it's gone.
        // Reread: after an undo, the item as stored now (the view reads its items again), null if it's gone.
        public sealed class Host
        {
            public Func<Entity, bool> Save { get; init; } = _ => false;
            public Func<Entity, Entity?> EditBasics { get; init; } = _ => null;
            public Func<Entity, bool> Delete { get; init; } = _ => false;
            public Func<Guid, Entity?> Reread { get; init; } = _ => null;
        }

        private const string OverviewKey = ItemSearch.OverviewTab;
        private const double WideBody = 860;
        private const double WideTitle = 760;
        private const double PageTitleHeight = 40; // the page's title line is this tall with or without an Add beside it
        private const double ColumnBottom = 32;    // the space under the sections (the column's bottom margin)

        private Entity item;
        private readonly Host host;
        private readonly PictureHero hero = new PictureHero();
        private string page;
        private IInlineEdit? editing;

        // The - or + of a compact list that was pressed: the page is drawn again from what was saved, and the keyboard
        // goes back to the same button.
        private (Guid Record, bool Plus)? refocus;

        // Kept across redraws: the record row that is open, the kind each list shows (FilterKey), and in a nested list
        // (parts) which records show the ones under them.
        private Guid? openRecord;
        // What the page title carries on its right (a page that is one list: its Add), set while the page is built.
        private UIElement? titleAction;
        private readonly Dictionary<string, string> filters = new Dictionary<string, string>();
        private readonly HashSet<Guid> openParts = new HashSet<Guid>();

        // A custom item: the line of a checklist block (or, Guid.Empty, its add box) that had the keyboard when the page
        // was drawn again; and the gallery block's pictures, which Left and Right browse in place of the ones on top.
        private (string Block, Guid Line)? keepLine;
        private PictureBrowser? gallery;
        private int galleryIndex;

        private CustomType? Custom => CustomTypes.Of(item.EntityType);

        public ItemWindow(Entity item, Host host)
        {
            InitializeComponent();
            this.item = item;
            this.host = host;
            Hero.Content = hero;
            hero.Picked += ShowPictures;
            page = Pages()[0].Key;

            AppSettings settings = AppSettings.Load();
            Width = Math.Min(settings.ItemWindowWidth ?? 1100, SystemParameters.WorkArea.Width - 40);
            Height = Math.Min(settings.ItemWindowHeight ?? 820, SystemParameters.WorkArea.Height - 40);
            SizeChanged += (s, e) => Layout();

            Loc.Track(this, Redraw);
            Redraw();

            // Ctrl+Z here undoes this item's own changes (each save is a step of the collection page), not while a section
            // is being edited (its boxes have their own undo).
            UndoHost.Attach(this, () => new[] { UndoService.Page }, (history, step) => AfterUndo(),
                accepts: step => step.Target == this.item.Id, ready: () => editing == null || !editing.IsEditing);
        }

        // After an undo or redo: the item as stored now. If it is gone, so is the window.
        private void AfterUndo()
        {
            if (host.Reread(item.Id) is Entity stored)
            {
                item = stored;
                Redraw();
            }
            else
                Close();
        }

        // ---- Pages ---------------------------------------------------------------------------------

        // The type's tabs; with more than one, an Overview first. A type without a definition: one page of its notes.
        private List<(string Key, string Label)> Pages()
        {
            IReadOnlyList<TabDefinition> tabs = EntityTypes.Tabs(item.EntityType);
            List<(string, string)> pages = tabs.Select(t => (t.Key, t.Label)).ToList();
            return tabs.Count > 1 ? pages.Prepend((OverviewKey, Loc.T("Entity.Tab.Overview"))).ToList()
                : pages.Count == 1 ? pages : new List<(string, string)> { (OverviewKey, Loc.T("Entity.Tab.Overview")) };
        }

        private bool SinglePage => Pages().Count == 1;

        private void Redraw()
        {
            Title = item.Name;
            ShowHeader();
            ShowNav();
            ShowPage();
            Layout();
        }

        // Wide: the page list beside the content. Narrow: a dropdown of the pages above it. One page: neither, no search.
        private void Layout()
        {
            bool wide = ActualWidth >= WideBody;
            Nav.Visibility = SinglePage ? Visibility.Collapsed : Visibility.Visible;
            bool beside = wide && !SinglePage;
            NavColumn.Width = beside ? new GridLength(208) : new GridLength(0);
            GapColumn.Width = beside ? new GridLength(24) : new GridLength(0);
            Grid.SetColumnSpan(Nav, beside ? 1 : 3);
            Grid.SetRow(Page, beside ? 0 : 1);
            Grid.SetColumn(Page, beside ? 2 : 0);
            Grid.SetColumnSpan(Page, beside ? 1 : 3);
            Page.Margin = new Thickness(0, beside || SinglePage ? 0 : 16, 0, 0);
            SectionList.Visibility = beside ? Visibility.Visible : Visibility.Collapsed;
            SectionPicker.Visibility = beside ? Visibility.Collapsed : Visibility.Visible;

            // Switching pages must change the content and nothing else. The page is one scrolling column, so a shorter
            // page would shrink what there is to scroll and the scroll offset would be cut back to fit: everything above
            // (pictures, title block, page list) would jump down. So with several pages the content is never shorter than
            // the window (the page list can always stay where it is, at any offset up to its own top), and the scroll
            // bar is always there, so its coming and going never moves the column sideways.
            PageScroll.VerticalScrollBarVisibility = SinglePage ? ScrollBarVisibility.Auto : ScrollBarVisibility.Visible;
            Page.MinHeight = SinglePage ? 0 : Math.Max(0, PageScroll.ActualHeight - ColumnBottom);

            // The title block's actions: on the right, or under it on its own row when there isn't room for both.
            bool actionsBeside = ActualWidth >= WideTitle;
            Grid.SetRow(Actions, actionsBeside ? 0 : 1);
            Grid.SetColumn(Actions, actionsBeside ? 1 : 0);
            Actions.HorizontalAlignment = actionsBeside ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            Actions.Margin = actionsBeside ? new Thickness(24, 4, -8, 0) : new Thickness(0, 16, 0, 0);

            // The pictures: as large as the window leaves room for, never more than a third of its height or so.
            hero.Height = Math.Clamp(Math.Min(ActualWidth * 0.3, ActualHeight * 0.4), 200, 380);
            Dispatcher.BeginInvoke(new Action(() =>
                hero.Inset = Column.TranslatePoint(new Point(0, 0), (UIElement)PageScroll.Content).X), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void ShowNav()
        {
            SectionList.SelectionChanged -= SectionList_SelectionChanged;
            SectionPicker.SelectionChanged -= SectionPicker_SelectionChanged;
            SectionList.Items.Clear();
            SectionPicker.Items.Clear();
            foreach ((string key, string label) in Pages())
            {
                bool shown = key == page && SearchBox.Text.Length == 0;
                SectionList.Items.Add(new ListBoxItem { Content = label, Tag = key, IsSelected = shown });
                SectionPicker.Items.Add(new ComboBoxItem { Content = label, Tag = key, IsSelected = shown });
            }
            SectionList.SelectionChanged += SectionList_SelectionChanged;
            SectionPicker.SelectionChanged += SectionPicker_SelectionChanged;
        }

        // The list and the dropdown both on the page shown (none while searching), without their events.
        private void MarkNav()
        {
            SectionList.SelectionChanged -= SectionList_SelectionChanged;
            SectionPicker.SelectionChanged -= SectionPicker_SelectionChanged;
            bool searching = SearchBox.Text.Length > 0;
            SectionList.SelectedItem = searching ? null : SectionList.Items.OfType<ListBoxItem>().FirstOrDefault(i => (string)i.Tag == page);
            SectionPicker.SelectedItem = searching ? null : SectionPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == page);
            SectionList.SelectionChanged += SectionList_SelectionChanged;
            SectionPicker.SelectionChanged += SectionPicker_SelectionChanged;
        }

        private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SectionList.SelectedItem is ListBoxItem chosen)
                PickPage((string)chosen.Tag);
        }

        private void SectionPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SectionPicker.SelectedItem is ComboBoxItem chosen)
                PickPage((string)chosen.Tag);
        }

        private void PickPage(string key)
        {
            if (key == page && SearchBox.Text.Length == 0)
                return;
            if (!LeaveEdit())
            {
                MarkNav(); // back to the page being edited
                return;
            }
            page = key;
            openRecord = null;
            SearchBox.Text = ""; // draws the page
            MarkNav();
            ShowPage();
            ScrollToBody();
        }

        public void SelectPage(string key) => PickPage(key);

        // The scroll offset after a switch: unchanged while the page list is in view (it and everything above it stay
        // where they are on the screen, only the content below changes); if the window was scrolled further down, up to
        // where the page list starts, so the new page begins right under it.
        private void ScrollToBody()
        {
            PageScroll.UpdateLayout();
            double top = Body.TranslatePoint(new Point(0, 0), (UIElement)PageScroll.Content).Y;
            if (PageScroll.VerticalOffset > top)
                PageScroll.ScrollToVerticalOffset(top);
        }

        // ---- Header ---------------------------------------------------------------------------------

        private void ShowHeader()
        {
            ItemHeadline headline = EntityTypes.Headline(item.EntityType);
            FieldDefinition? Field(string key) => EntityTypes.Fields(item.EntityType).FirstOrDefault(f => f.Key == key);
            string Shown(string key) => Field(key) is FieldDefinition f ? f.Show(f.Read(item), item) : "";

            string type = Loc.Find("Entity.Type." + item.EntityType) ?? "";
            EyebrowText.Text = string.Join(" · ", new[] { type, headline.Eyebrow == null ? "" : Shown(headline.Eyebrow) }.Where(t => t.Length > 0)).ToUpper(Loc.Culture);
            EyebrowText.Visibility = EyebrowText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            NameText.Text = item.Name;
            SubtitleText.Text = string.Join(" ", headline.Subtitle.Select(Shown).Where(t => t.Length > 0));
            SubtitleText.Visibility = SubtitleText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            Chips.Children.Clear();
            if (headline.Badge != null && Shown(headline.Badge) is string badge && badge.Length > 0)
                Chips.Children.Add(Chip(badge, plate: true));
            // A single-page type shows its fields right below: the chips would only repeat them.
            if (!SinglePage)
                foreach (string fact in headline.Chips.Select(Shown).Where(t => t.Length > 0))
                    Chips.Children.Add(Chip(fact));
            foreach (Due due in Dues().Where(d => d.Level != ExpiryLevel.Later).Take(3))
                Chips.Children.Add(DueChip(due));
            Chips.Visibility = Chips.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            Actions.Children.Clear();
            Actions.Children.Add(HeaderButton(Loc.T(EntityImages.Count(item) == 0 ? "Vehicle.AddPictures" : "Vehicle.EditBasics"), EditBasics));
            if (item.EntityType == EntityTypes.Vehicle)
                Actions.Children.Add(HeaderButton(Loc.T("Vehicle.Odometer.Update"), UpdateOdometer));
            Button delete = HeaderButton(Loc.T("Common.Delete"), Delete);
            delete.Foreground = ItemLook.Res("Danger");
            Actions.Children.Add(delete);

            ShowPictures(hero.Selected);
        }

        private static Button HeaderButton(string text, Action click)
        {
            Button button = new Button { Content = text, Style = (Style)Application.Current.FindResource("GhostButtonStyle"), Height = 36, Margin = new Thickness(0, 0, 8, 8) };
            button.Click += (s, e) => click();
            return button;
        }

        // A key fact in a rounded chip; the plate as a plate.
        private static Border Chip(string text, bool plate = false) => new Border
        {
            Background = ItemLook.Res("BgPanel"),
            BorderBrush = plate ? ItemLook.Res("TextMain") : ItemLook.Res("Border"),
            BorderThickness = new Thickness(plate ? 1.5 : 1),
            CornerRadius = new CornerRadius(plate ? 4 : 14),
            Padding = new Thickness(12, 4, 12, 5),
            Margin = new Thickness(0, 0, 8, 8),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = plate ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = ItemLook.Res(plate ? "TextMain" : "TextBody")
            }
        };

        private static Border DueChip(Due due)
        {
            Border chip = Chip(Loc.F("Entity.ExpiryLine", due.Name, due.When));
            chip.BorderBrush = ItemLook.Res(due.Level == ExpiryLevel.Expired ? "Danger" : "BtnHover");
            chip.ToolTip = due.Detail;
            TextBlock text = (TextBlock)chip.Child;
            text.Foreground = Expiry.Brush(due.Level);
            text.FontWeight = Expiry.Weight(due.Level);
            return chip;
        }

        // The cover first, then whichever is picked (arrows, keys, thumbnails); none: no picture area at all. A custom item
        // whose layout has a gallery block shows them there instead.
        private void ShowPictures(int index)
        {
            List<string> names = EntityImages.Names(item);
            Hero.Visibility = names.Count == 0 || Custom?.Layout.HasGallery == true ? Visibility.Collapsed : Visibility.Visible;
            if (Hero.Visibility == Visibility.Collapsed)
                return;
            Entity shown = item;
            hero.Show(names, index, () => item == shown && IsVisible);
        }

        // What comes due, the most urgent first: readings close or passed (EntityTypes.DistanceDues), then the dates
        // (EntityTypes.Expiries) by how close they are. Shown as chips and on the Overview.
        private sealed record Due(string Name, string When, string Detail, ExpiryLevel Level, string Tab, Guid Record);

        private List<Due> Dues()
        {
            string TabOf(RecordDefinition kind) => EntityTypes.Tabs(item.EntityType).First(t => t.Records.Contains(kind)).Key;
            IEnumerable<Due> readings = EntityTypes.DistanceDues(item).Select(d => new Due(d.Name, d.Describe(item),
                Loc.F("Expiry.AtReading", Units.Format(d.Remaining + item.OdometerKm!.Value, Units.Distance, item)), d.Level, TabOf(d.Kind), d.Record.Id));
            IEnumerable<Due> dates = EntityTypes.Expiries(item).Select(d => new Due(DueName(d), Expiry.Describe(d.Date, DateTime.Today),
                d.Date.ToString(Loc.T("Format.LongDate"), Loc.Culture), Expiry.Level(d.Date, DateTime.Today), TabOf(d.Kind), d.Record.Id));
            return readings.Concat(dates).OrderByDescending(d => d.Level).ToList(); // stable: each kind keeps its own order
        }

        // A date's name: its record's title ("Vignette · Austria", what Home shortens to "Austria"); a dated list's
        // (a service) by its kind, as its title starts with its own date.
        private static string DueName(Deadline deadline) =>
            deadline.Kind.NewestFirstKey == null ? deadline.Kind.Title(deadline.Record) : deadline.Name;

        // ---- The page --------------------------------------------------------------------------------

        private void ShowPage()
        {
            editing = null;
            Page.Children.Clear();
            string query = SearchBox.Text.Trim();
            titleAction = null;
            gallery = null;
            List<UIElement> blocks = query.Length > 0 ? SearchBlocks(query)
                : Custom is CustomType custom ? CustomBlocks(custom)
                : page == OverviewKey ? OverviewBlocks()
                : TabBlocks(EntityTypes.Tabs(item.EntityType).First(t => t.Key == page));
            if (!SinglePage)
            {
                Grid title = new Grid { MinHeight = PageTitleHeight };
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                title.Children.Add(new TextBlock
                {
                    Text = query.Length > 0 ? Loc.T("Vehicle.Search.Placeholder") : Pages().First(p => p.Key == page).Label,
                    Style = (Style)FindResource("PageTitleStyle"),
                    TextWrapping = TextWrapping.Wrap
                });
                if (titleAction is FrameworkElement action)
                {
                    action.Margin = new Thickness(16, 0, ItemLook.Gap + 1, 12); // in line with the links inside the card
                    action.VerticalAlignment = VerticalAlignment.Bottom;
                    Grid.SetColumn(action, 1);
                    title.Children.Add(action);
                }
                Page.Children.Add(title);
            }
            Page.Children.Add(Card(blocks));
        }

        // A heading over rows that run edge to edge: the rows pad themselves by 12 below, so the block adds the rest of
        // the space every section has under it.
        private static StackPanel ListBlock() => new StackPanel { Margin = new Thickness(0, 0, 0, ItemLook.SectionBottom - 12) };

        // The blocks in one white card, a thin line between them.
        private static Border Card(List<UIElement> blocks)
        {
            StackPanel content = new StackPanel();
            for (int i = 0; i < blocks.Count; i++)
            {
                if (i > 0)
                    content.Children.Add(ItemLook.Divider());
                content.Children.Add(blocks[i]);
            }
            return new Border { Style = (Style)Application.Current.FindResource("CardStyle"), Child = content };
        }

        // A tab: its sections, its record lists, its logs; a single-page type's notes at the end.
        private List<UIElement> TabBlocks(TabDefinition tab)
        {
            List<UIElement> blocks = tab.Sections.Select(s => (UIElement)Section(s)).ToList();
            // A page that is only one list: the page title names it already.
            bool alone = tab.Sections.Length == 0 && tab.Logs.Length == 0 && tab.Records.Length == 1 && !SinglePage;
            blocks.AddRange(tab.Records.Select(records => RecordsBlock(records, titled: !alone)));
            blocks.AddRange(tab.Logs.Select(LogBlock));
            if (SinglePage)
                blocks.Add(Section(EntityTypes.Notes));
            return blocks;
        }

        // A custom item (CustomType): its blocks in the layout's order, two half blocks side by side (HalfRow), each in the
        // look of the sections and lists every type has; the checklist and the gallery are blocks of their own. The item's
        // own notes, if it has any from elsewhere, last.
        private List<UIElement> CustomBlocks(CustomType custom)
        {
            List<(UIElement Element, bool Half)> blocks = new List<(UIElement, bool)>();
            foreach (CustomBlock block in custom.Blocks)
            {
                UIElement? element = block.Kind switch
                {
                    BlockKinds.Fields when block.Section!.Fields.Length == 0 => NoFields(block),
                    BlockKinds.Fields or BlockKinds.Notes => Section(block.Section!, beside: block.Half),
                    BlockKinds.Gallery => GalleryBlock(block),
                    BlockKinds.Checklist => ChecklistBlock(block.Records!),
                    _ => block.Records == null ? null : RecordsBlock(block.Records, titled: true)
                };
                if (element != null)
                    blocks.Add((element, block.Half));
            }
            List<UIElement> rows = HalfRow.Rows(blocks);
            if (!string.IsNullOrWhiteSpace(item.Notes))
                rows.Add(Section(EntityTypes.Notes));
            if (rows.Count == 0)
            {
                // Every block removed: what the window shows and where blocks are added.
                StackPanel empty = new StackPanel { Margin = new Thickness(ItemLook.Gap, 32, ItemLook.Gap, 32) };
                empty.Children.Add(ItemLook.Muted(Loc.T("Custom.Item.NoBlocks"), 14));
                rows.Add(empty);
            }
            return rows;
        }

        // A Fields block without fields yet: its name, and where fields are added.
        private UIElement NoFields(CustomBlock block)
        {
            StackPanel section = new StackPanel();
            section.Children.Add(ItemLook.Heading(block.Section!.Label, out StackPanel actions, compact: !block.Half));
            actions.Children.Add(ItemLook.Muted(Loc.T("Custom.Fields.None")));
            return section;
        }

        // The item's own pictures (cover and further ones) where the layout puts them: the selected one large with arrows,
        // the strip under it; Left and Right browse them. Changed in the item form, as on every item.
        private UIElement GalleryBlock(CustomBlock block)
        {
            List<string> names = EntityImages.Names(item);
            StackPanel section = new StackPanel();
            section.Children.Add(ItemLook.Heading(block.Name, out StackPanel actions, compact: names.Count == 0 && !block.Half));
            if (names.Count == 0)
            {
                actions.Children.Add(ItemLook.Muted(Loc.T("Custom.Gallery.Empty")));
                actions.Children.Add(ItemLook.Link(Loc.T("Vehicle.AddPictures"), EditBasics));
                return section;
            }
            actions.Children.Add(ItemLook.Link(Loc.T("Common.Edit"), EditBasics));
            galleryIndex = Math.Clamp(galleryIndex, 0, names.Count - 1);
            PictureBrowser browser = new PictureBrowser(block.Half ? 220 : 300, PictureBrowser.ThumbSize + 10, canReorder: false, stripForOne: false)
            {
                Margin = ItemLook.FieldsMargin
            };
            Entity shown = item;
            browser.Picked += index =>
            {
                galleryIndex = index;
                browser.ShowStored(names, index, () => item == shown && browser.IsVisible);
            };
            browser.ShowStored(names, galleryIndex, () => item == shown && browser.IsVisible);
            AutomationProperties.SetName(browser, block.Name);
            section.Children.Add(browser);
            gallery = browser;
            return section;
        }

        // A checklist block: ticks and lines saved at once; the keyboard goes back to the line it was on once the page is
        // drawn again (keepLine).
        private UIElement ChecklistBlock(RecordDefinition records)
        {
            Guid? focus = keepLine is (string block, Guid line) && block == records.Key ? line : null;
            if (focus != null)
                keepLine = null;
            return new ChecklistSection(records, item, edit => Commit(v =>
            {
                List<EntityRecord> stored = records.Read(v);
                edit(stored);
                records.Write(v, stored);
            }), line => keepLine = line == null ? null : (records.Key, line.Value), focus);
        }

        private FieldsSection Section(SectionDefinition section, bool beside = false) =>
            new FieldsSection(section, item, Gate, editors => Commit(v =>
            {
                foreach (FieldEditor editor in editors)
                    editor.Field.Write(v, editor.Value);
            }), keepHeadingLine: beside);

        // ---- Overview: what there is, nothing else ---------------------------------------------------

        private List<UIElement> OverviewBlocks()
        {
            List<UIElement> blocks = new List<UIElement>();
            if (EntityTypes.Tabs(item.EntityType).Count == 0)
            {
                blocks.Add(Section(EntityTypes.Notes));
                return blocks;
            }
            List<Due> dues = Dues();
            if (dues.Count > 0)
                blocks.Add(DuesBlock(dues));
            foreach (RecordDefinition records in EntityTypes.Records(item.EntityType).Where(r => r.NewestFirstKey != null))
                if (LatestBlock(records) is UIElement latest)
                    blocks.Add(latest);
            if (!string.IsNullOrWhiteSpace(item.Notes))
                blocks.Add(Section(EntityTypes.Notes));
            if (blocks.Count == 0)
                blocks.Add(GettingStarted());
            return blocks;
        }

        // Everything that comes due, the most urgent first, with how close it is; a click opens it where it is.
        private UIElement DuesBlock(List<Due> dues)
        {
            StackPanel block = ListBlock();
            block.Children.Add(ItemLook.Heading(Loc.T("Vehicle.Overview.Deadlines"), out _));
            foreach (Due due in dues)
            {
                Grid line = new Grid();
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel what = new StackPanel();
                what.Children.Add(new TextBlock { Text = due.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ItemLook.Res("TextMain"), TextWrapping = TextWrapping.Wrap });
                what.Children.Add(ItemLook.Muted(due.Detail, 12));
                line.Children.Add(what);
                TextBlock when = new TextBlock { Text = due.When, FontSize = 13, Foreground = Expiry.Brush(due.Level), FontWeight = Expiry.Weight(due.Level),
                    Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(when, 1);
                line.Children.Add(when);
                block.Children.Add(LinkRow(line, due.Name + ", " + due.When, () => GoToRecord(due.Tab, due.Record)));
            }
            return block;
        }

        // A list's newest record (the last service), as its row; a click opens it in its list.
        private UIElement? LatestBlock(RecordDefinition records)
        {
            EntityRecord? latest = records.Read(item).OrderByDescending(r => Values.ParseDate(r.Get(records.NewestFirstKey!)) ?? DateTime.MinValue).FirstOrDefault();
            if (latest == null)
                return null;
            string tab = EntityTypes.Tabs(item.EntityType).First(t => t.Records.Contains(records)).Key;
            StackPanel block = ListBlock();
            Grid heading = ItemLook.Heading(Loc.F("Item.Overview.Latest", records.Label), out StackPanel actions);
            actions.Children.Add(ItemLook.Link(Loc.T("Item.Overview.All"), () => SelectPage(tab)));
            block.Children.Add(heading);
            RecordRow row = new RecordRow(records, latest, item, 0, null, null, Array.Empty<UIElement>(), null, null, null);
            row.PointRight();
            row.Toggled += _ => GoToRecord(tab, latest.Id);
            block.Children.Add(row);
            return block;
        }

        // Nothing to show yet: what fills this page, and the one thing to do first.
        private UIElement GettingStarted()
        {
            StackPanel block = new StackPanel { Margin = new Thickness(ItemLook.Gap, 40, ItemLook.Gap, 40), MaxWidth = 400, HorizontalAlignment = HorizontalAlignment.Center };
            block.Children.Add(new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 28, Foreground = ItemLook.Res("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center });
            block.Children.Add(new TextBlock { Text = Loc.T("Item.Overview.Empty.Title"), FontSize = 17, FontWeight = FontWeights.SemiBold, Foreground = ItemLook.Res("TextMain"),
                HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 16, 0, 8) });
            TextBlock text = ItemLook.Muted(Loc.T("Item.Overview.Empty.Text"), 14);
            text.TextAlignment = TextAlignment.Center;
            block.Children.Add(text);
            TabDefinition first = EntityTypes.Tabs(item.EntityType)[0];
            Button start = new Button { Content = Loc.T("Vehicle.Overview.AddSpecs"), Height = 36, Padding = new Thickness(20, 0, 20, 0),
                HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 0) };
            start.Click += (s, e) =>
            {
                SelectPage(first.Key);
                Page.Dispatcher.BeginInvoke(new Action(() => Page.Children.OfType<Border>().SelectMany(c => ((StackPanel)c.Child).Children.OfType<FieldsSection>())
                    .FirstOrDefault()?.BeginEdit()), System.Windows.Threading.DispatcherPriority.Loaded);
            };
            block.Children.Add(start);
            return block;
        }

        // A row that is a button: hover tint, a click runs `go`.
        private static Button LinkRow(UIElement content, string name, Action go)
        {
            Border row = new Border { Padding = new Thickness(ItemLook.Gap, 12, ItemLook.Gap, 12), Background = Brushes.Transparent, Child = content };
            Button button = new Button { Template = PictureBrowser.FlatTemplate, Cursor = Cursors.Hand, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(button, name);
            button.MouseEnter += (s, e) => row.Background = ItemLook.Res("BgSubtle");
            button.MouseLeave += (s, e) => row.Background = Brushes.Transparent;
            button.Click += (s, e) => go();
            return button;
        }

        // The record's page with the record open (in a nested list, the ones above it opened) and in view.
        private void GoToRecord(string tab, Guid id)
        {
            foreach (RecordDefinition nested in EntityTypes.Records(item.EntityType).Where(r => r.Nested))
                if (RecordTree.Walk(nested.Read(item)).FirstOrDefault(x => x.Record.Id == id) is { Record: not null } found)
                    foreach (EntityRecord above in found.Above)
                        openParts.Add(above.Id);
            openRecord = id;
            page = tab;
            SearchBox.Text = "";
            ShowNav();
            ShowPage();
            Dispatcher.BeginInvoke(new Action(() => FindRow(id)?.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private RecordRow? FindRow(Guid id) => Descendants(Page).OfType<RecordRow>().FirstOrDefault(r => r.Record.Id == id);

        private static IEnumerable<DependencyObject> Descendants(DependencyObject parent) =>
            LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>().SelectMany(c => Descendants(c).Prepend(c));

        // ---- Record lists ------------------------------------------------------------------------------

        // Its heading with Add; the costs (TotalKey) and the filter by kind (FilterKey); then a row per record, newest
        // first (NewestFirstKey), or the outline of a nested one. Empty: what the list is for.
        private UIElement RecordsBlock(RecordDefinition records, bool titled)
        {
            List<EntityRecord> list = records.Read(item);
            if (records.Compact)
            {
                (Guid Record, bool Plus)? focus = refocus;
                refocus = null;
                return new CompactRecords(records, list, item,
                    edit => Commit(v =>
                    {
                        List<EntityRecord> stored = records.Read(v);
                        edit(stored);
                        records.Write(v, stored);
                    }),
                    record => DeleteRecord(records, record), (id, plus) => refocus = (id, plus), Gate, focus);
            }
            // Untitled (the page is this one list): no heading, its Add goes on the page title's line.
            StackPanel block = titled ? ListBlock() : new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
            FrameworkElement add = ItemLook.Link("+ " + records.AddLabel, () => OpenRecord(records, null, null));
            if (titled)
            {
                Grid heading = ItemLook.Heading(records.Label, out StackPanel actions);
                actions.Children.Add(add);
                block.Children.Add(heading);
            }
            else
                titleAction = add;

            if (list.Count == 0)
            {
                StackPanel empty = new StackPanel { Margin = new Thickness(ItemLook.Gap, titled ? 0 : 16, ItemLook.Gap, 12) };
                empty.Children.Add(ItemLook.Muted(records.Text("Empty")));
                if (records.Nested)
                {
                    Button template = new Button { Content = records.Text("Template"), Height = 36, Padding = new Thickness(16, 0, 16, 0),
                        HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 16, 0, 0) };
                    template.Click += (s, e) => AddTemplate(records);
                    empty.Children.Add(template);
                }
                block.Children.Add(empty);
                return block;
            }

            if (records.TotalKey != null && Costs(records, list) is UIElement costs)
                block.Children.Add(costs);
            IEnumerable<EntityRecord> shown = records.NewestFirstKey == null ? list
                : list.OrderByDescending(r => Values.ParseDate(r.Get(records.NewestFirstKey)) ?? DateTime.MinValue);
            if (records.FilterKey != null)
            {
                block.Children.Add(FilterRow(records, list));
                if (filters.GetValueOrDefault(records.Key) is string kind)
                    shown = shown.Where(r => r.Get(records.FilterKey) == kind);
            }

            List<RecordRow> rows = records.Nested ? OutlineRows(records, list) : shown.Select(r => Row(records, r, list, 0)).ToList();
            if (rows.Count == 0)
            {
                TextBlock none = ItemLook.Muted(Loc.T("Entity.Records.NoMatch"));
                none.Margin = new Thickness(ItemLook.Gap, 8, ItemLook.Gap, 8);
                block.Children.Add(none);
            }
            foreach (RecordRow row in rows)
            {
                if (block.Children.Count > 0) // no line over the first row of an untitled list: the card's edge is there
                    block.Children.Add(ItemLook.Divider());
                block.Children.Add(row);
            }
            return block;
        }

        // A record's row, open if it was. Edit and Delete are the row's own icons; what its list adds (making it the one
        // in use, a part's moves) is in the row once opened.
        private RecordRow Row(RecordDefinition records, EntityRecord record, List<EntityRecord> list, int depth, FrameworkElement? outline = null)
        {
            List<UIElement> actions = new List<UIElement>();
            UIElement? status = null;
            if (records.CurrentKey != null)
            {
                if (records.IsCurrent(item, record))
                    status = StatusBadge(records.Text("Current"));
                else
                    actions.Add(ItemLook.Link(records.Text("MakeCurrent"), () => Commit(v => records.SetCurrent(v, record.Id))));
            }
            UIElement? extra = null;
            if (records.Nested)
            {
                actions.Add(ItemLook.Link(records.Text("AddUnder"), () => OpenRecord(records, null, record.Id)));
                actions.AddRange(Moves(records, list, record, depth));
                extra = History(records, record);
            }
            RecordRow row = new RecordRow(records, record, item, depth, () => OpenRecord(records, record, null), () => DeleteRecord(records, record),
                actions, status, outline, extra);
            row.SetOpen(openRecord == record.Id);
            row.Toggled += Toggle;
            return row;
        }

        private void Toggle(RecordRow row)
        {
            bool open = !row.IsOpen;
            foreach (RecordRow other in Descendants(Page).OfType<RecordRow>())
                other.SetOpen(false);
            row.SetOpen(open);
            openRecord = open ? row.Record.Id : null;
        }

        private static Border StatusBadge(string text) => new Border
        {
            Background = ItemLook.Res("AccentSoft"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(8, 1, 8, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ItemLook.Res("Accent") }
        };

        // A nested list's rows: every record whose parents are open, indented by depth, with an arrow to open the ones
        // that hold others.
        private List<RecordRow> OutlineRows(RecordDefinition records, List<EntityRecord> list)
        {
            List<RecordRow> rows = new List<RecordRow>();
            foreach ((EntityRecord record, IReadOnlyList<EntityRecord> above) in RecordTree.Walk(list))
            {
                if (above.Any(a => !openParts.Contains(a.Id)))
                    continue;
                // The open / close arrow, or its room, so every name of a level starts in one line.
                FrameworkElement arrow = new Border { Width = 28, Margin = new Thickness(-8, 0, 8, 0) };
                if (record.Children?.Count > 0)
                {
                    bool isOpen = openParts.Contains(record.Id);
                    Button toggle = new Button
                    {
                        Style = (Style)FindResource("HoverIconButtonStyle"),
                        Content = isOpen ? "" : "", // ChevronDown / ChevronRight
                        FontSize = 10,
                        Width = 28,
                        Height = 28, // as tall as the row's text: rows with and without one stay the same height
                        Margin = new Thickness(-8, 0, 8, 0),
                        ToolTip = Loc.T(isOpen ? "Entity.Outline.Close" : "Entity.Outline.Open")
                    };
                    AutomationProperties.SetName(toggle, Loc.T(isOpen ? "Entity.Outline.Close" : "Entity.Outline.Open"));
                    toggle.Click += (s, e) =>
                    {
                        if (!openParts.Remove(record.Id))
                            openParts.Add(record.Id);
                        ShowPage();
                        e.Handled = true;
                    };
                    arrow = toggle;
                }
                rows.Add(Row(records, record, list, above.Count, arrow));
            }
            return rows;
        }

        // A nested record's moves: up, down, into the one above, out a level (each only when it can).
        private IEnumerable<UIElement> Moves(RecordDefinition records, List<EntityRecord> list, EntityRecord record, int depth)
        {
            List<EntityRecord> holder = RecordTree.Holder(list, record.Id)!;
            int at = holder.IndexOf(record);
            UIElement Move(string key, string glyph, bool can, Func<List<EntityRecord>, bool> move)
            {
                Button button = new Button
                {
                    Style = (Style)FindResource("HoverIconButtonStyle"),
                    Content = glyph,
                    IsEnabled = can,
                    Opacity = can ? 1 : 0.35,
                    Margin = new Thickness(8, 0, 0, 0),
                    ToolTip = records.Text(key)
                };
                AutomationProperties.SetName(button, records.Text(key));
                button.Click += (s, e) => Commit(v =>
                {
                    List<EntityRecord> stored = records.Read(v);
                    if (move(stored))
                        records.Write(v, stored);
                });
                return button;
            }
            yield return Move("Up", "", at > 0, l => RecordTree.Move(l, record.Id, -1));
            yield return Move("Down", "", at < holder.Count - 1, l => RecordTree.Move(l, record.Id, +1));
            yield return Move("In", "", RecordTree.CanIndent(list, record.Id), l => RecordTree.Indent(l, record.Id));
            yield return Move("Out", "", holder != list, l => RecordTree.Outdent(l, record.Id));
        }

        // The records of other lists that point at this one (a part's services: fields of the kind Links), newest first.
        private UIElement? History(RecordDefinition records, EntityRecord record)
        {
            List<(RecordDefinition Kind, EntityRecord Record)> linked = EntityTypes.Records(item.EntityType)
                .SelectMany(def => def.Fields.Where(f => f.Kind == ValueKind.Links && f.LinkTo == records.Key)
                    .SelectMany(f => def.Read(item).Where(r => FieldDefinition.Items(r.Get(f.Key)).Contains(record.Id.ToString()))
                        .OrderByDescending(r => def.NewestFirstKey == null ? null : Values.ParseDate(r.Get(def.NewestFirstKey)))
                        .Select(r => (def, r))))
                .ToList();
            if (linked.Count == 0)
                return null;
            StackPanel history = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
            history.Children.Add(new TextBlock { Text = records.Text("History"), FontSize = 12, Foreground = ItemLook.Res("TextSub"), Margin = new Thickness(0, 0, 0, 4) });
            foreach ((RecordDefinition kind, EntityRecord entry) in linked)
            {
                string subtitle = kind.Subtitle?.Invoke(entry, item) ?? "";
                history.Children.Add(ItemLook.Value(subtitle.Length == 0 ? kind.Title(entry) : kind.Title(entry) + " — " + subtitle));
            }
            return history;
        }

        // "All" and each kind the list has (FilterKey's choices, in their order); picking one shows only those.
        private UIElement FilterRow(RecordDefinition records, List<EntityRecord> list)
        {
            FieldDefinition field = records.Fields.First(f => f.Key == records.FilterKey);
            HashSet<string> present = list.Select(r => r.Get(field.Key) ?? "").ToHashSet();
            string? chosen = filters.GetValueOrDefault(records.Key);
            WrapPanel row = new WrapPanel { Margin = new Thickness(ItemLook.Gap - 4, 0, ItemLook.Gap, 12) };
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
                    if (kind == filters.GetValueOrDefault(records.Key))
                        return;
                    if (kind == null)
                        filters.Remove(records.Key);
                    else
                        filters[records.Key] = kind;
                    openRecord = null;
                    ShowPage();
                };
                row.Children.Add(button);
            }
            return row;
        }

        // The money a list adds up to (TotalKey): this year and all time, then per kind (FilterKey), as figures.
        private UIElement? Costs(RecordDefinition records, List<EntityRecord> list)
        {
            List<(EntityRecord Record, double Amount)> amounts = list.Select(r => (r, Values.Number(r.Get(records.TotalKey!))))
                .Where(x => x.Item2 != null).Select(x => (x.r, x.Item2!.Value)).ToList();
            if (amounts.Count == 0)
                return null;
            int year = DateTime.Today.Year;
            double thisYear = amounts.Where(x => records.NewestFirstKey != null && Values.ParseDate(x.Record.Get(records.NewestFirstKey))?.Year == year).Sum(x => x.Amount);

            // The two totals large; under them, when there is more than one kind, what each kind came to in all.
            StackPanel costs = new StackPanel { Margin = new Thickness(ItemLook.Gap, 4, ItemLook.Gap, 12) };
            WrapPanel totals = new WrapPanel();
            totals.Children.Add(Figure(Loc.F("Entity.Costs.ThisYear", year), thisYear, large: true));
            totals.Children.Add(Figure(Loc.T("Entity.Costs.AllTime"), amounts.Sum(x => x.Amount), large: true));
            costs.Children.Add(totals);
            List<IGrouping<string, (EntityRecord Record, double Amount)>> byKind = records.Fields.FirstOrDefault(f => f.Key == records.FilterKey) is FieldDefinition kinds
                ? amounts.GroupBy(x => x.Record.Get(kinds.Key) ?? "").OrderBy(g => Array.IndexOf(kinds.Choices, g.Key)).ToList()
                : new List<IGrouping<string, (EntityRecord Record, double Amount)>>();
            if (byKind.Count > 1)
            {
                FieldDefinition kindField = records.Fields.First(f => f.Key == records.FilterKey);
                TextBlock caption = ItemLook.Muted(Loc.T("Entity.Costs.ByKind"), 12);
                caption.Margin = new Thickness(0, 4, 0, 8);
                costs.Children.Add(caption);
                WrapPanel kindsRow = new WrapPanel();
                foreach (IGrouping<string, (EntityRecord Record, double Amount)> kind in byKind)
                    kindsRow.Children.Add(Figure(kindField.ChoiceLabel(kind.Key), kind.Sum(x => x.Amount), large: false));
                costs.Children.Add(kindsRow);
            }
            return costs;
        }

        private UIElement Figure(string label, double amount, bool large)
        {
            StackPanel figure = new StackPanel { Margin = new Thickness(0, 0, 40, 12) };
            figure.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = ItemLook.Res("TextSub") });
            TextBox value = TextCopy.Selectable(Units.Format(amount, Units.Money, item), large ? 22 : 16, ItemLook.Res(large ? "TextMain" : "TextBody"), FontWeights.SemiBold);
            value.FontFamily = (FontFamily)FindResource("DisplayFont");
            value.TextWrapping = TextWrapping.NoWrap;
            figure.Children.Add(value);
            return figure;
        }

        // ---- Editing a record --------------------------------------------------------------------------

        // A record's dialog: a new one (under `parent` in a nested list), or `record` to change.
        private void OpenRecord(RecordDefinition records, EntityRecord? record, Guid? parent)
        {
            if (!LeaveEdit())
                return;
            EntityRecord target = record ?? records.New(item);
            List<FieldEditor> editors = records.Fields.Select(f => new FieldEditor(f, item, target.Get(f.Key), target)).ToList();
            string title = record != null ? records.Title(record)
                : parent is Guid under && RecordTree.Find(records.Read(item), under) is EntityRecord holder
                    ? Loc.F("Entity.Records." + records.Key + ".NewUnder", records.Title(holder)) : records.AddLabel;
            EditDialog dialog = null!;
            dialog = new EditDialog(this, title, EditDialog.Inputs(editors),
                async () => EditDialog.Check(editors, dialog) && await SaveRecord(records, target, parent, record == null, editors),
                EditDialog.Changed(editors), width: 720) { Editors = editors };
            dialog.ShowDialog();
        }

        // The record's fields from the dialog. A picked file and picked pictures are copied in first (and removed again
        // if the save fails); what it replaces or loses is deleted once the item is saved without it (the collection
        // view). A new record goes at the end of its list, or under `parent`, and is the one open after.
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

            Guid? wasOpen = openRecord;
            openRecord = record.Id;
            if (parent != null)
                openParts.Add(parent.Value);
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
                openRecord = wasOpen;
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

        // The usual records to start a nested list with (VehicleSchema.PartsTemplate), named in the current language
        // (the user's own from then on).
        private void AddTemplate(RecordDefinition records)
        {
            if (!LeaveEdit())
                return;
            Commit(v => records.Write(v, VehicleSchema.PartsTemplate.Select(key =>
            {
                EntityRecord system = records.New(v);
                system.Set("Name", Loc.T("Entity.Parts.Template." + key));
                return system;
            }).ToList()));
        }

        // ---- Logs ------------------------------------------------------------------------------------------

        private UIElement LogBlock(LogDefinition log) => new LogSection(log, EntityTypes.Rows(item, log), item, () => OpenLog(log));

        private void OpenLog(LogDefinition log)
        {
            if (!LeaveEdit())
                return;
            LogEditor editor = new LogEditor(log);
            editor.Load(EntityTypes.Rows(item, log));
            string opened = JsonSerializer.Serialize(editor.FilledRows);
            new EditDialog(this, log.Label, editor, () =>
            {
                Keyboard.ClearFocus();
                if (editor.HasProblem)
                {
                    editor.BringIntoView();
                    return Task.FromResult(false);
                }
                List<Dictionary<string, string>> rows = editor.FilledRows;
                return Task.FromResult(Commit(v =>
                {
                    if (rows.Count > 0)
                        (v.Logs ??= new Dictionary<string, List<Dictionary<string, string>>>())[log.Key] = rows;
                    else if (v.Logs != null && v.Logs.Remove(log.Key) && v.Logs.Count == 0)
                        v.Logs = null;
                }));
            }, EditDialog.Changed(editor, opened)).ShowDialog();
        }

        // ---- Search ----------------------------------------------------------------------------------------

        // While the box has text the page lists what matches, in place of the page. An edit going on is stopped first
        // (asking if something was typed); kept, the search is emptied again.
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!LeaveEdit())
            {
                SearchBox.Clear();
                return;
            }
            ShowNav();
            ShowPage();
        }

        private List<UIElement> SearchBlocks(string query)
        {
            List<ItemMatch> matches = ItemSearch.Find(item, query);
            StackPanel block = ListBlock();
            block.Children.Add(ItemLook.Heading(matches.Count == 0 ? Loc.F("Vehicle.Search.None", query) : Loc.Plural("Vehicle.Search.Found", matches.Count), out _));
            foreach (ItemMatch match in matches)
            {
                StackPanel lines = new StackPanel();
                lines.Children.Add(ItemLook.Muted(match.Where, 12));
                lines.Children.Add(new TextBlock { Text = match.Text, FontSize = 14, Foreground = ItemLook.Res("TextMain"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
                block.Children.Add(LinkRow(lines, match.Where + ": " + match.Text, () => GoTo(match)));
            }
            return new List<UIElement> { block };
        }

        private void GoTo(ItemMatch match)
        {
            if (match.Record is Guid id)
            {
                GoToRecord(match.Tab, id);
                return;
            }
            page = match.Tab;
            SearchBox.Text = ""; // draws the page
            ShowNav();
            ShowPage();
        }

        // ---- Editing and saving ------------------------------------------------------------------------------

        // One section is edited at a time: another one starting asks the one being edited to stop first.
        private bool Gate(IInlineEdit section)
        {
            if (editing != null && editing != section && !LeaveEdit())
                return false;
            editing = section;
            return true;
        }

        // Stops the edit going on (asking first if something was typed); false if the user wants to go on with it.
        private bool LeaveEdit()
        {
            if (editing == null || !editing.IsEditing)
                return true;
            if (!editing.ConfirmLeave())
                return false;
            IInlineEdit left = editing;
            editing = null;
            left.CancelEdit();
            return true;
        }

        // The change, made on a copy (fields this version doesn't know come along), saved by the collection view; then
        // everything is drawn from it. False if it couldn't be saved: the window still shows what is stored.
        private bool Commit(Action<Entity> change)
        {
            Entity edited = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(item))!;
            change(edited);
            if (!host.Save(edited))
                return false;
            item = edited;
            Redraw();
            return true;
        }

        // ---- Header actions -----------------------------------------------------------------------------------

        // A new reading: stored as the current one and added to the readings (dated today). One lower than the last
        // is asked about first (a typo, or a new instrument cluster).
        private void UpdateOdometer()
        {
            if (!LeaveEdit())
                return;
            string unit = Units.Label(Units.Distance, item);
            string typed = item.OdometerKm?.ToString(Loc.Culture) ?? "";
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

            if (item.OdometerKm is int now && Values.Number(stored) < now
                && Msg.Show(this, Loc.F("Vehicle.Odometer.Lower", Units.Format(now, Units.Distance, item)), Loc.T("Vehicle.Odometer.Update"),
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

        private void EditBasics()
        {
            if (!LeaveEdit())
                return;
            if (host.EditBasics(item) is Entity saved)
            {
                item = saved;
                Redraw();
            }
        }

        private void Delete()
        {
            if (LeaveEdit() && host.Delete(item))
                Close();
        }

        // ---- Keys and closing ----------------------------------------------------------------------------------

        // Left and Right browse the pictures unless the keyboard is in a box or list that uses them.
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);
            if ((e.Key == Key.Left || e.Key == Key.Right) && Keyboard.Modifiers == ModifierKeys.None
                && !(e.OriginalSource is TextBoxBase) && !InList(e.OriginalSource as DependencyObject)
                && (gallery?.Browse(e.Key == Key.Left ? -1 : +1) ?? hero.Browse(e.Key == Key.Left ? -1 : +1)))
                e.Handled = true;
        }

        private static bool InList(DependencyObject? source)
        {
            for (DependencyObject? at = source; at != null; at = at is Visual ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
                if (at is ComboBox || at is DatePicker || at is RadioButton || at is ListBox)
                    return true;
            return false;
        }

        // Ctrl+F goes to the search box. Esc empties the search (back to the page), or stops an edit (asking first if
        // something was typed), or closes. An open dropdown takes its own Esc first.
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control && !SinglePage)
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
                s.ItemWindowWidth = Math.Round(size.Width);
                s.ItemWindowHeight = Math.Round(size.Height);
            });
            base.OnClosing(e);
        }
    }
}
