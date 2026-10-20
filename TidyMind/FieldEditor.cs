using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
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
        public bool IsWide => Field.Kind == ValueKind.LongText || Field.Kind == ValueKind.CheckList || Field.Kind == ValueKind.File
            || Field.Kind == ValueKind.Specs || Field.Kind == ValueKind.Pictures || Field.Kind == ValueKind.Links;

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

        // Specs: a row per pair. Pictures: the record's as the editor leaves them (stored names or picked files), cover
        // first; the window copies the picked ones in (EntityImages.Import).
        private StackPanel? specRows;
        public List<PictureSlot> PictureSlots { get; } = new List<PictureSlot>();
        private PictureBrowser? pictureBrowser;
        private Button? removePicture;
        private int selectedPicture;
        private readonly string listsInitial = "";

        // record: the one being edited, for the kinds kept on it (File, Specs, Pictures).
        public FieldEditor(FieldDefinition field, Entity owner, string? stored, EntityRecord? record = null)
        {
            Field = field;
            File = record?.File;
            initial = stored ?? "";
            switch (field.Kind)
            {
                case ValueKind.Choice:
                    combo = new ComboBox { Height = InputHeight };
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
                    picker = new DatePicker { Height = InputHeight, SelectedDate = Values.ParseDate(initial) };
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
                case ValueKind.Links:
                    checks = new WrapPanel();
                    Element = Links(field, owner, initial);
                    break;
                case ValueKind.LongText:
                    box = Box(initial, 96);
                    box.TextWrapping = TextWrapping.Wrap;
                    box.AcceptsReturn = true;
                    box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
                    Element = box;
                    break;
                case ValueKind.File:
                    Element = FilePicker();
                    break;
                case ValueKind.Specs:
                    Element = SpecsEditor(record?.Specs?.Where(s => s != null) ?? Enumerable.Empty<SpecLine>());
                    break;
                case ValueKind.Pictures:
                    PictureSlots.AddRange((record?.Pictures ?? new List<string>()).Select(name => new PictureSlot { Stored = name }));
                    Element = PicturesEditor();
                    break;
                case ValueKind.Number:
                    box = Box(Typed(initial));
                    alternateBox = field.Alternate == null ? null : Box();
                    Element = field.Unit == null ? box : NumberWithUnit(field, owner);
                    break;
                default:
                    box = Box(initial);
                    Element = box;
                    break;
            }
            AutomationProperties.SetName(Element, field.Label);
            if (checks != null)
                initial = Value!;
            listsInitial = Lists();
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

        public bool Changed => Value != initial || PickedFile != null || FileRemoved || Lists() != listsInitial;

        // The pairs typed, without empty rows.
        public List<SpecLine> Specs => specRows == null ? new List<SpecLine>()
            : specRows.Children.OfType<Grid>().Select(row => row.Children.OfType<TextBox>().Select(b => b.Text.Trim()).ToArray())
                .Where(t => t[0].Length > 0 || t[1].Length > 0).Select(t => new SpecLine { Name = t[0], Value = t[1] }).ToList();

        private string Lists() => JsonSerializer.Serialize(Specs.Select(s => new[] { s.Name, s.Value }))
            + string.Join("|", PictureSlots.Select(p => p.Stored ?? p.PickedFile));

        public void Focus() => (box ?? combo ?? picker ?? (Control?)flag ?? Element as Control)?.Focus();

        // ---- Kinds ----------------------------------------------------------------

        // Every input is this tall (a long text taller): the dialogs' form box (NoteInputStyle) for text, the app's own
        // combo box and date picker beside it.
        public const double InputHeight = 36;

        // The app's input box, at the size and text inset of the form's dropdowns and date pickers beside it.
        private static TextBox Box(string text = "", double height = InputHeight) =>
            new TextBox { Style = (Style)Application.Current.FindResource("NoteInputStyle"), Height = height, Text = text, FontSize = 13, Padding = new Thickness(0) };

        // What "nothing chosen" is called: the field's own word for it (a document's reminder: "No reminder"), else "Not set".
        private static string NotSetLabel(FieldDefinition field)
        {
            string own = field.ChoiceLabel("");
            return string.IsNullOrEmpty(own) ? Loc.T("Entity.NotSet") : own;
        }

        // A stored number as typed in the current language ("6,5" in Hungarian), no grouping.
        private static string Typed(string stored) =>
            Values.Number(stored) is double n ? n.ToString("0.###", Loc.Culture) : stored;

        // The box with its unit after it; with an alternate unit a second box, the two filling each other as typed. The
        // boxes share what room the cell has (never more than a number needs, never less than a few digits), so the pair
        // fits the narrowest column instead of running out of it.
        private FrameworkElement NumberWithUnit(FieldDefinition field, Entity owner)
        {
            bool pair = alternateBox != null && field.Alternate != null;
            Grid row = new Grid { MaxWidth = pair ? 320 : 170, HorizontalAlignment = HorizontalAlignment.Left };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (pair)
            {
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            }
            row.Children.Add(BoxWithUnit(box!, Units.Label(field.Unit!, owner)));
            if (!pair)
                return row;

            AutomationProperties.SetName(alternateBox!, field.Label + " (" + Units.Label(field.Alternate!.Unit, owner) + ")");
            FrameworkElement second = BoxWithUnit(alternateBox!, Units.Label(field.Alternate.Unit, owner));
            Grid.SetColumn(second, 2);
            row.Children.Add(second);

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

        // The unit docked after the box, which takes the rest.
        private static FrameworkElement BoxWithUnit(TextBox number, string unit)
        {
            number.MinWidth = 56;
            DockPanel group = new DockPanel();
            TextBlock text = new TextBlock
            {
                Text = unit,
                Foreground = (Brush)Application.Current.FindResource("TextSub"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            DockPanel.SetDock(text, Dock.Right);
            group.Children.Add(text);
            group.Children.Add(number);
            return group;
        }

        // A check box per offered choice and per item of the user's own, then a box to add one more.
        private FrameworkElement CheckList(FieldDefinition field, string stored)
        {
            HashSet<string> chosen = FieldDefinition.Items(stored).ToHashSet();
            foreach (string choice in field.Choices.Concat(chosen.Where(c => !field.Choices.Contains(c))))
                AddCheck(field.ChoiceLabel(choice), choice, chosen.Contains(choice));

            TextBox own = Box();
            own.Width = 200;
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

            // Wraps when the label, the box and the button don't fit one line.
            WrapPanel ownRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            ownRow.Children.Add(new TextBlock { Text = Loc.T("Entity.CheckList.Own"), VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.FindResource("TextSub"), Margin = new Thickness(0, 0, 8, 0) });
            ownRow.Children.Add(own);
            ownRow.Children.Add(add);

            StackPanel panel = new StackPanel();
            panel.Children.Add(checks!);
            panel.Children.Add(ownRow);
            return panel;
        }

        // A check box per record it can point at (a part, named with the ones above it); one deleted since is left out.
        // None yet: where they come from.
        private FrameworkElement Links(FieldDefinition field, Entity owner, string stored)
        {
            HashSet<string> chosen = FieldDefinition.Items(stored).ToHashSet();
            List<(string Value, string Label)> options = field.LinkOptions(owner);
            foreach ((string value, string label) in options)
                AddCheck(label, value, chosen.Contains(value));
            if (options.Count > 0)
                return checks!;
            return new TextBlock { Text = Loc.T("Entity.Records." + field.LinkTo + ".NoneToLink"), TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.FindResource("TextSub") };
        }

        private void AddCheck(string label, string value, bool isChecked) =>
            checks!.Children.Add(new CheckBox { Content = label, Tag = value, IsChecked = isChecked, Margin = new Thickness(0, 4, 18, 4) });

        // The file's name (or that there is none), Choose…, and Remove while there is one.
        private FrameworkElement FilePicker()
        {
            // The name takes what the buttons leave and is trimmed to it.
            Grid row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            fileName = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            Button choose = new Button { Content = Loc.T("Entity.File.Choose"), Style = (Style)Application.Current.FindResource("GhostButtonStyle") };
            removeFile = new Button { Content = Loc.T("Entity.File.Remove"), Style = (Style)Application.Current.FindResource("LinkButtonStyle"),
                Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(choose, 1);
            Grid.SetColumn(removeFile, 2);
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

        // ---- Specs: name and value pairs --------------------------------------------------------

        // Rows of a name box and a value box, Remove on each, and Add a spec under them.
        private FrameworkElement SpecsEditor(IEnumerable<SpecLine> specs)
        {
            specRows = new StackPanel();
            foreach (SpecLine spec in specs)
                AddSpecRow(spec.Name, spec.Value);
            Button add = new Button
            {
                Content = Loc.T("Entity.Specs.Add"),
                Style = (Style)Application.Current.FindResource("LinkButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
            };
            add.Click += (s, e) => AddSpecRow("", "").Children.OfType<TextBox>().First().Focus();
            StackPanel panel = new StackPanel();
            panel.Children.Add(specRows);
            panel.Children.Add(add);
            return panel;
        }

        private Grid AddSpecRow(string name, string value)
        {
            Grid row = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            TextBox nameBox = Box(name);
            nameBox.Margin = new Thickness(0, 0, 8, 0);
            TextBox valueBox = Box(value);
            AutomationProperties.SetName(nameBox, Loc.T("Entity.Specs.Name"));
            AutomationProperties.SetName(valueBox, Loc.T("Entity.Specs.Value"));
            Button remove = new Button
            {
                Content = "✕",
                Style = (Style)Application.Current.FindResource("IconButtonStyle"),
                Width = 28,
                Height = 28,
                Margin = new Thickness(6, 0, 0, 0),
                ToolTip = Loc.T("Entity.Specs.Remove")
            };
            AutomationProperties.SetName(remove, Loc.T("Entity.Specs.Remove"));
            remove.Click += (s, e) => specRows!.Children.Remove(row);
            Grid.SetColumn(valueBox, 1);
            Grid.SetColumn(remove, 2);
            row.Children.Add(nameBox);
            row.Children.Add(valueBox);
            row.Children.Add(remove);
            specRows!.Children.Add(row);
            return row;
        }

        // ---- Pictures --------------------------------------------------------------------------

        // The pictures as a browser with a strip (drag to reorder, the first is the cover), Add pictures… and Remove for
        // the selected one. Picked files are only previewed here; the window copies them in on Save.
        private FrameworkElement PicturesEditor()
        {
            pictureBrowser = new PictureBrowser(150, double.PositiveInfinity, canReorder: true, stripForOne: true);
            pictureBrowser.Picked += index => { selectedPicture = index; ShowPictures(); };
            pictureBrowser.Moved += (from, to) =>
            {
                PictureSlot slot = PictureSlots[from];
                PictureSlots.RemoveAt(from);
                PictureSlots.Insert(to, slot);
                selectedPicture = to;
                ShowPictures();
            };
            Button add = new Button { Content = Loc.T("Entity.ChoosePictures"), Style = (Style)Application.Current.FindResource("GhostButtonStyle") };
            removePicture = new Button
            {
                Content = Loc.T("Entity.Picture.Remove"),
                Style = (Style)Application.Current.FindResource("LinkButtonStyle"),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            add.Click += (s, e) =>
            {
                OpenFileDialog dialog = new OpenFileDialog { Filter = Loc.T("Entity.ImageFilter"), Multiselect = true };
                if (dialog.ShowDialog(Window.GetWindow(add)) == true)
                    AddPictures(dialog.FileNames);
            };
            removePicture.Click += (s, e) =>
            {
                if (selectedPicture < 0) return;
                PictureSlots.RemoveAt(selectedPicture);
                ShowPictures();
            };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            buttons.Children.Add(add);
            buttons.Children.Add(removePicture);
            StackPanel panel = new StackPanel();
            panel.Children.Add(buttons);
            panel.Children.Add(pictureBrowser);
            ShowPictures();
            return panel;
        }

        // Picked files go in after the others (PictureSlot.AddPicked says what is left out); the first new one is selected.
        internal void AddPictures(IEnumerable<string> files)
        {
            int firstNew = PictureSlot.AddPicked(PictureSlots, files, Window.GetWindow(Element));
            if (firstNew >= 0)
                selectedPicture = firstNew;
            ShowPictures();
        }

        private void ShowPictures()
        {
            selectedPicture = PictureSlots.Count == 0 ? -1 : Math.Clamp(selectedPicture, 0, PictureSlots.Count - 1);
            foreach (PictureSlot slot in PictureSlots)
                slot.Preview ??= slot.Stored != null ? EntityImages.Load(slot.Stored) : EntityImages.Store.Decode(slot.PickedFile);
            pictureBrowser!.Show(PictureSlots.Select(p => p.Preview).ToList(), selectedPicture);
            pictureBrowser.Visibility = PictureSlots.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            removePicture!.Visibility = PictureSlots.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
