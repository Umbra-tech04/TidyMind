using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace TidyMind
{
    public partial class CollectionView : UserControl, IUndoPage, IMemoryPage
    {
        private readonly Profile memory;
        private List<Entity> entities;
        private readonly MemoryTabStrip tabStrip;
        private readonly DragReorder<Entity> cardDrag;
        private readonly CardSelectionController<Entity> selection;

        // Its items or tabs file couldn't be read: shown read-only (ShowUnreadable), and never saved over.
        private bool unreadable;

        // A custom collection (it has a layout, CustomLayout): Add makes an item of its layout, no type to pick.
        private bool custom;
        private string unreadableFile, unreadableProblem;

        // The search box rebuilds the cards after a short pause in typing, not on every key: rebuilding a big grid per
        // keystroke lags.
        private readonly DispatcherTimer searchPause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        // saveProfiles: writes the main window's list of memories, which `profile` belongs to (Edit Background).
        public CollectionView(Profile profile, Func<bool> saveProfiles)
        {
            InitializeComponent();
            searchPause.Tick += (s, e) =>
            {
                searchPause.Stop();
                RenderEntities();
            };
            memory = profile;
            editBackground = MemoryPageBackground.Attach(Page, BackgroundLayer, profile, saveProfiles, HeaderPlate, EmptyCard);
            TitleText.Text = profile.Name;
            NotebookButton.Load(profile);

            // Counts, cards and the reason a file couldn't be read, in the new language; names are the user's own.
            Loc.Track(this, () =>
            {
                if (unreadable)
                {
                    SubtitleText.Text = Loc.T("Collection.Kind");
                    EmptyState.Text = Loc.F("Memory.Unreadable", unreadableFile, unreadableProblem);
                    return;
                }
                tabStrip.Render();
                RenderEntities();
            });

            // An unreadable file is left alone: the page says why, and nothing on it can change or save items.
            if (!EntityStore.TryLoad(memory, out entities, out string problem))
            {
                ShowUnreadable(MemoryFiles.Items(memory), problem);
                return;
            }
            // A custom collection's items are laid out by its layout file. One that can't be read leaves the page read-only,
            // as an unreadable items file does: without it the items' blocks can't be shown or changed.
            if (!CustomLayoutStore.TryLoad(memory, out CustomLayout? layout, out problem))
            {
                ShowUnreadable(MemoryFiles.Layout(memory), problem);
                return;
            }
            custom = layout != null;
            if (!TabStore.TryLoadOrCreate(memory, entities.Select(e => e.TabId), out List<MemoryTab> tabs, out problem))
            {
                ShowUnreadable(MemoryFiles.Tabs(memory), problem);
                return;
            }

            selection = new CardSelectionController<Entity>(CardScroll, CardArea, MarqueeCanvas,
                CardEffects.CardRadius, DeleteEntities);
            selection.SelectionChanged += UpdateSelectionBar;
            SelectionBar.DeleteClicked += () => selection.DeleteSelected();
            SelectionBar.CancelClicked += selection.ClearSelection;

            // Cards sit 24px apart (12px margin each side); the drop line goes in the middle of that gap.
            cardDrag = new DragReorder<Entity>(EntityPanel, Orientation.Horizontal, 24, null, MoveEntity);

            tabStrip = new MemoryTabStrip(this, TabStrip, memory, tabs, "Tabs.DeleteMove.Items",
                tabId => entities.Count(e => e.TabId == tabId),
                (fromTab, toTab) =>
                {
                    foreach (Entity entity in entities.Where(e => e.TabId == fromTab))
                        entity.TabId = toTab;
                    SaveEntities();
                },
                () => UndoChange.Begin(UndoService.Page, UndoParts.Tabs(memory), UndoParts.Entities(memory)));
            tabStrip.TabsChanged += RenderEntities;

            AssignOrphansToFirstTab();
            CopyInLegacyImages();
            tabStrip.Render();
            RenderEntities();
        }

        // Items from before pictures were copied in point at the user's own file: it's copied into EntityImages/ now,
        // so moving or deleting the original no longer loses it. Any of an item's pictures may be such a path (one
        // the form moved away from the cover, too). One whose original is gone keeps its path (and shows no picture).
        // If the save fails, the copies are removed again and the items keep their old paths.
        private void CopyInLegacyImages()
        {
            List<(Entity Entity, List<string> Before, List<string> Made)> copied = new List<(Entity, List<string>, List<string>)>();
            foreach (Entity entity in entities)
            {
                List<string> before = EntityImages.Names(entity), after = new List<string>(), made = new List<string>();
                foreach (string name in before)
                {
                    string stored = EntityImages.IsLegacyPath(name) && File.Exists(name) ? EntityImages.Store.Import(name) : null;
                    after.Add(stored ?? name);
                    if (stored != null)
                        made.Add(stored);
                }
                if (made.Count == 0)
                    continue;
                EntityImages.SetNames(entity, after);
                copied.Add((entity, before, made));
            }

            if (copied.Count == 0 || SaveEntities())
                return;

            foreach ((Entity entity, List<string> before, List<string> made) in copied)
            {
                RemoveCopies(made);
                EntityImages.SetNames(entity, before);
            }
        }

        // Items from before tabs existed (TabId empty) or pointing at a deleted tab go to the first tab.
        private void AssignOrphansToFirstTab()
        {
            bool migrated = false;
            foreach (Entity entity in entities.Where(e => !tabStrip.Contains(e.TabId)))
            {
                entity.TabId = tabStrip.FirstTabId;
                migrated = true;
            }

            if (migrated)
                SaveEntities();
        }

        // Reads the list back from disk, e.g. after a delete whose save failed, so the grid shows what's really stored.
        private void ReloadEntities()
        {
            if (EntityStore.TryLoad(memory, out List<Entity> loaded, out string problem))
                entities = loaded;
            else
                ShowUnreadable(MemoryFiles.Items(memory), problem);
        }

        // The page without its items: the reason in place of the cards, and no toolbar or tabs to change anything
        // with. The page background has its own file and keeps working.
        private void ShowUnreadable(string file, string problem)
        {
            unreadable = true;
            unreadableFile = file;
            unreadableProblem = problem;
            selection?.ClearSelection();
            EntityPanel.Children.Clear();
            // The notebook has a file of its own and keeps working, and so does the memory's menu.
            foreach (UIElement tool in Toolbar.Children)
                if (tool != NotebookButton && tool != MoreButton)
                    tool.Visibility = Visibility.Collapsed;
            CardHint.Visibility = Visibility.Collapsed;
            SelectionBar.Visibility = Visibility.Collapsed;
            TabStrip.Visibility = Visibility.Collapsed;
            SubtitleText.Text = Loc.T("Collection.Kind");
            EmptyState.Text = Loc.F("Memory.Unreadable", file, problem);
            EmptyState.Visibility = Visibility.Visible;
        }

        // Drag-and-drop on the cards. Other tabs' items keep their relative order; everything is renumbered 0..n.
        private void MoveEntity(Entity dragged, Entity target, bool after)
        {
            using UndoChange change = Record();
            DragReorder<Entity>.Move(entities, dragged, target, after);
            for (int i = 0; i < entities.Count; i++)
                entities[i].Order = i;

            SaveEntities();
            change.Commit(Loc.F("Undo.Moved", dragged.Name));
            RenderEntities();
        }

        // An undoable change to the items (UndoService.Page: this page's history).
        private UndoChange Record() => UndoChange.Begin(unreadable ? null : UndoService.Page, UndoParts.Entities(memory), UndoParts.Reminders());

        public void OpenNotebook() => NotebookButton.Open();

        // The memory's menu, the same as its right-click in the sidebar (set by the main window).
        public Func<ContextMenu>? MemoryMenu { get; set; }

        // Edit Background for this page (MemoryPageBackground), also in the ⋯ menu.
        private readonly Action editBackground;

        private void MoreButton_Click(object sender, RoutedEventArgs e) =>
            MemoryPageBackground.OpenPageMenu(MoreButton, MemoryMenu, editBackground);

        // After an undo or redo: the items and tabs as stored now.
        public void ReloadAfterUndo()
        {
            NotebookButton.Refresh();
            if (unreadable)
                return;
            ReloadEntities();
            if (unreadable)
                return;
            if (!TabStore.TryLoadOrCreate(memory, entities.Select(e => e.TabId), out List<MemoryTab> tabs, out string problem))
            {
                ShowUnreadable(MemoryFiles.Tabs(memory), problem);
                return;
            }
            tabStrip.Reload(tabs); // draws the cards
        }

        // For the item's window after an undo: the item as stored now, null if it's gone.
        private Entity? Reread(Guid id)
        {
            ReloadAfterUndo();
            return unreadable ? null : entities.FirstOrDefault(e => e.Id == id);
        }

        private bool SaveEntities()
        {
            return !unreadable && EntityStore.SaveOrWarn(memory, entities);
        }

        private void RenderEntities()
        {
            if (unreadable) return;

            EntityPanel.Children.Clear();
            cardDrag.Clear();
            selection.BeginRender();

            string filter = SearchBox.Text.Trim();
            List<Entity> inTab = entities.Where(e => e.TabId == tabStrip.ActiveTabId).ToList();

            int shown = 0;
            foreach (Entity entity in inTab)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    !TextSearch.Contains(entity.Name, filter))
                    continue;

                EntityPanel.Children.Add(CreateEntityCard(entity));
                shown++;
            }

            SubtitleText.Text = Loc.Plural("Collection.Subtitle", inTab.Count);
            EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            CardHint.Visibility = shown == 0 ? Visibility.Collapsed : Visibility.Visible;
            EmptyState.Text = Loc.T(inTab.Count == 0 ? "Collection.Empty" : "Collection.NoMatch");
            selection.EndRender(); // what's no longer on screen (other tab, filtered out) drops out of the selection
        }

        // ---- Multi-selection ----------------------------------------------

        // While anything is selected, the selection bar stands in for the notebook button, search box and Add button.
        private void UpdateSelectionBar()
        {
            bool any = selection.Count > 0;
            SelectionBar.Show(selection.Count);
            SelectionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            Toolbar.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        // One confirmation for the lot, then the same removal as a single item's Delete, and the grid read back
        // from disk. One selected: just that item's Delete.
        private bool DeleteEntities(IReadOnlyList<Entity> doomed)
        {
            if (doomed.Count == 1)
            {
                Guid id = doomed[0].Id;
                DeleteEntity(doomed[0]);
                return !entities.Any(x => x.Id == id);
            }
            if (!BulkDelete.Confirm(Window.GetWindow(this), "Bulk.Delete.Items", doomed.Select(x => x.Name).ToList(), Loc.T("Bulk.UndoHint")))
                return false;

            RemoveEntities(doomed);
            ReloadEntities();
            RenderEntities();
            return true;
        }

        // ---- Search -------------------------------------------------------

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            searchPause.Stop();
            searchPause.Start();
        }

        // ---- Card ---------------------------------------------------------

        private FrameworkElement CreateEntityCard(Entity entity)
        {
            double size = CardEffects.CardSize;
            BitmapImage picture = EntityImages.Load(entity);
            bool hasImage = picture != null;

            Grid card = new Grid
            {
                Width = size,
                Height = size,
                Margin = new Thickness(12),
                Cursor = Cursors.Hand
            };

            DropShadowEffect shadow = CardEffects.CreateShadow();
            card.Children.Add(new Border
            {
                Background = Brushes.White,
                BorderBrush = B("#ECECE9"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(CardEffects.CardRadius),
                Effect = shadow
            });

            // A custom item shows the fields its layout puts on cards under its name; its picture makes room for them.
            List<FrameworkElement> facts = CustomTypes.Of(entity.EntityType) is CustomType customType ? CardFacts(entity, customType) : new List<FrameworkElement>();
            if (hasImage)
            {
                card.Children.Add(new Border
                {
                    Height = facts.Count > 0 ? CardFactsPicture : 100,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(12, 12, 12, 0),
                    Background = B("#F5F5F3"),
                    CornerRadius = new CornerRadius(10),
                    Child = new Image
                    {
                        Source = picture,
                        Stretch = Stretch.Uniform,
                        Margin = new Thickness(8)
                    }
                });
            }

            TextBlock title = new TextBlock
            {
                Text = entity.Name,
                FontSize = hasImage ? 14 : 18,
                FontWeight = FontWeights.Bold,
                Foreground = B("#1C1B19"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                LineHeight = hasImage ? 18 : 23,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = hasImage ? 36 : 69,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = hasImage ? new Thickness(16, 118, 16, 30) : new Thickness(20)
            };
            card.Children.Add(title);

            TextBlock subtitle = new TextBlock
            {
                Text = GetCardSubtitle(entity),
                FontSize = 11,
                Foreground = B("#8A8A87"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = size - 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 16, 14)
            };
            card.Children.Add(subtitle);
            if (entity.EntityType == EntityTypes.Vehicle)
                ShowVehicleOnCard(entity, title, subtitle);
            else if (CustomTypes.IsCustom(entity.EntityType))
            {
                ShowDueOnCard(entity, subtitle);
                if (facts.Count > 0)
                    ShowFactsOnCard(card, title, facts, hasImage);
            }
            if (subtitle.Text.Length > 0)
                subtitle.ToolTip = subtitle.Text; // the whole line, when a longer translation is cut short

            if (hasImage && EntityImages.Count(entity) > 1)
                card.Children.Add(PictureCountBadge(EntityImages.Count(entity)));

            selection.Attach(card, entity); // first: a Ctrl or Shift click selects, and neither opens nor drags
            CardEffects.AttachHoverLift(card, shadow);
            CardEffects.AttachClick(card, () => OpenItem(entity));
            cardDrag.Attach(card, entity);

            ContextMenu menu = new ContextMenu();
            MenuItem edit = new MenuItem { Header = Loc.T("Common.Edit") };
            edit.Click += (s, e) => OpenItem(entity);
            MenuItem delete = new MenuItem { Header = Loc.T("Common.Delete") };
            delete.Click += (s, e) => DeleteEntity(entity);
            MenuItem reminder = new MenuItem { Header = Loc.T("Common.AddReminder") };
            reminder.Click += (s, e) => new AddReminderWindow(entity.Name).ShowDialog();
            menu.Items.Add(edit);
            menu.Items.Add(delete);
            menu.Items.Add(reminder);
            menu.Items.Add(ExportMenu.Submenu(() => entity.Name, (path, format) => ExportService.ExportEntity(entity, path, format)));
            menu.Items.Add(TransferUI.MenuEntry(() => TransferUI.ExportItem(Window.GetWindow(this), entity)));
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => entity.Name, Loc.T("Common.CopyName")));
            card.ContextMenu = menu;
            CardEffects.AttachMenuButton(card, button => selection.OpenMenu(card, button));

            return card;
        }

        // Top right of the card's picture when the item has several: a picture symbol and how many.
        private static FrameworkElement PictureCountBadge(int count)
        {
            StackPanel content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = "", // Segoe MDL2: Photo2
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = B("#1C1B19"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });
            content.Children.Add(new TextBlock
            {
                Text = count.ToString(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = B("#1C1B19"),
                VerticalAlignment = VerticalAlignment.Center
            });
            Border badge = new Border
            {
                Background = B("#E6FFFFFF"),
                BorderBrush = B("#ECECE9"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(7, 2, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 18, 18, 0),
                ToolTip = Loc.Plural("Entity.PictureCount", count),
                Child = content
            };
            AutomationProperties.SetName(badge, Loc.Plural("Entity.PictureCount", count));
            return badge;
        }

        // A vehicle's card: the plate under the make and model (one line each), and what comes due first (ShowDueOnCard).
        private static void ShowVehicleOnCard(Entity entity, TextBlock title, TextBlock subtitle)
        {
            if (!string.IsNullOrWhiteSpace(entity.Plate))
            {
                title.TextWrapping = TextWrapping.NoWrap;
                title.Inlines.Add(new LineBreak());
                title.Inlines.Add(new Run(entity.Plate)
                {
                    FontSize = 11,
                    FontWeight = FontWeights.Normal,
                    Foreground = (Brush)Application.Current.FindResource("TextSub")
                });
            }

            ShowDueOnCard(entity, subtitle);
        }

        // What comes due first in the subtitle's place ("Inspection in 12 days", "Service in 800 km"), highlighted when it's
        // close or past (Expiry); nothing when nothing comes due.
        private static void ShowDueOnCard(Entity entity, TextBlock subtitle)
        {
            // A reading that is close or passed (a service due at so many km) when it is at least as urgent as the date.
            Deadline? nearest = EntityTypes.NearestExpiry(entity);
            DistanceDue? reading = EntityTypes.DistanceDues(entity).FirstOrDefault();
            ExpiryLevel dateLevel = nearest == null ? ExpiryLevel.Later : Expiry.Level(nearest.Date, DateTime.Today);
            if (nearest == null && reading == null)
            {
                subtitle.Text = "";
                return;
            }
            bool byReading = reading != null && (nearest == null || reading.Level >= dateLevel);
            ExpiryLevel level = byReading ? reading!.Level : dateLevel;
            subtitle.Text = byReading ? Loc.F("Entity.ExpiryLine", reading!.Name, reading.Describe(entity))
                : Loc.F("Entity.ExpiryLine", nearest!.Name, Expiry.Describe(nearest.Date, DateTime.Today));
            subtitle.Foreground = Expiry.Brush(level);
            subtitle.FontWeight = Expiry.Weight(level);
        }

        // The picture's height on a card that shows fields under the name (100 on the others).
        private const double CardFactsPicture = 64;

        // The fields a custom layout puts on cards (CustomLayout.CardFields) that this item has filled: "Brand  Acme", a
        // line each, the name muted.
        private static List<FrameworkElement> CardFacts(Entity entity, CustomType custom)
        {
            List<FrameworkElement> facts = new List<FrameworkElement>();
            foreach (FieldDefinition field in custom.CardFields)
            {
                string shown = field.Show(field.Read(entity), entity).Replace('\n', ' ');
                if (shown.Length == 0)
                    continue;
                TextBlock line = new TextBlock
                {
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    TextAlignment = TextAlignment.Center,
                    Margin = new Thickness(0, 1, 0, 1),
                    ToolTip = field.Label + ": " + shown
                };
                line.Inlines.Add(new Run(field.Label + "  ") { FontSize = 11, Foreground = (Brush)Application.Current.FindResource("TextSub") });
                line.Inlines.Add(new Run(shown) { Foreground = (Brush)Application.Current.FindResource("TextBody") });
                facts.Add(line);
            }
            return facts;
        }

        // The name on one line with the facts under it, below the (smaller) picture, or in the middle of a card without one.
        private static void ShowFactsOnCard(Grid card, TextBlock title, List<FrameworkElement> facts, bool hasImage)
        {
            card.Children.Remove(title);
            title.Margin = new Thickness(0, 0, 0, 4);
            title.VerticalAlignment = VerticalAlignment.Top;
            if (hasImage)
            {
                title.TextWrapping = TextWrapping.NoWrap;
                title.MaxHeight = double.PositiveInfinity;
            }
            else
                title.MaxHeight = 46; // two lines at most, the facts under it
            StackPanel text = new StackPanel
            {
                VerticalAlignment = hasImage ? VerticalAlignment.Top : VerticalAlignment.Center,
                Margin = hasImage ? new Thickness(16, 12 + CardFactsPicture + 8, 16, 30) : new Thickness(16, 16, 16, 30)
            };
            text.Children.Add(title);
            foreach (FrameworkElement fact in facts)
                text.Children.Add(fact);
            card.Children.Insert(card.Children.Count - 1, text); // under the subtitle, which stays last
        }

        private string GetCardSubtitle(Entity e)
        {
            string color = e.Color ?? "";
            int sizeCount = e.Sizes?.Count ?? 0;
            string sizes = sizeCount == 0 ? "" : Loc.Plural("Entity.SizeCount", sizeCount);
            return string.IsNullOrEmpty(color) || string.IsNullOrEmpty(sizes)
                ? color + sizes : color + " · " + sizes;
        }

        // ---- The item's window ------------------------------------------

        // From global search or Home: switch to the item's tab and open it. Nothing if it's gone since.
        public void Reveal(Guid entityId)
        {
            Entity entity = unreadable ? null : entities.FirstOrDefault(e => e.Id == entityId);
            if (entity == null) return;

            tabStrip.Select(entity.TabId);
            OpenItem(entity);
        }

        // The item's own window (ItemWindow), saving through this view; the cards show the result once it closes.
        private void OpenItem(Entity entity)
        {
            ItemWindow window = new ItemWindow(entity, new ItemWindow.Host
            {
                Save = Replace,
                EditBasics = EditEntity,
                Delete = DeleteEntity,
                Reread = Reread,
                ChangeLayout = ChangeLayout,
                ItemsHolding = ItemsHolding
            })
            { Owner = Window.GetWindow(this) };
            window.ShowDialog();
            RenderEntities();
        }

        // ---- A custom collection's layout ---------------------------------------------------------

        // The layout editor on its own (the memory's menu, a new custom collection): the item window on an empty item that
        // is never stored, in its editor; it closes when that is done.
        public void EditLayout()
        {
            if (unreadable || !custom)
                return;
            Entity preview = new Entity { Id = Guid.Empty, EntityType = CustomTypes.TypeOf(memory.Id), Name = memory.Name };
            ItemWindow window = new ItemWindow(preview, new ItemWindow.Host
            {
                Reread = id => preview,
                ChangeLayout = ChangeLayout,
                ItemsHolding = ItemsHolding,
                Preview = true
            })
            { Owner = Window.GetWindow(this) };
            window.EditLayout();
            window.ShowDialog();
            RenderEntities();
        }

        // How many items hold something under any of the keys (a block's or a field's data, Entity.Details or Records).
        private int ItemsHolding(IReadOnlyCollection<string> keys) => entities.Count(e => keys.Any(key => Holds(e, key)));

        private static bool Holds(Entity entity, string key) =>
            entity.Details?.ContainsKey(key) == true || entity.Records?.GetValueOrDefault(key)?.Count > 0;

        // A layout change, saved for every item as one undo step: the layout, then the items without the data of the blocks
        // and fields it removed, their files (once nothing uses them and no step can bring them back) and their documents'
        // reminders. If the items can't be saved, the layout is put back. False if it couldn't be saved (said).
        private bool ChangeLayout(ItemWindow.LayoutChange change)
        {
            if (unreadable)
                return false;
            using UndoChange step = UndoChange.Begin(UndoService.Page, UndoParts.Layout(memory), UndoParts.Entities(memory), UndoParts.Reminders());
            CustomLayout? before = CustomTypes.OfMemory(memory)?.Layout;
            if (!CustomLayoutStore.TrySave(memory, change.Layout))
            {
                Msg.Show(Loc.F("Custom.Layout.SaveFailed", MemoryFiles.Layout(memory)), Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            List<(Entity Before, Entity After)> stripped = new List<(Entity, Entity)>();
            for (int i = 0; i < entities.Count; i++)
            {
                if (!change.Removed.Any(key => Holds(entities[i], key)))
                    continue;
                Entity after = JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(entities[i]))!;
                foreach (string key in change.Removed)
                {
                    FieldDefinition.WriteDetail(after, key, null);
                    if (after.Records != null && after.Records.Remove(key) && after.Records.Count == 0)
                        after.Records = null;
                }
                stripped.Add((entities[i], after));
                entities[i] = after;
            }
            if (stripped.Count > 0 && !SaveEntities())
            {
                foreach ((Entity was, Entity after) in stripped)
                    entities[entities.IndexOf(after)] = was;
                if (before != null)
                    CustomLayoutStore.TrySave(memory, before);
                return false;
            }
            if (stripped.Count > 0)
            {
                EntityFiles.DeleteUnreferenced(stripped.Select(x => x.Before), entities);
                // The removed lists' records are no longer in the layout, so their reminders go by their Ids.
                WarnReminders(VehicleReminders.Remove(stripped.SelectMany(x => change.Removed
                    .SelectMany(key => RecordTree.Walk(x.Before.Records?.GetValueOrDefault(key) ?? new List<EntityRecord>()))
                    .Select(r => r.Record.Id))));
            }
            step.Commit(change.Label, change.Destructive, change.Target);
            RenderEntities();
            return true;
        }

        // ---- CRUD ---------------------------------------------------------

        // A new item opens in its window straight away, for everything the short form doesn't ask.
        private void AddEntityButton_Click(object sender, RoutedEventArgs e)
        {
            string type = CustomTypes.TypeOf(memory.Id);
            if (!custom)
            {
                EntityTypeSelector selector = new EntityTypeSelector();
                if (selector.ShowDialog() != true)
                    return;
                type = selector.SelectedType;
            }
            EntityForm form = new EntityForm(type);
            if (form.ShowDialog() != true)
                return;

            Entity added = form.ResultEntity;
            added.Id = Guid.NewGuid();
            added.TabId = tabStrip.ActiveTabId;
            added.Order = entities.Count == 0 ? 0 : entities.Max(x => x.Order) + 1;
            added.CreatedDate = DateTime.Now;
            List<string> imported = ApplyPictures(added, form.Pictures);

            entities.Add(added);
            if (!SaveEntities())
            {
                entities.Remove(added);
                RemoveCopies(imported);
                RenderEntities();
                return;
            }
            RenderEntities();
            OpenItem(added);
        }

        // The form edits a copy (Id, tab, place and fields it doesn't show come along). Newly picked pictures are
        // copied in; if the item can't be saved, it stays as it was and the new copies go again. Returns the item as
        // saved, or null.
        private Entity EditEntity(Entity entity)
        {
            EntityForm form = new EntityForm(entity);
            if (form.ShowDialog() != true)
                return null;

            Entity edited = form.ResultEntity;
            List<string> imported = ApplyPictures(edited, form.Pictures);
            bool saved = Replace(edited);
            if (!saved)
                RemoveCopies(imported);
            RenderEntities();
            return saved ? edited : null;
        }

        // Puts an edited copy in place of the item with its Id (by Id, since a reload replaces the list's objects) and
        // saves. Once saved, the files the item no longer has are deleted (if no other item uses them, and no undo step
        // can still bring them back) and its documents' reminders follow. False if it can't be saved, or was deleted
        // meanwhile: nothing changes then. Each save is one undo step for this item (its window undoes only those).
        private bool Replace(Entity edited)
        {
            int index = entities.FindIndex(x => x.Id == edited.Id);
            if (index < 0)
                return false;

            using UndoChange change = Record();
            Entity original = entities[index];
            entities[index] = edited;
            if (!SaveEntities())
            {
                entities[index] = original;
                return false;
            }
            EntityFiles.DeleteUnreferenced(new[] { original }, entities);
            WarnReminders(VehicleReminders.Follow(original, edited));
            change.Commit(Describe(original, edited, out bool deleted), deleted, edited.Id);
            return true;
        }

        // What an item's change did, in words: the record it deleted or added ("“Insurance” deleted"), or "Changes to …".
        // deleted: a record went, which the toast offers to undo.
        private static string Describe(Entity before, Entity after, out bool deleted)
        {
            List<string> gone = new List<string>(), added = new List<string>();
            foreach (RecordDefinition kind in EntityTypes.Records(after.EntityType))
            {
                List<(EntityRecord Record, IReadOnlyList<EntityRecord> Above)> was = RecordTree.Walk(kind.Read(before)).ToList();
                List<(EntityRecord Record, IReadOnlyList<EntityRecord> Above)> now = RecordTree.Walk(kind.Read(after)).ToList();
                HashSet<Guid> wasIds = was.Select(x => x.Record.Id).ToHashSet(), nowIds = now.Select(x => x.Record.Id).ToHashSet();
                // The records that went or came, not the ones under them that went or came along.
                gone.AddRange(was.Where(x => !nowIds.Contains(x.Record.Id) && x.Above.All(a => nowIds.Contains(a.Id))).Select(x => kind.Title(x.Record)));
                added.AddRange(now.Where(x => !wasIds.Contains(x.Record.Id) && x.Above.All(a => wasIds.Contains(a.Id))).Select(x => kind.Title(x.Record)));
            }
            deleted = gone.Count > 0;
            if (gone.Count > 0)
                return gone.Count == 1 ? Loc.F("Undo.Deleted", gone[0]) : Loc.Plural("Undo.Deleted.Records", gone.Count);
            if (added.Count == 1)
                return Loc.F("Undo.Added", added[0]);
            return Loc.F("Undo.Changed", after.Name);
        }

        private static void WarnReminders(string problem)
        {
            if (problem != null)
                Msg.Show(problem, Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Gives the item the form's pictures: picked files are copied in. One that can't be copied is left out and
        // named in a message; everything else the form did (removals, order, cover) is kept. Returns the new copies
        // made, for RemoveCopies if the item can't be saved.
        private static List<string> ApplyPictures(Entity entity, IReadOnlyList<PictureSlot> slots)
        {
            EntityImages.Apply(entity, slots, out List<string> imported, out List<string> failed);
            if (failed.Count > 0)
                Msg.Show(Loc.F("Collection.PictureFailed", string.Join(", ", failed.Select(Path.GetFileName)), EntityImages.Store.Folder),
                    Loc.T("Collection.PictureFailed.Caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return imported;
        }

        // Copies made for an item that then wasn't saved: nothing else refers to them.
        private static void RemoveCopies(IEnumerable<string> names)
        {
            foreach (string name in names)
                EntityImages.DeleteCopy(name);
        }

        // True once the item is gone (asked first).
        private bool DeleteEntity(Entity entity)
        {
            var result = Msg.Show(Loc.F("Common.DeleteNamed", entity.Name), Loc.T("Common.ConfirmDelete"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
                return false;

            RemoveEntities(new[] { entity });
            RenderEntities();
            return !entities.Any(x => x.Id == entity.Id);
        }

        // Deleting items: one from its menu, or a multi-selection's Delete. Their files and reminders go once the list
        // is saved without them. If that couldn't be saved, the list is read back from disk, so the items reappear
        // instead of looking deleted until some later save drops them (and leaves their files behind). One undo step:
        // until it ends, their files stay on disk, and an undo brings the items and their reminders back.
        private void RemoveEntities(IEnumerable<Entity> doomed)
        {
            // By Id: a caller may hold a copy of an item (a reload replaces the list's objects).
            HashSet<Guid> ids = doomed.Select(e => e.Id).ToHashSet();
            using UndoChange change = Record();
            List<Entity> removed = entities.Where(e => ids.Contains(e.Id)).ToList();
            entities.RemoveAll(e => ids.Contains(e.Id));

            if (!SaveEntities())
            {
                ReloadEntities();
                return;
            }
            EntityFiles.DeleteUnreferenced(removed, entities);
            WarnReminders(removed.Select(entity => VehicleReminders.Follow(entity, null)).ToList().FirstOrDefault(problem => problem != null));
            change.Commit(removed.Count == 1 ? Loc.F("Undo.Deleted", removed[0].Name) : Loc.Plural("Undo.Deleted.Items", removed.Count),
                destructive: true, removed.Count == 1 ? removed[0].Id : null);
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
