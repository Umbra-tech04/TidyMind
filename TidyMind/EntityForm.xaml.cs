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
            BuildClothing();
            AddTextBoxMultiline("Notes", ref notesBox);
        }

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

        private void FillForm(Entity e)
        {
            if (nameBox != null) nameBox.Text = e.Name;
            if (notesBox != null) notesBox.Text = e.Notes;

            if (!string.IsNullOrEmpty(e.ImagePath))
            {
                imagePathText.Text = e.ImagePath;
                imagePreview.Source = new BitmapImage(new System.Uri(e.ImagePath));
            }

            if (brandBox != null) brandBox.Text = e.Brand;
            if (colorBox != null) colorBox.Text = e.Color;
            if (priceBox != null) priceBox.Text = e.PurchasePrice;
            SetComboBox(materialBox, e.Material);
            SetComboBox(seasonBox, e.Season);

            if (e.Sizes != null)
                foreach (var sq in e.Sizes)
                    sizes.Add(new SizeQuantity { Size = sq.Size, Condition = sq.Condition, Quantity = sq.Quantity });
            RenderSizes();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(nameBox?.Text))
            {
                MessageBox.Show("Name is required.", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultEntity = new Entity();
            ResultEntity.EntityType = "Clothing";
            ResultEntity.Name = nameBox.Text;
            ResultEntity.Notes = notesBox?.Text;
            ResultEntity.ImagePath = imagePathText.Text == "No image selected" ? null : imagePathText.Text;
            ResultEntity.Brand = brandBox?.Text;
            ResultEntity.Color = colorBox?.Text;
            ResultEntity.PurchasePrice = priceBox?.Text;
            ResultEntity.Material = GetComboValue(materialBox);
            ResultEntity.Season = GetComboValue(seasonBox);
            ResultEntity.Sizes = sizes;

            this.DialogResult = true;
            this.Close();
        }
    }
}
