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
            foreach (Entity entity in entities)
            {
                Border card = CreateEntityCard(entity);
                EntityPanel.Children.Add(card);
            }
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

            TextBlock typeTag = new TextBlock();
            typeTag.Text = entity.EntityType ?? "";
            typeTag.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#569CD6"));
            typeTag.FontSize = 10;
            typeTag.HorizontalAlignment = HorizontalAlignment.Center;
            typeTag.Margin = new Thickness(8, 0, 8, 2);

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
            stack.Children.Add(typeTag);
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
            switch (e.EntityType)
            {
                case "Clothing":
                    string color = e.Color ?? "";
                    string sizes = e.Sizes != null && e.Sizes.Count > 0 ? e.Sizes.Count + " size(s)" : "";
                    return string.IsNullOrEmpty(color) || string.IsNullOrEmpty(sizes)
                        ? color + sizes : color + " - " + sizes;
                case "Car":
                case "Motorcycle":
                    return string.Join(" ", new[] { e.Brand, e.Model, e.Year }).Trim();
                case "Book":
                    return e.Author ?? "";
                case "Game":
                    return e.Platform ?? "";
                case "Movie":
                    return e.Year ?? "";
                case "Electronics":
                    return string.Join(" ", new[] { e.Brand, e.DeviceType }).Trim();
                case "Pet":
                    return string.Join(" ", new[] { e.Species, e.Breed }).Trim();
                case "Plant":
                    return e.Species ?? "";
                case "Property":
                    return e.PropertyType ?? "";
                case "Medicine":
                    return e.MedicineType ?? "";
                case "Artwork":
                    return e.Artist ?? "";
                case "Custom":
                    return "Custom";
                default:
                    return "";
            }
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

            AddDetailRow("Type", entity.EntityType);

            switch (entity.EntityType)
            {
                case "Clothing":
                    AddDetailRow("Brand", entity.Brand);
                    AddDetailRow("Color", entity.Color);
                    AddDetailRow("Material", entity.Material);
                    AddDetailRow("Season", entity.Season);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    AddSizesDetail(entity);
                    break;

                case "Car":
                case "Motorcycle":
                    AddDetailRow("Brand", entity.Brand);
                    AddDetailRow("Model", entity.Model);
                    AddDetailRow("Year", entity.Year);
                    AddDetailRow("Color", entity.Color);
                    AddDetailRow("License Plate", entity.LicensePlate);
                    AddDetailRow("Mileage", entity.Mileage);
                    AddDetailRow("Fuel Type", entity.FuelType);
                    AddDetailRow("Insurance Expiry", entity.InsuranceExpiry);
                    AddDetailRow("Tech Inspection Expiry", entity.TechExpiry);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    break;

                case "Book":
                    AddDetailRow("Author", entity.Author);
                    AddDetailRow("Publisher", entity.Publisher);
                    AddDetailRow("Year", entity.Year);
                    AddDetailRow("ISBN", entity.ISBN);
                    AddDetailRow("Genre", entity.Genre);
                    AddDetailRow("Language", entity.Language);
                    AddDetailRow("Condition", entity.Condition);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    break;

                case "Game":
                    AddDetailRow("Platform", entity.Platform);
                    AddDetailRow("Genre", entity.Genre);
                    AddDetailRow("Publisher", entity.Publisher);
                    AddDetailRow("Year", entity.Year);
                    AddDetailRow("Condition", entity.Condition);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    break;

                case "Movie":
                    AddDetailRow("Director", entity.Director);
                    AddDetailRow("Genre", entity.Genre);
                    AddDetailRow("Year", entity.Year);
                    AddDetailRow("Format", entity.Format);
                    AddDetailRow("Condition", entity.Condition);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    break;

                case "Electronics":
                    AddDetailRow("Device Type", entity.DeviceType);
                    AddDetailRow("Brand", entity.Brand);
                    AddDetailRow("Model", entity.Model);
                    AddDetailRow("Serial Number", entity.SerialNumber);
                    AddDetailRow("Condition", entity.Condition);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    AddDetailRow("Purchase Date", entity.PurchaseDate);
                    AddDetailRow("Warranty Expiry", entity.WarrantyExpiry);
                    break;

                case "Pet":
                    AddDetailRow("Species", entity.Species);
                    AddDetailRow("Breed", entity.Breed);
                    AddDetailRow("Color", entity.Color);
                    AddDetailRow("Gender", entity.Gender);
                    AddDetailRow("Birth Date", entity.BirthDate);
                    AddDetailRow("Chip Number", entity.ChipNumber);
                    AddDetailRow("Vaccine Expiry", entity.VaccineExpiry);
                    AddDetailRow("Vet", entity.Vet);
                    break;

                case "Plant":
                    AddDetailRow("Species", entity.Species);
                    AddDetailRow("Location", entity.Location);
                    AddDetailRow("Watering Frequency", entity.WateringFrequency);
                    AddDetailRow("Last Repotted", entity.LastRepotted);
                    break;

                case "Property":
                    AddDetailRow("Address", entity.Address);
                    AddDetailRow("Type", entity.PropertyType);
                    AddDetailRow("Area", entity.Area);
                    AddDetailRow("Rooms", entity.Rooms);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    AddDetailRow("Rent Price", entity.RentPrice);
                    break;

                case "Medicine":
                    AddDetailRow("Type", entity.MedicineType);
                    AddDetailRow("Manufacturer", entity.Manufacturer);
                    AddDetailRow("Dosage", entity.Dosage);
                    AddDetailRow("Expiry Date", entity.ExpiryDate);
                    AddDetailRow("Condition", entity.Condition);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    break;

                case "Artwork":
                    AddDetailRow("Artist", entity.Artist);
                    AddDetailRow("Type", entity.ArtworkType);
                    AddDetailRow("Material", entity.ArtMaterial);
                    AddDetailRow("Size", entity.Size);
                    AddDetailRow("Condition", entity.Condition);
                    AddDetailRow("Purchase Price", entity.PurchasePrice);
                    break;

                case "Custom":
                    if (entity.CustomFields != null)
                        foreach (var cf in entity.CustomFields)
                            AddDetailRow(cf.FieldName, cf.Value);
                    break;
            }

            AddDetailRow("Notes", entity.Notes);

            // Animáció
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
    }
}