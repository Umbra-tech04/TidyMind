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
        private readonly string profileName;
        private List<Entity> entities;
        private Entity currentDetailEntity;
        private readonly MemoryTabStrip tabStrip;
        private readonly DragReorder<Entity> cardDrag;
        private readonly CardSelectionController<Entity> selection;

        // saveProfiles: writes the main window's list of memories, which `profile` belongs to (Edit Background).
        public CollectionView(Profile profile, Func<bool> saveProfiles)
        {
            InitializeComponent();
            profileName = profile.Name;
            // An open item overlay isn't empty page: no background menu there.
            MemoryPageBackground.Attach(Page, BackgroundLayer, profile, saveProfiles, OverlayGrid);

            selection = new CardSelectionController<Entity>(this, CardScroll, CardArea, MarqueeCanvas,
                CardEffects.CardRadius, DeleteEntities);
            selection.SelectionChanged += UpdateSelectionBar;
            SelectionBar.DeleteClicked += () => selection.DeleteSelected();
            SelectionBar.CancelClicked += selection.ClearSelection;
            TitleText.Text = profile.Name;
            LoadEntities();

            // Cards sit 24px apart (12px margin each side); the drop line goes in the middle of that gap.
            cardDrag = new DragReorder<Entity>(EntityPanel, Orientation.Horizontal, 24, null, MoveEntity);

            tabStrip = new MemoryTabStrip(this, TabStrip, profileName, ProfileType.Collection, "item",
                tabId => entities.Count(e => e.TabId == tabId),
                (fromTab, toTab) =>
                {
                    foreach (Entity entity in entities.Where(e => e.TabId == fromTab))
                        entity.TabId = toTab;
                    SaveEntities();
                });
            tabStrip.TabsChanged += RenderEntities;

            AssignOrphansToFirstTab();
            tabStrip.Render();
            RenderEntities();
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

        private void LoadEntities()
        {
            string fileName = AppPaths.Data(profileName + "_entities.json");
            if (File.Exists(fileName))
                entities = JsonSerializer.Deserialize<List<Entity>>(File.ReadAllText(fileName)) ?? new List<Entity>();
            else
                entities = new List<Entity>();

            // Stable sort: files from before Order existed (all 0) keep their saved order.
            entities = entities.OrderBy(e => e.Order).ToList();
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

        // Written via a temporary file, so a crash or full disk mid-save leaves the previous file intact.
        // If it can't be written (locked, no access), says so; the file is then unchanged.
        private void SaveEntities()
        {
            string fileName = profileName + "_entities.json";
            if (!AtomicFile.TryWriteAllText(AppPaths.Data(fileName), JsonSerializer.Serialize(entities)))
                MessageBox.Show("Couldn't save: " + fileName + " can't be written. Your last change wasn't saved.",
                    "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void RenderEntities()
        {
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
            LoadEntities();
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
            bool hasImage = !string.IsNullOrEmpty(entity.ImagePath) && File.Exists(entity.ImagePath);

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
                        Source = new BitmapImage(new Uri(entity.ImagePath)),
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

        // From global search: switch to the item's tab and open its detail card.
        public void Reveal(int entityIndex)
        {
            if (entityIndex < 0 || entityIndex >= entities.Count) return;

            Entity entity = entities[entityIndex];
            tabStrip.Select(entity.TabId);
            OpenDetail(entity);
        }

        private void OpenDetail(Entity entity)
        {
            currentDetailEntity = entity;

            if (!string.IsNullOrEmpty(entity.ImagePath) && File.Exists(entity.ImagePath))
                DetailImage.Source = new BitmapImage(new Uri(entity.ImagePath));
            else
                DetailImage.Source = null;

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
                    form.ResultEntity.TabId = tabStrip.ActiveTabId;
                    form.ResultEntity.Order = entities.Count == 0 ? 0 : entities.Max(x => x.Order) + 1;
                    form.ResultEntity.CreatedDate = DateTime.Now;
                    entities.Add(form.ResultEntity);
                    SaveEntities();
                    RenderEntities();
                }
            }
        }

        private void EditEntity(Entity entity)
        {
            EntityForm form = new EntityForm(entity);
            if (form.ShowDialog() == true)
            {
                int index = entities.IndexOf(entity);
                form.ResultEntity.TabId = entity.TabId; // the form builds a fresh Entity; keep it in its tab and place
                form.ResultEntity.Order = entity.Order;
                form.ResultEntity.CreatedDate = entity.CreatedDate;
                entities[index] = form.ResultEntity;
                SaveEntities();
                RenderEntities();
            }
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

        // Deleting items: one from its menu, or a multi-selection's Delete.
        private void RemoveEntities(IEnumerable<Entity> doomed)
        {
            foreach (Entity entity in doomed.ToList())
                entities.Remove(entity);
            SaveEntities();
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
