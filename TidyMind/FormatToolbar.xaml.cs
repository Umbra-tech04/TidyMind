using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace TidyMind
{
    // The B / I / U / text-color strip above a rich-text editor. One per editor: Attach connects it, after which
    // the buttons format that editor and mirror the formatting at its caret, and the editor's right-click menu
    // offers the same formatting (so a long text needn't be scrolled back up to the strip).
    public partial class FormatToolbar : UserControl
    {
        private static readonly (string Name, string Hex)[] TextColors =
        {
            ("Default", "#1C1B19"), ("Blue", "#2E7BC4"), ("Green", "#27AE60"), ("Orange", "#F2994A"),
            ("Red", "#EB5757"), ("Purple", "#9B51E0"), ("Gray", "#8A8A87")
        };

        private RichTextBox editor;

        public FormatToolbar()
        {
            InitializeComponent();

            foreach (var color in TextColors)
                ColorSwatches.Children.Add(CreateColorSwatch(color.Name, color.Hex));
        }

        public void Attach(RichTextBox target)
        {
            editor = target;
            editor.SelectionChanged += (s, e) => UpdateFormatState();

            // Ctrl+B / Ctrl+I / Ctrl+U are built into RichTextBox; they don't move the selection, so refresh the
            // toolbar after any editing command (handledEventsToo: the editor marks its own commands handled).
            editor.AddHandler(CommandManager.ExecutedEvent,
                new ExecutedRoutedEventHandler((s, e) => UpdateFormatState()), true);

            editor.ContextMenu = BuildContextMenu();

            UpdateFormatState();
        }

        private static Brush BrushOf(string hex) => new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));

        private FrameworkElement CreateColorSwatch(string name, string hex)
        {
            Border swatch = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(11),
                Margin = new Thickness(3),
                Cursor = Cursors.Hand,
                Background = BrushOf(hex),
                ToolTip = name
            };
            CardEffects.AttachClick(swatch, () =>
            {
                ColorPopup.IsOpen = false;
                ApplyColor(swatch.Background);
            });
            return swatch;
        }

        private void ApplyColor(Brush brush)
        {
            editor.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
            editor.Focus();
            UpdateFormatState();
        }

        // Replaces the editor's built-in menu, so Cut / Copy / Paste are added back below the formatting.
        private ContextMenu BuildContextMenu()
        {
            // Each label previews its own formatting (on the label only, not the shortcut beside it).
            MenuItem bold = FormatMenuItem(new TextBlock { Text = "Bold", FontWeight = FontWeights.Bold }, "Ctrl+B", EditingCommands.ToggleBold);
            MenuItem italic = FormatMenuItem(new TextBlock { Text = "Italic", FontStyle = FontStyles.Italic }, "Ctrl+I", EditingCommands.ToggleItalic);
            MenuItem underline = FormatMenuItem(new TextBlock { Text = "Underline", TextDecorations = TextDecorations.Underline }, "Ctrl+U", EditingCommands.ToggleUnderline);

            MenuItem color = new MenuItem { Header = "Text color" };
            foreach (var (name, hex) in TextColors)
            {
                Brush brush = BrushOf(hex);
                MenuItem item = new MenuItem
                {
                    Header = name,
                    Icon = new Ellipse { Width = 12, Height = 12, Fill = brush, Stroke = BrushOf("#33000000"), StrokeThickness = 1 }
                };
                item.Click += (s, e) => ApplyColor(brush);
                color.Items.Add(item);
            }

            ContextMenu menu = new ContextMenu();
            menu.Items.Add(bold);
            menu.Items.Add(italic);
            menu.Items.Add(underline);
            menu.Items.Add(color);
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Command = ApplicationCommands.Cut, CommandTarget = editor });
            menu.Items.Add(new MenuItem { Command = ApplicationCommands.Copy, CommandTarget = editor });
            menu.Items.Add(new MenuItem { Command = ApplicationCommands.Paste, CommandTarget = editor });

            // Ticks show what's applied to the selection, like the toolbar's filled buttons.
            menu.Opened += (s, e) =>
            {
                bold.IsChecked = BoldButton.IsChecked == true;
                italic.IsChecked = ItalicButton.IsChecked == true;
                underline.IsChecked = UnderlineButton.IsChecked == true;
            };
            return menu;
        }

        private MenuItem FormatMenuItem(TextBlock header, string shortcut, RoutedUICommand command)
        {
            MenuItem item = new MenuItem { Header = header, InputGestureText = shortcut };
            item.Click += (s, e) => Format(command);
            return item;
        }

        private void BoldButton_Click(object sender, RoutedEventArgs e) => Format(EditingCommands.ToggleBold);
        private void ItalicButton_Click(object sender, RoutedEventArgs e) => Format(EditingCommands.ToggleItalic);
        private void UnderlineButton_Click(object sender, RoutedEventArgs e) => Format(EditingCommands.ToggleUnderline);

        // Same commands as Ctrl+B / Ctrl+I / Ctrl+U, so the buttons and the shortcuts behave identically.
        private void Format(RoutedUICommand command)
        {
            command.Execute(null, editor);
            editor.Focus();
            UpdateFormatState();
        }

        private void ColorButton_Click(object sender, RoutedEventArgs e)
        {
            ColorPopup.IsOpen = !ColorPopup.IsOpen;
            ColorButton.IsChecked = ColorPopup.IsOpen;
        }

        private void ColorPopup_Closed(object sender, EventArgs e) => ColorButton.IsChecked = false;

        // Mirrors the formatting at the caret / across the selection (mixed counts as off).
        private void UpdateFormatState()
        {
            TextSelection selection = editor.Selection;

            BoldButton.IsChecked = Equals(selection.GetPropertyValue(TextElement.FontWeightProperty), FontWeights.Bold);
            ItalicButton.IsChecked = Equals(selection.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
            UnderlineButton.IsChecked = selection.GetPropertyValue(Inline.TextDecorationsProperty) is TextDecorationCollection decorations
                && decorations.Any(d => d.Location == TextDecorationLocation.Underline);

            ColorBar.Fill = selection.GetPropertyValue(TextElement.ForegroundProperty) as Brush ?? (Brush)FindResource("TextSub");
        }
    }
}
