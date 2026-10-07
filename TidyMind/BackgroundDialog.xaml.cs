using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace TidyMind
{
    // The background picker behind Customize Card and Edit Background: a color from the named palette, a picture,
    // or nothing. It only collects the choice and shows it on a live preview; the caller applies it
    // (BackgroundChoice.ApplyTo copies a picked image in and cleans up old copies). Opened through
    // CardCustomizeDialog / MemoryBackgroundDialog, which say what the preview is and how a swatch looks.
    public partial class BackgroundDialog : Window
    {
        // Mid-to-dark, saturated but not neon: white card text stays legible on them even above the bottom scrim.
        // Azure is the app's own accent. The same names and hex values for cards and pages, so "Teal" is the same
        // color in both places (a page shows it washed out). Shown 5 x 2; the name is each swatch's tooltip.
        public static readonly (string Name, string Hex)[] Palette =
        {
            ("Rust", "#C1562F"),
            ("Ochre", "#C99A2E"),
            ("Forest", "#2F5D43"),
            ("Teal", "#1F6B6B"),
            ("Ink", "#2C4A6E"),
            ("Azure", "#2E7BC4"),
            ("Plum", "#6B3B5E"),
            ("Brick", "#9C3D3D"),
            ("Slate", "#51637A"),
            ("Charcoal", "#332F2B")
        };

        // Builds the preview for the current choice: type, color, and the newly picked image (null: the stored one).
        public delegate FrameworkElement PreviewBuilder(BackgroundFill type, string color, ImageSource pickedImage);

        private readonly ImageStore store;
        private readonly PreviewBuilder preview;
        private BackgroundFill type;
        private string color;
        private string pickedFile;          // a new pick, not copied in yet
        private ImageSource pickedImage;
        private readonly List<KeyValuePair<Border, string>> swatches = new List<KeyValuePair<Border, string>>();

        // swatchColor: how a palette color is shown on its swatch (as it will look where it's applied).
        public BackgroundDialog(string heading, string subheading, BackgroundSetting current, ImageStore store,
            Func<Color, Color> swatchColor, PreviewBuilder preview)
        {
            InitializeComponent();
            Title = heading;
            HeadingText.Text = heading;
            SubheadingText.Text = subheading;
            this.store = store;
            this.preview = preview;

            type = current.Type;
            color = current.Color;

            foreach ((string name, string hex) in Palette)
                AddSwatch(name, hex, swatchColor);
            Refresh();
        }

        // The choice, or null if the dialog was cancelled.
        public BackgroundChoice Ask(DependencyObject owner)
        {
            Window ownerWindow = owner as Window ?? Window.GetWindow(owner);
            if (ownerWindow != null)
                Owner = ownerWindow;
            else
                WindowStartupLocation = WindowStartupLocation.CenterScreen;

            if (ShowDialog() != true)
                return null;
            return new BackgroundChoice
            {
                Type = type,
                Color = type == BackgroundFill.Color ? color : null,
                ImageFile = type == BackgroundFill.Image ? pickedFile : null
            };
        }

        // ---- Choosing -----------------------------------------------------

        // A color replaces any image; null (Remove customization) is the default look.
        private void Pick(string hex)
        {
            type = hex == null ? BackgroundFill.None : BackgroundFill.Color;
            color = hex;
            Refresh();
        }

        private void ChooseImage_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog { Title = "Choose an image", Filter = ImageStore.FileFilter };
            if (dialog.ShowDialog(this) != true)
                return;

            // Checked now, so a file that isn't a readable image is caught before anything is saved.
            ImageSource image = store.Decode(dialog.FileName);
            if (image == null)
            {
                MessageBox.Show(this, "That file can't be opened as an image. Choose a JPG, PNG or WebP picture.",
                    "Choose Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            pickedFile = dialog.FileName;
            pickedImage = image;
            type = BackgroundFill.Image;
            color = null;
            Refresh();
        }

        private void Remove_Click(object sender, RoutedEventArgs e) => Pick(null);

        private void Save_Click(object sender, RoutedEventArgs e) => DialogResult = true;

        // ---- Showing ------------------------------------------------------

        private void Refresh()
        {
            foreach (KeyValuePair<Border, string> swatch in swatches)
            {
                bool selected = type == BackgroundFill.Color && string.Equals(swatch.Value, color, StringComparison.OrdinalIgnoreCase);
                swatch.Key.BorderBrush = selected ? (Brush)FindResource("TextMain") : Brushes.Transparent;
            }

            bool hasImage = type == BackgroundFill.Image;
            ImageText.Text = !hasImage ? "No image"
                : pickedFile != null ? System.IO.Path.GetFileName(pickedFile)
                : "Current image";

            RemoveButton.IsEnabled = type != BackgroundFill.None;
            RemoveButton.Opacity = RemoveButton.IsEnabled ? 1 : 0.45;

            Preview.Content = preview(type, color, hasImage ? pickedImage : null);
        }

        // Same swatch look as the calendar's day colors: a dot in a ring that darkens when selected.
        private void AddSwatch(string name, string hex, Func<Color, Color> swatchColor)
        {
            Color shown = swatchColor((Color)ColorConverter.ConvertFromString(hex));
            Border fill = new Border
            {
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(shown),
                // A thin edge so very light (washed) swatches still read as dots.
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x26, 0, 0, 0)),
                BorderThickness = new Thickness(shown.R + shown.G + shown.B > 600 ? 1 : 0)
            };

            Border ring = new Border
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(2),
                Margin = new Thickness(0, 0, 4, 4),
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                ToolTip = name,
                Child = fill
            };
            CardEffects.AttachClick(ring, () => Pick(hex));

            swatches.Add(new KeyValuePair<Border, string>(ring, hex));
            SwatchPanel.Children.Add(ring);
        }
    }
}
