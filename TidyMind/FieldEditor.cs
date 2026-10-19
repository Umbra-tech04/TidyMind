using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyMind
{
    // The input for one field (FieldDefinition) of the item form and the vehicle window's cards: a text box (with its
    // unit, and a second box for an alternate unit), a choice, a date, a check box, check boxes with the user's own
    // items, a multi-line box, or a record's file. It starts from the stored value and gives back the value to store.
    public sealed class FieldEditor
    {
        public FieldDefinition Field { get; }
        public FrameworkElement Element { get; }

        // A flag's check box carries the field's name itself; the others need a label above them.
        public bool HasOwnLabel => Field.Kind == ValueKind.Flag;

        // Long ones take a whole row of a card.
        public bool IsWide => Field.Kind == ValueKind.LongText || Field.Kind == ValueKind.CheckList || Field.Kind == ValueKind.File;

        private readonly string initial; // the stored value it started from (a check list's in its shown order)
        private readonly TextBox? box, alternateBox;
        private readonly ComboBox? combo;
        private readonly DatePicker? picker;
        private readonly CheckBox? flag;
        private readonly WrapPanel? checks;

        // File: the record's file as it was, a file picked to replace it, or the file removed.
        public Attachment? File { get; }
        public string? PickedFile { get; private set; }
        public bool FileRemoved { get; private set; }
        private TextBlock? fileName;
        private Button? removeFile;

        public FieldEditor(FieldDefinition field, Entity owner, string? stored, Attachment? file = null)
        {
            Field = field;
            File = file;
            initial = stored ?? "";
            switch (field.Kind)
            {
                case ValueKind.Choice:
                    combo = new ComboBox { Height = 32 };
                    combo.Items.Add(new ComboBoxItem { Content = NotSetLabel(field), Tag = "" });
                    foreach (string choice in field.Choices)
                        combo.Items.Add(new ComboBoxItem { Content = field.ChoiceLabel(choice), Tag = choice });
                    // A value that isn't among the choices (another version, or typed into the file) is kept as one.
                    if (initial.Length > 0 && !field.Choices.Contains(initial))
                        combo.Items.Add(new ComboBoxItem { Content = initial, Tag = initial });
                    combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == initial);
                    Element = combo;
                    break;
                case ValueKind.Date:
                    picker = new DatePicker { Height = 32, VerticalContentAlignment = VerticalAlignment.Center, SelectedDate = Values.ParseDate(initial) };
                    Element = picker;
                    break;
                case ValueKind.Flag:
                    flag = new CheckBox { Content = field.Label, IsChecked = initial.Length > 0, VerticalAlignment = VerticalAlignment.Center };
                    Element = flag;
                    break;
                case ValueKind.CheckList:
                    checks = new WrapPanel();
                    Element = CheckList(field, initial);
                    break;
                case ValueKind.LongText:
                    box = new TextBox { Height = 80, Text = initial, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                        VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                    Element = box;
                    break;
                case ValueKind.File:
                    Element = FilePicker();
                    break;
                case ValueKind.Number:
                    box = new TextBox { Height = 32, VerticalContentAlignment = VerticalAlignment.Center, Text = Typed(initial) };
                    alternateBox = field.Alternate == null ? null : new TextBox { Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
                    Element = field.Unit == null ? box : NumberWithUnit(field, owner);
                    break;
                default:
                    box = new TextBox { Height = 32, VerticalContentAlignment = VerticalAlignment.Center, Text = initial };
                    Element = box;
                    break;
            }
            AutomationProperties.SetName(Element, field.Label);
            if (checks != null)
                initial = Value!;
        }

        // The value to store: "" for nothing, null when a number can't be read as one.
        public string? Value
        {
            get
            {
                if (combo != null) return (string?)((ComboBoxItem?)combo.SelectedItem)?.Tag ?? "";
                if (picker != null) return Values.FromDate(picker.SelectedDate) ?? "";
                if (flag != null) return flag.IsChecked == true ? "true" : "";
                if (checks != null) return string.Join("\n", checks.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (string)c.Tag));
                if (box == null) return "";
                return Field.Kind == ValueKind.Number ? Values.Normalize(ValueKind.Number, box.Text) : box.Text.Trim();
            }
        }

        public bool Changed => Value != initial || PickedFile != null || FileRemoved;

        public void Focus() => (box ?? combo ?? picker ?? (Control?)flag ?? Element as Control)?.Focus();

        // ---- Kinds ----------------------------------------------------------------

        // What "nothing chosen" is called: the field's own word for it (a document's reminder: "No reminder"), else "Not set".
        private static string NotSetLabel(FieldDefinition field)
        {
            string own = field.ChoiceLabel("");
            return string.IsNullOrEmpty(own) ? Loc.T("Entity.NotSet") : own;
        }

        // A stored number as typed in the current language ("6,5" in Hungarian), no grouping.
        private static string Typed(string stored) =>
            Values.Number(stored) is double n ? n.ToString("0.###", Loc.Culture) : stored;

        // The box with its unit after it; with an alternate unit a second box, the two filling each other as typed.
        private FrameworkElement NumberWithUnit(FieldDefinition field, Entity owner)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            box!.Width = 110;
            row.Children.Add(box);
            row.Children.Add(UnitText(Units.Label(field.Unit!, owner)));
            if (alternateBox == null || field.Alternate == null)
                return row;

            alternateBox.Width = 90;
            alternateBox.Margin = new Thickness(12, 0, 0, 0);
            AutomationProperties.SetName(alternateBox, field.Label + " (" + Units.Label(field.Alternate.Unit, owner) + ")");
            row.Children.Add(alternateBox);
            row.Children.Add(UnitText(Units.Label(field.Alternate.Unit, owner)));

            bool filling = false;
            void Fill(TextBox from, TextBox to, double factor, int decimals)
            {
                if (filling) return;
                filling = true;
                string? stored = Values.Normalize(ValueKind.Number, from.Text);
                to.Text = Values.Number(stored) is double n ? Math.Round(n * factor, decimals).ToString("0.#", Loc.Culture) : "";
                filling = false;
            }
            Fill(box, alternateBox, field.Alternate.Factor, 0);
            box.TextChanged += (s, e) => Fill(box, alternateBox, field.Alternate.Factor, 0);
            alternateBox.TextChanged += (s, e) => Fill(alternateBox, box, 1 / field.Alternate.Factor, 1);
            return row;
        }

        private static TextBlock UnitText(string unit) => new TextBlock
        {
            Text = unit,
            Foreground = (Brush)Application.Current.FindResource("TextSub"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };

        // A check box per offered choice and per item of the user's own, then a box to add one more.
        private FrameworkElement CheckList(FieldDefinition field, string stored)
        {
            HashSet<string> chosen = FieldDefinition.Items(stored).ToHashSet();
            foreach (string choice in field.Choices.Concat(chosen.Where(c => !field.Choices.Contains(c))))
                AddCheck(field.ChoiceLabel(choice), choice, chosen.Contains(choice));

            TextBox own = new TextBox { Height = 30, Width = 200, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(own, Loc.T("Entity.CheckList.Own"));
            Button add = new Button
            {
                Content = Loc.T("Entity.CheckList.Add"),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            void AddOwn()
            {
                string text = own.Text.Trim();
                if (text.Length == 0)
                    return;
                CheckBox? same = checks!.Children.OfType<CheckBox>().FirstOrDefault(c => string.Equals((string)c.Tag, text, StringComparison.CurrentCultureIgnoreCase));
                if (same != null)
                    same.IsChecked = true;
                else
                    AddCheck(text, text, true);
                own.Clear();
                own.Focus();
            }
            add.Click += (s, e) => AddOwn();
            own.KeyDown += (s, e) =>
            {
                if (e.Key != System.Windows.Input.Key.Enter) return;
                AddOwn();
                e.Handled = true;
            };

            StackPanel ownRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            ownRow.Children.Add(new TextBlock { Text = Loc.T("Entity.CheckList.Own"), VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("TextSub"), Margin = new Thickness(0, 0, 8, 0) });
            ownRow.Children.Add(own);
            ownRow.Children.Add(add);

            StackPanel panel = new StackPanel();
            panel.Children.Add(checks!);
            panel.Children.Add(ownRow);
            return panel;
        }

        private void AddCheck(string label, string value, bool isChecked) =>
            checks!.Children.Add(new CheckBox { Content = label, Tag = value, IsChecked = isChecked, Margin = new Thickness(0, 4, 18, 4) });

        // The file's name (or that there is none), Choose…, and Remove while there is one.
        private FrameworkElement FilePicker()
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            fileName = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 320 };
            Button choose = new Button { Content = Loc.T("Entity.File.Choose"), Style = (Style)Application.Current.FindResource("GhostButtonStyle") };
            removeFile = new Button { Content = Loc.T("Entity.File.Remove"), Style = (Style)Application.Current.FindResource("LinkButtonStyle"),
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            choose.Click += (s, e) =>
            {
                OpenFileDialog dialog = new OpenFileDialog { Filter = Loc.T("Entity.File.Filter") };
                if (dialog.ShowDialog(Window.GetWindow(row)) != true)
                    return;
                PickedFile = dialog.FileName;
                FileRemoved = false;
                ShowFile();
            };
            removeFile.Click += (s, e) =>
            {
                PickedFile = null;
                FileRemoved = File != null;
                ShowFile();
            };
            row.Children.Add(fileName);
            row.Children.Add(choose);
            row.Children.Add(removeFile);
            ShowFile();
            return row;
        }

        private void ShowFile()
        {
            string? shown = PickedFile != null ? System.IO.Path.GetFileName(PickedFile) : FileRemoved ? null : File?.DisplayName;
            fileName!.Text = shown ?? Loc.T("Entity.File.None");
            fileName.Foreground = (Brush)Application.Current.FindResource(shown == null ? "TextSub" : "TextMain");
            removeFile!.Visibility = shown == null ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
