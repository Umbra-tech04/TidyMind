using Microsoft.Win32;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    public partial class EntityForm : Window
    {
        public Entity ResultEntity { get; private set; }
        private string entityType;
        private Entity existingEntity;

        // Közös
        private TextBox nameBox;
        private TextBox notesBox;
        private TextBlock imagePathText;
        private System.Windows.Controls.Image imagePreview;

        // Clothing
        private List<SizeQuantity> sizes = new List<SizeQuantity>();
        private StackPanel sizesPanel;
        private TextBox brandBox, colorBox, priceBox;
        private ComboBox materialBox, seasonBox;

        // Car / Motorcycle
        private TextBox modelBox, yearBox, plateBox, mileageBox, insuranceBox, techBox;
        private ComboBox fuelBox;

        // Book
        private TextBox authorBox, publisherBox, isbnBox, langBox;
        private ComboBox genreBox, conditionBox;

        // Game
        private TextBox gameTitleBox, gamePublisherBox, gameYearBox;
        private ComboBox platformBox, gameGenreBox, gameConditionBox;

        // Movie
        private TextBox directorBox, movieYearBox;
        private ComboBox movieGenreBox, formatBox, movieConditionBox;

        // Electronics
        private TextBox elecModelBox, serialBox, purchaseDateBox, warrantyBox, elecPriceBox;
        private ComboBox deviceTypeBox, elecConditionBox;

        // Pet
        private TextBox speciesBox, breedBox, birthDateBox, chipBox, vaccineBox, vetBox;
        private ComboBox genderBox;

        // Plant
        private TextBox locationBox, wateringBox, repottedBox;

        // Property
        private TextBox addressBox, areaBox, roomsBox, propPriceBox, rentBox;
        private ComboBox propTypeBox;

        // Medicine
        private TextBox manufacturerBox, dosageBox, expiryBox, medPriceBox;
        private ComboBox medTypeBox, medConditionBox;

        // Artwork
        private TextBox artistBox, artSizeBox, artPriceBox;
        private ComboBox artTypeBox, artMaterialBox, artConditionBox;

        // Custom
        private List<CustomField> customFields = new List<CustomField>();
        private StackPanel customFieldsPanel;

        public EntityForm(string entityType)
        {
            InitializeComponent();
            this.entityType = entityType;
            FormTitle.Text = "Add " + entityType;
            this.Title = "Add " + entityType;
            BuildForm();
        }

        public EntityForm(Entity existing)
        {
            InitializeComponent();
            this.entityType = existing.EntityType;
            this.existingEntity = existing;
            FormTitle.Text = "Edit " + entityType;
            this.Title = "Edit " + entityType;
            BuildForm();
            FillForm(existing);
        }

        private void BuildForm()
        {
            FormPanel.Children.Clear();
            AddImagePicker();
            AddTextBox("Name", ref nameBox);

            switch (entityType)
            {
                case "Clothing": BuildClothing(); break;
                case "Car": BuildCar(); break;
                case "Motorcycle": BuildMotorcycle(); break;
                case "Book": BuildBook(); break;
                case "Game": BuildGame(); break;
                case "Movie": BuildMovie(); break;
                case "Electronics": BuildElectronics(); break;
                case "Pet": BuildPet(); break;
                case "Plant": BuildPlant(); break;
                case "Property": BuildProperty(); break;
                case "Medicine": BuildMedicine(); break;
                case "Artwork": BuildArtwork(); break;
                case "Custom": BuildCustom(); break;
            }

            AddTextBoxMultiline("Notes", ref notesBox);
        }

        // ─── FORM BUILDERS ───────────────────────────────────────────

        private void BuildClothing()
        {
            AddTextBox("Brand", ref brandBox);
            AddTextBox("Color", ref colorBox);
            AddComboBox("Material", ref materialBox,
                new[] { "Cotton", "Polyester", "Wool", "Linen", "Silk", "Denim", "Leather", "Synthetic", "Mixed" });
            AddComboBox("Season", ref seasonBox,
                new[] { "All-season", "Summer", "Winter", "Spring/Autumn" });
            AddTextBox("Purchase Price", ref priceBox);
            AddSizesSection();
        }

        private void BuildCar()
        {
            AddTextBox("Brand", ref brandBox);
            AddTextBox("Model", ref modelBox);
            AddTextBox("Year", ref yearBox);
            AddTextBox("Color", ref colorBox);
            AddTextBox("License Plate", ref plateBox);
            AddTextBox("Mileage (km)", ref mileageBox);
            AddComboBox("Fuel Type", ref fuelBox,
                new[] { "Petrol", "Diesel", "Electric", "Hybrid", "LPG" });
            AddTextBox("Insurance Expiry", ref insuranceBox);
            AddTextBox("Tech Inspection Expiry", ref techBox);
            AddTextBox("Purchase Price", ref priceBox);
        }

        private void BuildMotorcycle()
        {
            AddTextBox("Brand", ref brandBox);
            AddTextBox("Model", ref modelBox);
            AddTextBox("Year", ref yearBox);
            AddTextBox("Color", ref colorBox);
            AddTextBox("License Plate", ref plateBox);
            AddTextBox("Mileage (km)", ref mileageBox);
            AddComboBox("Fuel Type", ref fuelBox,
                new[] { "Petrol", "Electric" });
            AddTextBox("Insurance Expiry", ref insuranceBox);
            AddTextBox("Tech Inspection Expiry", ref techBox);
            AddTextBox("Purchase Price", ref priceBox);
        }

        private void BuildBook()
        {
            AddTextBox("Author", ref authorBox);
            AddTextBox("Publisher", ref publisherBox);
            AddTextBox("Year", ref yearBox);
            AddTextBox("ISBN", ref isbnBox);
            AddComboBox("Genre", ref genreBox,
                new[] { "Fiction", "Non-fiction", "Science", "History", "Fantasy", "Mystery", "Biography", "Self-help", "Other" });
            AddTextBox("Language", ref langBox);
            AddComboBox("Condition", ref conditionBox,
                new[] { "New", "Good", "Worn" });
            AddTextBox("Purchase Price", ref priceBox);
        }

        private void BuildGame()
        {
            AddComboBox("Platform", ref platformBox,
                new[] { "PC", "PlayStation 5", "PlayStation 4", "Xbox Series X", "Xbox One", "Nintendo Switch", "Mobile", "Other" });
            AddComboBox("Genre", ref gameGenreBox,
                new[] { "Action", "RPG", "Strategy", "Sports", "Racing", "Horror", "Adventure", "Simulation", "Other" });
            AddTextBox("Publisher", ref gamePublisherBox);
            AddTextBox("Year", ref gameYearBox);
            AddComboBox("Condition", ref gameConditionBox,
                new[] { "New", "Good", "Worn", "Digital" });
            AddTextBox("Purchase Price", ref priceBox);
        }

        private void BuildMovie()
        {
            AddTextBox("Director", ref directorBox);
            AddComboBox("Genre", ref movieGenreBox,
                new[] { "Action", "Drama", "Comedy", "Horror", "Sci-fi", "Documentary", "Animation", "Thriller", "Other" });
            AddTextBox("Year", ref movieYearBox);
            AddComboBox("Format", ref formatBox,
                new[] { "DVD", "Blu-ray", "4K UHD", "Digital", "VHS" });
            AddComboBox("Condition", ref movieConditionBox,
                new[] { "New", "Good", "Worn", "Digital" });
            AddTextBox("Purchase Price", ref priceBox);
        }

        private void BuildElectronics()
        {
            AddComboBox("Device Type", ref deviceTypeBox,
                new[] { "Laptop", "Desktop PC", "Phone", "Tablet", "TV", "Monitor", "Camera", "Headphones", "Console", "Other" });
            AddTextBox("Brand", ref brandBox);
            AddTextBox("Model", ref elecModelBox);
            AddTextBox("Serial Number", ref serialBox);
            AddComboBox("Condition", ref elecConditionBox,
                new[] { "New", "Good", "Worn", "For repair" });
            AddTextBox("Purchase Price", ref elecPriceBox);
            AddTextBox("Purchase Date", ref purchaseDateBox);
            AddTextBox("Warranty Expiry", ref warrantyBox);
        }

        private void BuildPet()
        {
            AddTextBox("Species", ref speciesBox);
            AddTextBox("Breed", ref breedBox);
            AddTextBox("Color", ref colorBox);
            AddComboBox("Gender", ref genderBox,
                new[] { "Male", "Female", "Unknown" });
            AddTextBox("Birth Date", ref birthDateBox);
            AddTextBox("Chip Number", ref chipBox);
            AddTextBox("Vaccine Expiry", ref vaccineBox);
            AddTextBox("Vet", ref vetBox);
        }

        private void BuildPlant()
        {
            AddTextBox("Species", ref speciesBox);
            AddTextBox("Location", ref locationBox);
            AddTextBox("Watering Frequency", ref wateringBox);
            AddTextBox("Last Repotted", ref repottedBox);
        }

        private void BuildProperty()
        {
            AddTextBox("Address", ref addressBox);
            AddComboBox("Property Type", ref propTypeBox,
                new[] { "Apartment", "House", "Land", "Office", "Garage", "Other" });
            AddTextBox("Area (m²)", ref areaBox);
            AddTextBox("Rooms", ref roomsBox);
            AddTextBox("Purchase Price", ref propPriceBox);
            AddTextBox("Rent Price", ref rentBox);
        }

        private void BuildMedicine()
        {
            AddComboBox("Type", ref medTypeBox,
                new[] { "Prescription", "OTC", "Supplement", "Vitamin", "Other" });
            AddTextBox("Manufacturer", ref manufacturerBox);
            AddTextBox("Dosage", ref dosageBox);
            AddTextBox("Expiry Date", ref expiryBox);
            AddComboBox("Condition", ref medConditionBox,
                new[] { "Sealed", "Opened", "Expired" });
            AddTextBox("Purchase Price", ref medPriceBox);
        }

        private void BuildArtwork()
        {
            AddTextBox("Artist", ref artistBox);
            AddComboBox("Type", ref artTypeBox,
                new[] { "Painting", "Sculpture", "Photography", "Print", "Drawing", "Digital", "Other" });
            AddComboBox("Material", ref artMaterialBox,
                new[] { "Oil", "Acrylic", "Watercolor", "Pencil", "Ink", "Mixed", "Digital", "Other" });
            AddTextBox("Size", ref artSizeBox);
            AddComboBox("Condition", ref artConditionBox,
                new[] { "Mint", "Good", "Fair", "Poor" });
            AddTextBox("Purchase Price", ref artPriceBox);
        }

        private void BuildCustom()
        {
            customFields = new List<CustomField>();
            AddLabel("Custom Fields");
            customFieldsPanel = new StackPanel();
            FormPanel.Children.Add(customFieldsPanel);

            Button addFieldBtn = new Button();
            addFieldBtn.Content = "+ Add Field";
            addFieldBtn.Width = 130;
            addFieldBtn.Height = 30;
            addFieldBtn.HorizontalAlignment = HorizontalAlignment.Left;
            addFieldBtn.Margin = new Thickness(0, 8, 0, 0);
            addFieldBtn.Click += (s, e) =>
            {
                customFields.Add(new CustomField { FieldName = "", Value = "" });
                RenderCustomFields();
            };
            FormPanel.Children.Add(addFieldBtn);
        }

        private void RenderCustomFields()
        {
            customFieldsPanel.Children.Clear();
            foreach (CustomField cf in customFields)
            {
                StackPanel row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                row.Margin = new Thickness(0, 0, 0, 6);

                TextBox fieldName = new TextBox();
                fieldName.Width = 130;
                fieldName.Height = 32;
                fieldName.Text = cf.FieldName;
                fieldName.VerticalContentAlignment = VerticalAlignment.Center;
                fieldName.Margin = new Thickness(0, 0, 8, 0);
                fieldName.TextChanged += (s, e) => cf.FieldName = fieldName.Text;

                TextBox fieldValue = new TextBox();
                fieldValue.Width = 180;
                fieldValue.Height = 32;
                fieldValue.Text = cf.Value;
                fieldValue.VerticalContentAlignment = VerticalAlignment.Center;
                fieldValue.Margin = new Thickness(0, 0, 8, 0);
                fieldValue.TextChanged += (s, e) => cf.Value = fieldValue.Text;

                Button removeBtn = new Button();
                removeBtn.Content = "✕";
                removeBtn.Width = 32;
                removeBtn.Height = 32;
                removeBtn.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#8B1A1A"));
                removeBtn.Click += (s, e) =>
                {
                    customFields.Remove(cf);
                    RenderCustomFields();
                };

                row.Children.Add(fieldName);
                row.Children.Add(fieldValue);
                row.Children.Add(removeBtn);
                customFieldsPanel.Children.Add(row);
            }
        }

        // ─── SIZES SECTION (Clothing) ─────────────────────────────────

        private void AddSizesSection()
        {
            AddLabel("Sizes, Conditions & Quantities");
            sizesPanel = new StackPanel();
            FormPanel.Children.Add(sizesPanel);

            Button addSizeBtn = new Button();
            addSizeBtn.Content = "+ Add Size";
            addSizeBtn.Width = 120;
            addSizeBtn.Height = 30;
            addSizeBtn.HorizontalAlignment = HorizontalAlignment.Left;
            addSizeBtn.Margin = new Thickness(0, 8, 0, 0);
            addSizeBtn.Click += (s, e) =>
            {
                sizes.Add(new SizeQuantity { Size = "", Condition = "New", Quantity = 1 });
                RenderSizes();
            };
            FormPanel.Children.Add(addSizeBtn);
        }

        private void RenderSizes()
        {
            sizesPanel.Children.Clear();
            foreach (SizeQuantity sq in sizes)
            {
                StackPanel row = new StackPanel();
                row.Orientation = Orientation.Horizontal;
                row.Margin = new Thickness(0, 0, 0, 6);

                TextBox sizeBox = new TextBox();
                sizeBox.Width = 60;
                sizeBox.Height = 32;
                sizeBox.Text = sq.Size;
                sizeBox.VerticalContentAlignment = VerticalAlignment.Center;
                sizeBox.Margin = new Thickness(0, 0, 6, 0);
                sizeBox.TextChanged += (s, e) => sq.Size = sizeBox.Text;

                ComboBox condBox = new ComboBox();
                condBox.Width = 120;
                condBox.Height = 32;
                condBox.Margin = new Thickness(0, 0, 6, 0);
                foreach (var c in new[] { "New", "Good", "Worn", "To donate", "To throw away" })
                    condBox.Items.Add(new ComboBoxItem { Content = c });
                SetComboBox(condBox, sq.Condition);
                condBox.SelectionChanged += (s, e) =>
                    sq.Condition = ((ComboBoxItem)condBox.SelectedItem)?.Content.ToString();

                Button minusBtn = new Button();
                minusBtn.Content = "−";
                minusBtn.Width = 32;
                minusBtn.Height = 32;
                minusBtn.Margin = new Thickness(0, 0, 4, 0);
                minusBtn.Click += (s, e) => { if (sq.Quantity > 0) sq.Quantity--; RenderSizes(); };

                TextBlock qty = new TextBlock();
                qty.Text = sq.Quantity.ToString();
                qty.Width = 24;
                qty.TextAlignment = TextAlignment.Center;
                qty.VerticalAlignment = VerticalAlignment.Center;
                qty.Foreground = Brushes.White;
                qty.FontSize = 14;

                Button plusBtn = new Button();
                plusBtn.Content = "+";
                plusBtn.Width = 32;
                plusBtn.Height = 32;
                plusBtn.Margin = new Thickness(4, 0, 6, 0);
                plusBtn.Click += (s, e) => { sq.Quantity++; RenderSizes(); };

                Button removeBtn = new Button();
                removeBtn.Content = "✕";
                removeBtn.Width = 32;
                removeBtn.Height = 32;
                removeBtn.Background = new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString("#8B1A1A"));
                removeBtn.Click += (s, e) => { sizes.Remove(sq); RenderSizes(); };

                row.Children.Add(sizeBox);
                row.Children.Add(condBox);
                row.Children.Add(minusBtn);
                row.Children.Add(qty);
                row.Children.Add(plusBtn);
                row.Children.Add(removeBtn);
                sizesPanel.Children.Add(row);
            }
        }

        // ─── HELPERS ─────────────────────────────────────────────────

        private void AddImagePicker()
        {
            AddLabel("Image");
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 0, 0, 4);

            Button pickBtn = new Button();
            pickBtn.Content = "Choose Image";
            pickBtn.Height = 32;
            pickBtn.Padding = new Thickness(12, 0, 12, 0);
            pickBtn.Click += PickImageButton_Click;

            imagePathText = new TextBlock();
            imagePathText.Text = "No image selected";
            imagePathText.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#858585"));
            imagePathText.FontSize = 11;
            imagePathText.VerticalAlignment = VerticalAlignment.Center;
            imagePathText.Margin = new Thickness(12, 0, 0, 0);

            row.Children.Add(pickBtn);
            row.Children.Add(imagePathText);
            FormPanel.Children.Add(row);

            imagePreview = new System.Windows.Controls.Image();
            imagePreview.Height = 140;
            imagePreview.HorizontalAlignment = HorizontalAlignment.Left;
            imagePreview.Stretch = Stretch.Uniform;
            imagePreview.Margin = new Thickness(0, 6, 0, 12);
            FormPanel.Children.Add(imagePreview);
        }

        private void AddLabel(string text)
        {
            TextBlock label = new TextBlock();
            label.Text = text;
            label.Foreground = new SolidColorBrush(
                (Color)ColorConverter.ConvertFromString("#858585"));
            label.FontSize = 11;
            label.Margin = new Thickness(0, 12, 0, 4);
            FormPanel.Children.Add(label);
        }

        private void AddTextBox(string label, ref TextBox box)
        {
            AddLabel(label);
            box = new TextBox();
            box.Height = 32;
            box.VerticalContentAlignment = VerticalAlignment.Center;
            box.Margin = new Thickness(0, 0, 0, 0);
            FormPanel.Children.Add(box);
        }

        private void AddTextBoxMultiline(string label, ref TextBox box)
        {
            AddLabel(label);
            box = new TextBox();
            box.Height = 80;
            box.TextWrapping = TextWrapping.Wrap;
            box.AcceptsReturn = true;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            FormPanel.Children.Add(box);
        }

        private void AddComboBox(string label, ref ComboBox box, string[] items)
        {
            AddLabel(label);
            box = new ComboBox();
            box.Height = 32;
            foreach (var item in items)
                box.Items.Add(new ComboBoxItem { Content = item });
            box.SelectedIndex = 0;
            FormPanel.Children.Add(box);
        }

        private void SetComboBox(ComboBox box, string value)
        {
            if (string.IsNullOrEmpty(value)) { box.SelectedIndex = 0; return; }
            foreach (ComboBoxItem item in box.Items)
            {
                if (item.Content.ToString() == value)
                {
                    box.SelectedItem = item;
                    return;
                }
            }
            box.SelectedIndex = 0;
        }

        private string GetComboValue(ComboBox box)
            => ((ComboBoxItem)box?.SelectedItem)?.Content.ToString();

        // ─── IMAGE PICKER ─────────────────────────────────────────────

        private void PickImageButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = "Image files|*.jpg;*.jpeg;*.png;*.bmp;*.gif";
            if (dialog.ShowDialog() == true)
            {
                imagePathText.Text = dialog.FileName;
                imagePreview.Source = new BitmapImage(new System.Uri(dialog.FileName));
            }
        }

        private void ScrollViewer_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            var sv = sender as ScrollViewer;
            sv?.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta / 3.0);
            e.Handled = true;
        }

        // ─── FILL FORM (Edit mode) ────────────────────────────────────

        private void FillForm(Entity e)
        {
            if (nameBox != null) nameBox.Text = e.Name;
            if (notesBox != null) notesBox.Text = e.Notes;

            if (!string.IsNullOrEmpty(e.ImagePath))
            {
                imagePathText.Text = e.ImagePath;
                imagePreview.Source = new BitmapImage(new System.Uri(e.ImagePath));
            }

            switch (entityType)
            {
                case "Clothing":
                    if (brandBox != null) brandBox.Text = e.Brand;
                    if (colorBox != null) colorBox.Text = e.Color;
                    if (priceBox != null) priceBox.Text = e.PurchasePrice;
                    SetComboBox(materialBox, e.Material);
                    SetComboBox(seasonBox, e.Season);
                    if (e.Sizes != null)
                        foreach (var sq in e.Sizes)
                            sizes.Add(new SizeQuantity { Size = sq.Size, Condition = sq.Condition, Quantity = sq.Quantity });
                    RenderSizes();
                    break;

                case "Car":
                case "Motorcycle":
                    if (brandBox != null) brandBox.Text = e.Brand;
                    if (modelBox != null) modelBox.Text = e.Model;
                    if (yearBox != null) yearBox.Text = e.Year;
                    if (colorBox != null) colorBox.Text = e.Color;
                    if (plateBox != null) plateBox.Text = e.LicensePlate;
                    if (mileageBox != null) mileageBox.Text = e.Mileage;
                    if (insuranceBox != null) insuranceBox.Text = e.InsuranceExpiry;
                    if (techBox != null) techBox.Text = e.TechExpiry;
                    if (priceBox != null) priceBox.Text = e.PurchasePrice;
                    SetComboBox(fuelBox, e.FuelType);
                    break;

                case "Book":
                    if (authorBox != null) authorBox.Text = e.Author;
                    if (publisherBox != null) publisherBox.Text = e.Publisher;
                    if (yearBox != null) yearBox.Text = e.Year;
                    if (isbnBox != null) isbnBox.Text = e.ISBN;
                    if (langBox != null) langBox.Text = e.Language;
                    if (priceBox != null) priceBox.Text = e.PurchasePrice;
                    SetComboBox(genreBox, e.Genre);
                    SetComboBox(conditionBox, e.Condition);
                    break;

                case "Game":
                    if (gamePublisherBox != null) gamePublisherBox.Text = e.Publisher;
                    if (gameYearBox != null) gameYearBox.Text = e.Year;
                    if (priceBox != null) priceBox.Text = e.PurchasePrice;
                    SetComboBox(platformBox, e.Platform);
                    SetComboBox(gameGenreBox, e.Genre);
                    SetComboBox(gameConditionBox, e.Condition);
                    break;

                case "Movie":
                    if (directorBox != null) directorBox.Text = e.Director;
                    if (movieYearBox != null) movieYearBox.Text = e.Year;
                    if (priceBox != null) priceBox.Text = e.PurchasePrice;
                    SetComboBox(movieGenreBox, e.Genre);
                    SetComboBox(formatBox, e.Format);
                    SetComboBox(movieConditionBox, e.Condition);
                    break;

                case "Electronics":
                    if (brandBox != null) brandBox.Text = e.Brand;
                    if (elecModelBox != null) elecModelBox.Text = e.Model;
                    if (serialBox != null) serialBox.Text = e.SerialNumber;
                    if (elecPriceBox != null) elecPriceBox.Text = e.PurchasePrice;
                    if (purchaseDateBox != null) purchaseDateBox.Text = e.PurchaseDate;
                    if (warrantyBox != null) warrantyBox.Text = e.WarrantyExpiry;
                    SetComboBox(deviceTypeBox, e.DeviceType);
                    SetComboBox(elecConditionBox, e.Condition);
                    break;

                case "Pet":
                    if (speciesBox != null) speciesBox.Text = e.Species;
                    if (breedBox != null) breedBox.Text = e.Breed;
                    if (colorBox != null) colorBox.Text = e.Color;
                    if (birthDateBox != null) birthDateBox.Text = e.BirthDate;
                    if (chipBox != null) chipBox.Text = e.ChipNumber;
                    if (vaccineBox != null) vaccineBox.Text = e.VaccineExpiry;
                    if (vetBox != null) vetBox.Text = e.Vet;
                    SetComboBox(genderBox, e.Gender);
                    break;

                case "Plant":
                    if (speciesBox != null) speciesBox.Text = e.Species;
                    if (locationBox != null) locationBox.Text = e.Location;
                    if (wateringBox != null) wateringBox.Text = e.WateringFrequency;
                    if (repottedBox != null) repottedBox.Text = e.LastRepotted;
                    break;

                case "Property":
                    if (addressBox != null) addressBox.Text = e.Address;
                    if (areaBox != null) areaBox.Text = e.Area;
                    if (roomsBox != null) roomsBox.Text = e.Rooms;
                    if (propPriceBox != null) propPriceBox.Text = e.PurchasePrice;
                    if (rentBox != null) rentBox.Text = e.RentPrice;
                    SetComboBox(propTypeBox, e.PropertyType);
                    break;

                case "Medicine":
                    if (manufacturerBox != null) manufacturerBox.Text = e.Manufacturer;
                    if (dosageBox != null) dosageBox.Text = e.Dosage;
                    if (expiryBox != null) expiryBox.Text = e.ExpiryDate;
                    if (medPriceBox != null) medPriceBox.Text = e.PurchasePrice;
                    SetComboBox(medTypeBox, e.MedicineType);
                    SetComboBox(medConditionBox, e.Condition);
                    break;

                case "Artwork":
                    if (artistBox != null) artistBox.Text = e.Artist;
                    if (artSizeBox != null) artSizeBox.Text = e.Size;
                    if (artPriceBox != null) artPriceBox.Text = e.PurchasePrice;
                    SetComboBox(artTypeBox, e.ArtworkType);
                    SetComboBox(artMaterialBox, e.ArtMaterial);
                    SetComboBox(artConditionBox, e.Condition);
                    break;

                case "Custom":
                    if (e.CustomFields != null)
                        foreach (var cf in e.CustomFields)
                            customFields.Add(new CustomField { FieldName = cf.FieldName, Value = cf.Value });
                    RenderCustomFields();
                    break;
            }
        }

        // ─── SAVE ─────────────────────────────────────────────────────

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(nameBox?.Text))
            {
                MessageBox.Show("Name is required.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultEntity = new Entity();
            ResultEntity.EntityType = entityType;
            ResultEntity.Name = nameBox.Text;
            ResultEntity.Notes = notesBox?.Text;
            ResultEntity.ImagePath = imagePathText.Text == "No image selected" ? null : imagePathText.Text;

            switch (entityType)
            {
                case "Clothing":
                    ResultEntity.Brand = brandBox?.Text;
                    ResultEntity.Color = colorBox?.Text;
                    ResultEntity.PurchasePrice = priceBox?.Text;
                    ResultEntity.Material = GetComboValue(materialBox);
                    ResultEntity.Season = GetComboValue(seasonBox);
                    ResultEntity.Sizes = sizes;
                    break;

                case "Car":
                case "Motorcycle":
                    ResultEntity.Brand = brandBox?.Text;
                    ResultEntity.Model = modelBox?.Text;
                    ResultEntity.Year = yearBox?.Text;
                    ResultEntity.Color = colorBox?.Text;
                    ResultEntity.LicensePlate = plateBox?.Text;
                    ResultEntity.Mileage = mileageBox?.Text;
                    ResultEntity.FuelType = GetComboValue(fuelBox);
                    ResultEntity.InsuranceExpiry = insuranceBox?.Text;
                    ResultEntity.TechExpiry = techBox?.Text;
                    ResultEntity.PurchasePrice = priceBox?.Text;
                    break;

                case "Book":
                    ResultEntity.Author = authorBox?.Text;
                    ResultEntity.Publisher = publisherBox?.Text;
                    ResultEntity.Year = yearBox?.Text;
                    ResultEntity.ISBN = isbnBox?.Text;
                    ResultEntity.Language = langBox?.Text;
                    ResultEntity.PurchasePrice = priceBox?.Text;
                    ResultEntity.Genre = GetComboValue(genreBox);
                    ResultEntity.Condition = GetComboValue(conditionBox);
                    break;

                case "Game":
                    ResultEntity.Publisher = gamePublisherBox?.Text;
                    ResultEntity.Year = gameYearBox?.Text;
                    ResultEntity.PurchasePrice = priceBox?.Text;
                    ResultEntity.Platform = GetComboValue(platformBox);
                    ResultEntity.Genre = GetComboValue(gameGenreBox);
                    ResultEntity.Condition = GetComboValue(gameConditionBox);
                    break;

                case "Movie":
                    ResultEntity.Director = directorBox?.Text;
                    ResultEntity.Year = movieYearBox?.Text;
                    ResultEntity.PurchasePrice = priceBox?.Text;
                    ResultEntity.Genre = GetComboValue(movieGenreBox);
                    ResultEntity.Format = GetComboValue(formatBox);
                    ResultEntity.Condition = GetComboValue(movieConditionBox);
                    break;

                case "Electronics":
                    ResultEntity.Brand = brandBox?.Text;
                    ResultEntity.Model = elecModelBox?.Text;
                    ResultEntity.SerialNumber = serialBox?.Text;
                    ResultEntity.PurchasePrice = elecPriceBox?.Text;
                    ResultEntity.PurchaseDate = purchaseDateBox?.Text;
                    ResultEntity.WarrantyExpiry = warrantyBox?.Text;
                    ResultEntity.DeviceType = GetComboValue(deviceTypeBox);
                    ResultEntity.Condition = GetComboValue(elecConditionBox);
                    break;

                case "Pet":
                    ResultEntity.Species = speciesBox?.Text;
                    ResultEntity.Breed = breedBox?.Text;
                    ResultEntity.Color = colorBox?.Text;
                    ResultEntity.BirthDate = birthDateBox?.Text;
                    ResultEntity.ChipNumber = chipBox?.Text;
                    ResultEntity.VaccineExpiry = vaccineBox?.Text;
                    ResultEntity.Vet = vetBox?.Text;
                    ResultEntity.Gender = GetComboValue(genderBox);
                    break;

                case "Plant":
                    ResultEntity.Species = speciesBox?.Text;
                    ResultEntity.Location = locationBox?.Text;
                    ResultEntity.WateringFrequency = wateringBox?.Text;
                    ResultEntity.LastRepotted = repottedBox?.Text;
                    break;

                case "Property":
                    ResultEntity.Address = addressBox?.Text;
                    ResultEntity.Area = areaBox?.Text;
                    ResultEntity.Rooms = roomsBox?.Text;
                    ResultEntity.PurchasePrice = propPriceBox?.Text;
                    ResultEntity.RentPrice = rentBox?.Text;
                    ResultEntity.PropertyType = GetComboValue(propTypeBox);
                    break;

                case "Medicine":
                    ResultEntity.Manufacturer = manufacturerBox?.Text;
                    ResultEntity.Dosage = dosageBox?.Text;
                    ResultEntity.ExpiryDate = expiryBox?.Text;
                    ResultEntity.PurchasePrice = medPriceBox?.Text;
                    ResultEntity.MedicineType = GetComboValue(medTypeBox);
                    ResultEntity.Condition = GetComboValue(medConditionBox);
                    break;

                case "Artwork":
                    ResultEntity.Artist = artistBox?.Text;
                    ResultEntity.Size = artSizeBox?.Text;
                    ResultEntity.PurchasePrice = artPriceBox?.Text;
                    ResultEntity.ArtworkType = GetComboValue(artTypeBox);
                    ResultEntity.ArtMaterial = GetComboValue(artMaterialBox);
                    ResultEntity.Condition = GetComboValue(artConditionBox);
                    break;

                case "Custom":
                    ResultEntity.CustomFields = customFields;
                    break;
            }

            this.DialogResult = true;
            this.Close();
        }
    }
}