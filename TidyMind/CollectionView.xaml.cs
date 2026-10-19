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
    public partial class CollectionView : UserControl
    {
        private readonly Profile memory;
        private List<Entity> entities;
        private Entity currentDetailEntity;
        private int detailPicture; // which of its pictures the detail card shows
        private readonly PictureBrowser detailPictures = new PictureBrowser(double.NaN, 150, canReorder: false, stripForOne: false);
        private readonly MemoryTabStrip tabStrip;
        private readonly DragReorder<Entity> cardDrag;
        private readonly CardSelectionController<Entity> selection;

        // Its items or tabs file couldn't be read: shown read-only (ShowUnreadable), and never saved over.
        private bool unreadable;
        private string unreadableFile, unreadableProblem;

        // The search box rebuilds the cards after a short pause in typing, not on every key: rebuilding a big grid per
        // keystroke lags.
        private readonly DispatcherTimer searchPause = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };

        // saveProfiles: writes the main window's list of memories, which `profile` belongs to (Edit Background).
        public CollectionView(Profile profile, Func<bool> saveProfiles)
        {
            InitializeComponent();
            DetailPictures.Content = detailPictures;
            detailPictures.Picked += index => ShowDetailPicture(currentDetailEntity, index);

            // With the detail card open, Left and Right go through its pictures (unless the keyboard is in its text).
            PreviewKeyDown += (s, e) =>
            {
                if (OverlayGrid.Visibility == Visibility.Visible && (e.Key == Key.Left || e.Key == Key.Right)
                    && Keyboard.Modifiers == ModifierKeys.None && !(e.OriginalSource is System.Windows.Controls.Primitives.TextBoxBase)
                    && detailPictures.Browse(e.Key == Key.Left ? -1 : +1))
                    e.Handled = true;
            };

            searchPause.Tick += (s, e) =>
            {
                searchPause.Stop();
                RenderEntities();
            };
            memory = profile;
            // An open item overlay isn't empty page: no background menu there.
            MemoryPageBackground.Attach(Page, BackgroundLayer, profile, saveProfiles, HeaderPlate, EmptyCard, OverlayGrid);
            TitleText.Text = profile.Name;

            // Counts, cards, an open detail card and the reason a file couldn't be read, in the new language; names
            // and notes are the user's own.
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
                if (OverlayGrid.Visibility == Visibility.Visible && currentDetailEntity != null)
                {
                    FillDetail(currentDetailEntity);
                    ShowDetailPicture(currentDetailEntity, detailPicture);
                }
            });

            // An unreadable file is left alone: the page says why, and nothing on it can change or save items.
            if (!EntityStore.TryLoad(memory, out entities, out string problem))
            {
                ShowUnreadable(MemoryFiles.Items(memory), problem);
                return;
            }
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
                });
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
            Toolbar.Visibility = Visibility.Collapsed;
            SelectionBar.Visibility = Visibility.Collapsed;
            TabStrip.Visibility = Visibility.Collapsed;
            SubtitleText.Text = Loc.T("Collection.Kind");
            EmptyState.Text = Loc.F("Memory.Unreadable", file, problem);
            EmptyState.Visibility = Visibility.Visible;
        }

        // Drag-and-drop on the cards. Other tabs' items keep their relative order; everything is renumbered 0..n.
        private void MoveEntity(Entity dragged, Entity target, bool after)
        {
            DragReorder<Entity>.Move(entities, dragged, target, after);
            for (int i = 0; i < entities.Count; i++)
                entities[i].Order = i;

            SaveEntities();
            RenderEntities();
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
            EmptyState.Text = Loc.T(inTab.Count == 0 ? "Collection.Empty" : "Collection.NoMatch");
            selection.EndRender(); // what's no longer on screen (other tab, filtered out) drops out of the selection
        }

        // ---- Multi-selection ----------------------------------------------

        // While anything is selected, the selection bar stands in for the search box and Add button.
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
            if (!BulkDelete.Confirm(Window.GetWindow(this), "Bulk.Delete.Items", doomed.Select(x => x.Name).ToList(), Loc.T("Bulk.CantUndo")))
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

            if (hasImage)
            {
                card.Children.Add(new Border
                {
                    Height = 100,
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
            if (subtitle.Text.Length > 0)
                subtitle.ToolTip = subtitle.Text; // the whole line, when a longer translation is cut short

            if (hasImage && EntityImages.Count(entity) > 1)
                card.Children.Add(PictureCountBadge(EntityImages.Count(entity)));

            selection.Attach(card, entity); // first: a Ctrl or Shift click selects, and neither opens nor drags
            CardEffects.AttachHoverLift(card, shadow);
            CardEffects.AttachClick(card, () => OpenDetail(entity));
            cardDrag.Attach(card, entity);

            ContextMenu menu = new ContextMenu();
            MenuItem edit = new MenuItem { Header = Loc.T("Common.Edit") };
            edit.Click += (s, e) => { if (entity.EntityType == EntityTypes.Vehicle) OpenDetail(entity); else EditEntity(entity); };
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

        // A vehicle's card: the plate under the make and model (one line each), and its nearest expiry date in the
        // subtitle's place ("Inspection in 12 days"), highlighted when it's close or past (Expiry).
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

            Deadline? nearest = EntityTypes.NearestExpiry(entity);
            if (nearest == null)
            {
                subtitle.Text = "";
                return;
            }
            ExpiryLevel level = Expiry.Level(nearest.Date, DateTime.Today);
            subtitle.Text = Loc.F("Entity.ExpiryLine", nearest.Name, Expiry.Describe(nearest.Date, DateTime.Today));
            subtitle.Foreground = Expiry.Brush(level);
            subtitle.FontWeight = Expiry.Weight(level);
        }

        private string GetCardSubtitle(Entity e)
        {
            string color = e.Color ?? "";
            int sizeCount = e.Sizes?.Count ?? 0;
            string sizes = sizeCount == 0 ? "" : Loc.Plural("Entity.SizeCount", sizeCount);
            return string.IsNullOrEmpty(color) || string.IsNullOrEmpty(sizes)
                ? color + sizes : color + " · " + sizes;
        }

        // ---- Detail overlay ----------------------------------------------

        // From global search: switch to the item's tab and open its detail card. Nothing if it's gone since.
        public void Reveal(Guid entityId)
        {
            Entity entity = unreadable ? null : entities.FirstOrDefault(e => e.Id == entityId);
            if (entity == null) return;

            tabStrip.Select(entity.TabId);
            OpenDetail(entity);
        }

        // A vehicle opens its own window (VehicleWindow); any other item the detail card.
        private void OpenDetail(Entity entity)
        {
            if (entity.EntityType == EntityTypes.Vehicle)
            {
                OpenVehicle(entity);
                return;
            }
            currentDetailEntity = entity;
            ShowDetailPicture(entity, 0);
            FillDetail(entity);

            OverlayGrid.Visibility = Visibility.Visible;
            OverlayGrid.Opacity = 0;
            DetailCard.RenderTransform = new TranslateTransform(0, 20);

            OverlayGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            DetailCard.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(180)));
        }

        // The detail card's pictures: the cover first, then whichever is picked with an arrow, a key or the strip
        // (arrows and strip only with more than one picture), decoded off the UI thread while the card shows the item.
        private void ShowDetailPicture(Entity entity, int index)
        {
            List<string> names = EntityImages.Names(entity);
            detailPicture = names.Count == 0 ? 0 : Math.Clamp(index, 0, names.Count - 1);
            detailPictures.ShowStored(names, detailPicture, () => currentDetailEntity == entity && OverlayGrid.Visibility == Visibility.Visible);
        }

        // The detail card's rows; filled again after a language switch.
        private void FillDetail(Entity entity)
        {
            DetailName.Text = entity.Name;
            DetailPanel.Children.Clear();
            AddDetailRow(Loc.T("Entity.Field.Brand"), entity.Brand);
            AddDetailRow(Loc.T("Entity.Field.Color"), entity.Color);
            AddDetailRow(Loc.T("Entity.Field.Material"), Choices.Label(entity.Material));
            AddDetailRow(Loc.T("Entity.Field.Season"), Choices.Label(entity.Season));
            AddDetailRow(Loc.T("Entity.Field.PurchasePrice"), entity.PurchasePrice);
            AddSizesDetail(entity);
            AddDetailRow(Loc.T("Entity.Field.Notes"), entity.Notes);
        }

        private void AddDetailRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            DetailPanel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = (Brush)FindResource("TextSub"), // 5.1:1 on the white card
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 2)
            });
            DetailPanel.Children.Add(TextCopy.Selectable(value, 13, B("#1C1B19")));
        }

        private void AddSizesDetail(Entity entity)
        {
            if (entity.Sizes == null || entity.Sizes.Count == 0) return;

            DetailPanel.Children.Add(new TextBlock
            {
                Text = Loc.T("Entity.Sizes"),
                Foreground = (Brush)FindResource("TextSub"),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 4)
            });

            foreach (SizeQuantity sq in entity.Sizes)
            {
                TextBox size = TextCopy.Selectable(sq.Size + " - " + Choices.Label(sq.Condition) + " x" + sq.Quantity, 13, B("#1C1B19"));
                size.Margin = new Thickness(0, 0, 0, 4);
                DetailPanel.Children.Add(size);
            }
        }

        private void CloseOverlay()
        {
            DoubleAnimation fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130));
            fadeOut.Completed += (s, e) => OverlayGrid.Visibility = Visibility.Collapsed;
            OverlayGrid.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void CloseOverlay_Click(object sender, RoutedEventArgs e) => CloseOverlay();

        private void OverlayGrid_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => CloseOverlay();

        private void DetailCard_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

        private void DetailEditButton_Click(object sender, RoutedEventArgs e)
        {
            CloseOverlay();
            EditEntity(currentDetailEntity);
        }

        private void DetailDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            Entity target = currentDetailEntity;
            var result = Msg.Show(Loc.F("Common.DeleteNamed", target.Name), Loc.T("Common.ConfirmDelete"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                RemoveEntities(new[] { target });
                CloseOverlay();
                RenderEntities();
            }
        }

        // ---- CRUD ---------------------------------------------------------

        // A new vehicle opens in its window straight away, for everything the short form doesn't ask.
        private void AddEntityButton_Click(object sender, RoutedEventArgs e)
        {
            EntityTypeSelector selector = new EntityTypeSelector();
            if (selector.ShowDialog() != true)
                return;
            EntityForm form = new EntityForm(selector.SelectedType);
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
            if (added.EntityType == EntityTypes.Vehicle)
                OpenVehicle(added);
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
        // saves. Once saved, the files the item no longer has are deleted (if no other item uses them) and its
        // documents' reminders follow. False if it can't be saved, or was deleted meanwhile: nothing changes then.
        private bool Replace(Entity edited)
        {
            int index = entities.FindIndex(x => x.Id == edited.Id);
            if (index < 0)
                return false;

            Entity original = entities[index];
            entities[index] = edited;
            if (!SaveEntities())
            {
                entities[index] = original;
                return false;
            }
            EntityFiles.DeleteUnreferenced(new[] { original }, entities);
            WarnReminders(VehicleReminders.Follow(original, edited));
            return true;
        }

        // The vehicle's window, saving through this view; the cards show the result once it closes.
        private void OpenVehicle(Entity vehicle)
        {
            VehicleWindow window = new VehicleWindow(vehicle, new VehicleWindow.Host
            {
                Save = Replace,
                EditBasics = EditEntity,
                Delete = DeleteEntity
            })
            { Owner = Window.GetWindow(this) };
            window.ShowDialog();
            RenderEntities();
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
        // instead of looking deleted until some later save drops them (and leaves their files behind).
        private void RemoveEntities(IEnumerable<Entity> doomed)
        {
            // By Id: a caller may hold a copy of an item (a reload replaces the list's objects).
            HashSet<Guid> ids = doomed.Select(e => e.Id).ToHashSet();
            List<Entity> removed = entities.Where(e => ids.Contains(e.Id)).ToList();
            entities.RemoveAll(e => ids.Contains(e.Id));

            if (!SaveEntities())
            {
                ReloadEntities();
                return;
            }
            EntityFiles.DeleteUnreferenced(removed, entities);
            WarnReminders(removed.Select(entity => VehicleReminders.Follow(entity, null)).ToList().FirstOrDefault(problem => problem != null));
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
