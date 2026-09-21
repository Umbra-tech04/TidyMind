using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    public partial class CollectionWindow : Window
    {
        private string profileName;
        private List<Entity> entities;
        private Entity currentDetailEntity;

        public CollectionWindow(string profileName)
        {
            InitializeComponent();
            this.profileName = profileName;
            this.Title = profileName + " - TidyMind";
            CollectionTitle.Text = profileName;
            LoadEntities();
            RenderEntities();

            this.Activated += CollectionWindow_Activated;
            this.Deactivated += CollectionWindow_Deactivated;
        }

        private void CollectionWindow_Activated(object sender, EventArgs e)
        {
            QuickNotesButton.Visibility = Visibility.Visible;
        }

        private void CollectionWindow_Deactivated(object sender, EventArgs e)
        {
            QuickNotesButton.Visibility = Visibility.Collapsed;
        }

        private void LoadEntities()
        {
            string fileName = profileName + "_entities.json";
            if (File.Exists(fileName))
            {
                string json = File.ReadAllText(fileName);
                entities = JsonSerializer.Deserialize<List<Entity>>(json);
            }
            else
            {
                entities = new List<Entity>();
            }
        }

        private void SaveEntities()
        {
            string fileName = profileName + "_entities.json";
            string json = JsonSerializer.Serialize(entities);
            File.WriteAllText(fileName, json);
        }

        private void RenderEntities()
        {
            EntityPanel.Children.Clear();

            string filter = SearchBox.Text == "Search..." ? "" : SearchBox.Text.Trim();

            foreach (Entity entity in entities)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    entity.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                Border card = CreateEntityCard(entity);
                EntityPanel.Children.Add(card);
            }
        }

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchBox.Text == "Search...")
            {
                SearchBox.Text = "";
                SearchBox.Foreground = Brushes.White;
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = "Search...";
                SearchBox.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#858585"));
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchBox.Text == "Search...") return;
            RenderEntities();
        }

        private Border CreateEntityCard(Entity entity)
        {
            Border card = new Border();
            card.Width = 160;
            card.Margin = new Thickness(10);
            card.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#252526"));
            card.CornerRadius = new CornerRadius(8);
            card.Cursor = System.Windows.Input.Cursors.Hand;
            card.Tag = entity;

            StackPanel stack = new StackPanel();

            Border imgBorder = new Border();
            imgBorder.Height = 130;
            imgBorder.Background = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#1E1E1E"));
            imgBorder.CornerRadius = new CornerRadius(8, 8, 0, 0);

            System.Windows.Controls.Image img = new System.Windows.Controls.Image();
            img.Stretch = Stretch.Uniform;
            img.Margin = new Thickness(4);
            if (!string.IsNullOrEmpty(entity.ImagePath) && File.Exists(entity.ImagePath))
                img.Source = new BitmapImage(new System.Uri(entity.ImagePath));
            imgBorder.Child = img;

            TextBlock name = new TextBlock();
            name.Text = entity.Name;
            name.Foreground = Brushes.White;
            name.FontSize = 13;
            name.FontWeight = FontWeights.Bold;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.TextWrapping = TextWrapping.Wrap;
            name.TextAlignment = TextAlignment.Center;
            name.Margin = new Thickness(8, 8, 8, 2);

            TextBlock details = new TextBlock();
            details.Text = GetCardSubtitle(entity);
            details.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#858585"));
            details.FontSize = 11;
            details.HorizontalAlignment = HorizontalAlignment.Center;
            details.TextWrapping = TextWrapping.Wrap;
            details.TextAlignment = TextAlignment.Center;
            details.Margin = new Thickness(8, 0, 8, 8);

            stack.Children.Add(imgBorder);
            stack.Children.Add(name);
            stack.Children.Add(details);
            card.Child = stack;

            ContextMenu menu = new ContextMenu();

            MenuItem editItem = new MenuItem();
            editItem.Header = "Edit";
            editItem.Tag = entity;
            editItem.Click += EditEntity_Click;

            MenuItem deleteItem = new MenuItem();
            deleteItem.Header = "Delete";
            deleteItem.Tag = entity;
            deleteItem.Click += DeleteEntity_Click;

            menu.Items.Add(editItem);
            menu.Items.Add(deleteItem);
            card.ContextMenu = menu;

            card.MouseLeftButtonUp += (s, e) => OpenDetail(entity);

            return card;
        }

        private string GetCardSubtitle(Entity e)
        {
            string color = e.Color ?? "";
            string sizes = e.Sizes != null && e.Sizes.Count > 0 ? e.Sizes.Count + " size(s)" : "";
            return string.IsNullOrEmpty(color) || string.IsNullOrEmpty(sizes)
                ? color + sizes : color + " - " + sizes;
        }

        private void OpenDetail(Entity entity)
        {
            currentDetailEntity = entity;

            if (!string.IsNullOrEmpty(entity.ImagePath) && File.Exists(entity.ImagePath))
                DetailImage.Source = new BitmapImage(new System.Uri(entity.ImagePath));
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
            DetailCard.RenderTransform = new System.Windows.Media.TranslateTransform(0, 20);

            DoubleAnimation fadeIn = new DoubleAnimation(0, 1, System.TimeSpan.FromMilliseconds(200));
            DoubleAnimation slideUp = new DoubleAnimation(20, 0, System.TimeSpan.FromMilliseconds(200));

            OverlayGrid.BeginAnimation(OpacityProperty, fadeIn);
            DetailCard.RenderTransform.BeginAnimation(
                System.Windows.Media.TranslateTransform.YProperty, slideUp);
        }

        private void AddDetailRow(string label, string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;

            TextBlock labelBlock = new TextBlock();
            labelBlock.Text = label;
            labelBlock.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#858585"));
            labelBlock.FontSize = 11;
            labelBlock.Margin = new Thickness(0, 6, 0, 2);

            TextBlock valueBlock = new TextBlock();
            valueBlock.Text = value;
            valueBlock.Foreground = Brushes.White;
            valueBlock.FontSize = 13;
            valueBlock.TextWrapping = TextWrapping.Wrap;

            DetailPanel.Children.Add(labelBlock);
            DetailPanel.Children.Add(valueBlock);
        }

        private void AddSizesDetail(Entity entity)
        {
            if (entity.Sizes == null || entity.Sizes.Count == 0) return;

            TextBlock label = new TextBlock();
            label.Text = "Sizes";
            label.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#858585"));
            label.FontSize = 11;
            label.Margin = new Thickness(0, 6, 0, 4);
            DetailPanel.Children.Add(label);

            foreach (SizeQuantity sq in entity.Sizes)
            {
                TextBlock row = new TextBlock();
                row.Text = $"{sq.Size} - {sq.Condition} x{sq.Quantity}";
                row.Foreground = Brushes.White;
                row.FontSize = 13;
                row.Margin = new Thickness(0, 0, 0, 4);
                DetailPanel.Children.Add(row);
            }
        }

        private void CloseOverlay()
        {
            DoubleAnimation fadeOut = new DoubleAnimation(1, 0, System.TimeSpan.FromMilliseconds(150));
            fadeOut.Completed += (s, e) => OverlayGrid.Visibility = Visibility.Collapsed;
            OverlayGrid.BeginAnimation(OpacityProperty, fadeOut);
        }

        private void CloseOverlay_Click(object sender, RoutedEventArgs e) => CloseOverlay();

        private void OverlayGrid_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => CloseOverlay();

        private void DetailCard_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
            => e.Handled = true;

        private void DetailEditButton_Click(object sender, RoutedEventArgs e)
        {
            CloseOverlay();
            EntityForm form = new EntityForm(currentDetailEntity);
            bool? result = form.ShowDialog();

            if (result == true)
            {
                int index = entities.IndexOf(currentDetailEntity);
                entities[index] = form.ResultEntity;
                SaveEntities();
                RenderEntities();
            }
        }

        private void DetailDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                $"Delete '{currentDetailEntity.Name}'?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                entities.Remove(currentDetailEntity);
                SaveEntities();
                CloseOverlay();
                RenderEntities();
            }
        }

        private void AddEntityButton_Click(object sender, RoutedEventArgs e)
        {
            EntityTypeSelector selector = new EntityTypeSelector();
            bool? selectorResult = selector.ShowDialog();

            if (selectorResult == true)
            {
                EntityForm form = new EntityForm(selector.SelectedType);
                bool? formResult = form.ShowDialog();

                if (formResult == true)
                {
                    entities.Add(form.ResultEntity);
                    SaveEntities();
                    RenderEntities();
                }
            }
        }

        private void EditEntity_Click(object sender, RoutedEventArgs e)
        {
            Entity entity = (Entity)((MenuItem)sender).Tag;
            EntityForm form = new EntityForm(entity);
            bool? result = form.ShowDialog();

            if (result == true)
            {
                int index = entities.IndexOf(entity);
                entities[index] = form.ResultEntity;
                SaveEntities();
                RenderEntities();
            }
        }

        private void DeleteEntity_Click(object sender, RoutedEventArgs e)
        {
            Entity entity = (Entity)((MenuItem)sender).Tag;

            var result = MessageBox.Show(
                $"Delete '{entity.Name}'?",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                entities.Remove(entity);
                SaveEntities();
                RenderEntities();
            }
        }

        private void SwitchProfileButton_Click(object sender, RoutedEventArgs e)
        {
            ((App)Application.Current).SwitchProfile(this);
        }

        private void QuickNotesButton_Click(object sender, RoutedEventArgs e)
        {
            QuickNotesWindow.ShowOrFocus();
        }
    }
}
