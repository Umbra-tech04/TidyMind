using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    public partial class CollectionView : UserControl
    {
        private readonly Profile memory;
        private List<Entity> entities;
        private Entity currentDetailEntity;
        private readonly MemoryTabStrip tabStrip;
        private readonly DragReorder<Entity> cardDrag;
        private readonly CardSelectionController<Entity> selection;

        // Its items or tabs file couldn't be read: shown read-only (ShowUnreadable), and never saved over.
        private bool unreadable;

        // saveProfiles: writes the main window's list of memories, which `profile` belongs to (Edit Background).
        public CollectionView(Profile profile, Func<bool> saveProfiles)
        {
            InitializeComponent();
            memory = profile;
            // An open item overlay isn't empty page: no background menu there.
            MemoryPageBackground.Attach(Page, BackgroundLayer, profile, saveProfiles, OverlayGrid);
            TitleText.Text = profile.Name;

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

            selection = new CardSelectionController<Entity>(this, CardScroll, CardArea, MarqueeCanvas,
                CardEffects.CardRadius, DeleteEntities);
            selection.SelectionChanged += UpdateSelectionBar;
            SelectionBar.DeleteClicked += () => selection.DeleteSelected();
            SelectionBar.CancelClicked += selection.ClearSelection;

            // Cards sit 24px apart (12px margin each side); the drop line goes in the middle of that gap.
            cardDrag = new DragReorder<Entity>(EntityPanel, Orientation.Horizontal, 24, null, MoveEntity);

            tabStrip = new MemoryTabStrip(this, TabStrip, memory, tabs, "item",
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
        // so moving or deleting the original no longer loses it. One whose original is gone keeps its path (and shows
        // no picture). If the save fails, the copies are removed again and the items keep their old paths.
        private void CopyInLegacyImages()
        {
            List<(Entity Entity, string Original)> copied = new List<(Entity, string)>();
            foreach (Entity entity in entities.Where(e => EntityImages.IsLegacyPath(e.ImagePath) && File.Exists(e.ImagePath)))
            {
                string stored = EntityImages.Store.Import(entity.ImagePath);
                if (stored == null)
                    continue;
                copied.Add((entity, entity.ImagePath));
                entity.ImagePath = stored;
            }

            if (copied.Count == 0 || SaveEntities())
                return;

            foreach ((Entity entity, string original) in copied)
            {
                EntityImages.Store.Delete(entity.ImagePath);
                entity.ImagePath = original;
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
            selection?.ClearSelection();
            EntityPanel.Children.Clear();
            Toolbar.Visibility = Visibility.Collapsed;
            SelectionBar.Visibility = Visibility.Collapsed;
            TabStrip.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "COLLECTION";
            EmptyState.Text = "Couldn't read " + file + ":\n" + problem + "\n\nThe file was left as it is. "
                + "Fix it or restore it from a backup, then open this memory again.";
            EmptyState.TextWrapping = TextWrapping.Wrap;
            EmptyState.TextAlignment = TextAlignment.Center;
            EmptyState.MaxWidth = 520;
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

            string filter = SearchBox.Text == "Search..." ? "" : SearchBox.Text.Trim();
            List<Entity> inTab = entities.Where(e => e.TabId == tabStrip.ActiveTabId).ToList();

            int shown = 0;
            foreach (Entity entity in inTab)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    (entity.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                EntityPanel.Children.Add(CreateEntityCard(entity));
                shown++;
            }

            SubtitleText.Text = "COLLECTION  ·  " + inTab.Count + (inTab.Count == 1 ? " item" : " items");
            EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Text = inTab.Count == 0
                ? "No items in this tab yet. Click “+ Add Entity” to start."
                : "No items match your search.";
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
        // from disk.
        private bool DeleteEntities(IReadOnlyCollection<Entity> doomed)
        {
            string what = doomed.Count == 1 ? "1 item" : doomed.Count + " items";
            if (MessageBox.Show("Delete " + what + "? This can't be undone.", "Confirm Delete",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;

            RemoveEntities(doomed);
            ReloadEntities();
            RenderEntities();
            return true;
        }

        // ---- Search -------------------------------------------------------

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchBox.Text == "Search...")
            {
                SearchBox.Text = "";
                SearchBox.Foreground = B("#1C1B19");
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = "Search...";
                SearchBox.Foreground = B("#8A8A87");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchBox.Text == "Search...") return;
            RenderEntities();
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

            card.Children.Add(new TextBlock
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
            });

            card.Children.Add(new TextBlock
            {
                Text = GetCardSubtitle(entity),
                FontSize = 11,
                Foreground = B("#8A8A87"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = size - 32,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 16, 14)
            });

            selection.AttachCard(card, entity); // first: a Ctrl+click selects, and neither opens nor drags
            CardEffects.AttachHoverLift(card, shadow);
            CardEffects.AttachClick(card, () => OpenDetail(entity));
            cardDrag.Attach(card, entity);

            ContextMenu menu = new ContextMenu();
            MenuItem edit = new MenuItem { Header = "Edit" };
            edit.Click += (s, e) => EditEntity(entity);
            MenuItem delete = new MenuItem { Header = "Delete" };
            delete.Click += (s, e) => DeleteEntity(entity);
            MenuItem reminder = new MenuItem { Header = "Add Reminder" };
            reminder.Click += (s, e) => new AddReminderWindow(entity.Name).ShowDialog();
            menu.Items.Add(edit);
            menu.Items.Add(delete);
            menu.Items.Add(reminder);
            menu.Items.Add(ExportMenu.Submenu(() => entity.Name, (path, format) => ExportService.ExportEntity(entity, path, format)));
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => entity.Name, "Copy name"));
            card.ContextMenu = menu;

            return card;
        }

        private string GetCardSubtitle(Entity e)
        {
            string color = e.Color ?? "";
            int sizeCount = e.Sizes?.Count ?? 0;
            string sizes = sizeCount == 0 ? "" : sizeCount + (sizeCount == 1 ? " size" : " sizes");
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

        private void OpenDetail(Entity entity)
        {
            currentDetailEntity = entity;

            DetailImage.Source = EntityImages.Load(entity);

            DetailName.Text = entity.Name;
            DetailPanel.Children.Clear();

            AddDetailRow("Brand", entity.Brand);
            AddDetailRow("Color", entity.Color);
            AddDetailRow("Material", entity.Material);
            AddDetailRow("Season", entity.Season);
            AddDetailRow("Purchase Price", entity.PurchasePrice);
            AddSizesDetail(entity);
            AddDetailRow("Notes", entity.Notes);

            OverlayGrid.Visibility = Visibility.Visible;
            OverlayGrid.Opacity = 0;
            DetailCard.RenderTransform = new TranslateTransform(0, 20);

            OverlayGrid.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            DetailCard.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(20, 0, TimeSpan.FromMilliseconds(180)));
        }

        private void AddDetailRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            DetailPanel.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = B("#8A8A87"),
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
                Text = "Sizes",
                Foreground = B("#8A8A87"),
                FontSize = 11,
                Margin = new Thickness(0, 8, 0, 4)
            });

            foreach (SizeQuantity sq in entity.Sizes)
            {
                TextBox size = TextCopy.Selectable(sq.Size + " - " + sq.Condition + " x" + sq.Quantity, 13, B("#1C1B19"));
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
            var result = MessageBox.Show("Delete '" + target.Name + "'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                RemoveEntities(new[] { target });
                CloseOverlay();
                RenderEntities();
            }
        }

        // ---- CRUD ---------------------------------------------------------

        private void AddEntityButton_Click(object sender, RoutedEventArgs e)
        {
            EntityTypeSelector selector = new EntityTypeSelector();
            if (selector.ShowDialog() == true)
            {
                EntityForm form = new EntityForm(selector.SelectedType);
                if (form.ShowDialog() == true)
                {
                    Entity added = form.ResultEntity;
                    added.Id = Guid.NewGuid();
                    added.TabId = tabStrip.ActiveTabId;
                    added.Order = entities.Count == 0 ? 0 : entities.Max(x => x.Order) + 1;
                    added.CreatedDate = DateTime.Now;
                    bool imported = CopyInPickedImage(added, form.PickedImageFile);

                    entities.Add(added);
                    if (!SaveEntities())
                    {
                        entities.Remove(added);
                        if (imported)
                            EntityImages.DeleteCopy(added.ImagePath);
                    }
                    RenderEntities();
                }
            }
        }

        // The form edits a copy (Id, tab, place and fields it doesn't show come along). A newly picked picture is
        // copied in, and the old copy deleted only once the item is saved without it; if the save fails, the item
        // stays as it was and the new copy goes again.
        private void EditEntity(Entity entity)
        {
            EntityForm form = new EntityForm(entity);
            if (form.ShowDialog() != true)
                return;

            Entity edited = form.ResultEntity;
            bool imported = CopyInPickedImage(edited, form.PickedImageFile);

            int index = entities.IndexOf(entity);
            entities[index] = edited;
            if (SaveEntities())
            {
                if (imported)
                    EntityImages.DeleteCopy(entity.ImagePath);
            }
            else
            {
                entities[index] = entity;
                if (imported)
                    EntityImages.DeleteCopy(edited.ImagePath);
            }
            RenderEntities();
        }

        // Points the item at a copy of the picture picked in the form. False if none was picked, or it couldn't be
        // copied (said so; the item keeps its old picture).
        private static bool CopyInPickedImage(Entity entity, string pickedFile)
        {
            if (pickedFile == null)
                return false;

            string stored = EntityImages.Store.Import(pickedFile);
            if (stored == null)
            {
                MessageBox.Show("Couldn't copy that image into " + EntityImages.Store.Folder + ", so the new picture "
                    + "wasn't added. The rest of the item is saved as usual.", "Couldn't Add Picture",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            entity.ImagePath = stored;
            return true;
        }

        private void DeleteEntity(Entity entity)
        {
            var result = MessageBox.Show("Delete '" + entity.Name + "'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                RemoveEntities(new[] { entity });
                RenderEntities();
            }
        }

        // Deleting items: one from its menu, or a multi-selection's Delete. Their pictures go once the list is saved
        // without them. If that couldn't be saved, the list is read back from disk, so the items reappear instead of
        // looking deleted until some later save drops them (and leaves their pictures behind).
        private void RemoveEntities(IEnumerable<Entity> doomed)
        {
            List<Entity> removed = doomed.ToList();
            foreach (Entity entity in removed)
                entities.Remove(entity);

            if (!SaveEntities())
            {
                ReloadEntities();
                return;
            }
            foreach (Entity entity in removed)
                EntityImages.DeleteCopy(entity.ImagePath);
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
