using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    public partial class InputDialog : Window
    {
        private readonly bool allowEmpty;

        private InputDialog(string heading, string label, string initialValue, string confirmText,
            bool multiline, bool allowEmpty)
        {
            InitializeComponent();
            this.allowEmpty = allowEmpty;

            Title = heading;
            HeadingText.Text = heading;
            LabelText.Text = label;
            ConfirmButton.Content = confirmText;
            ValueInput.Text = initialValue ?? "";

            if (multiline)
            {
                ValueInput.AcceptsReturn = true;
                ValueInput.TextWrapping = TextWrapping.Wrap;
                ValueInput.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                ValueInput.VerticalContentAlignment = VerticalAlignment.Stretch;
                ValueInput.Height = 120;
                ConfirmButton.IsDefault = false; // Enter must insert a newline, not submit
            }

            UpdateConfirmState();
            Loaded += (s, e) =>
            {
                ValueInput.Focus();
                ValueInput.SelectAll();
            };
        }

        // Returns the entered text, or null if the user cancelled.
        public static string Prompt(DependencyObject owner, string heading, string label,
            string initialValue = "", string confirmText = "Create",
            bool multiline = false, bool allowEmpty = false)
        {
            InputDialog dialog = new InputDialog(heading, label, initialValue, confirmText, multiline, allowEmpty);

            Window ownerWindow = owner as Window ?? Window.GetWindow(owner);
            if (ownerWindow != null)
                dialog.Owner = ownerWindow;
            else
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

            return dialog.ShowDialog() == true ? dialog.ValueInput.Text.Trim() : null;
        }

        private void ValueInput_TextChanged(object sender, TextChangedEventArgs e) => UpdateConfirmState();

        private void UpdateConfirmState()
        {
            bool enabled = allowEmpty || !string.IsNullOrWhiteSpace(ValueInput.Text);
            ConfirmButton.IsEnabled = enabled;
            ConfirmButton.Opacity = enabled ? 1.0 : 0.45;
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }
    }
}
