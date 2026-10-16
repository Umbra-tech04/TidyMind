using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace TidyMind
{
    public partial class EntityForm : Window
    {
        // When editing: a copy of the item with the form's fields applied, so its Id, tab, place, type and any fields
        // the form doesn't show stay as they were. When adding: a new item of the chosen type.
        public Entity ResultEntity { get; private set; }

        // A picture picked in this form (the user's own file), or null. The caller copies it in (EntityImages).
        public string PickedImageFile { get; private set; }

        private readonly Entity existing;
        private string entityType;

        // Every type
        private TextBox nameBox;
        private TextBox notesBox;
        private TextBlock imagePathText;
        private System.Windows.Controls.Image imagePreview;

        // Clothing
        private List<SizeQuantity> sizes = new List<SizeQuantity>();
        private StackPanel sizesPanel;
        private TextBox brandBox, colorBox, priceBox;
        private ComboBox materialBox, seasonBox;

        // Types with a definition (EntityTypes, e.g. Vehicle): one control per field, one LogEditor per log.
        private readonly Dictionary<FieldDefinition, Control> fieldControls = new Dictionary<FieldDefinition, Control>();
        private readonly Dictionary<LogDefinition, LogEditor> logEditors = new Dictionary<LogDefinition, LogEditor>();

        public EntityForm(string entityType)
        {
            InitializeComponent();
            this.entityType = entityType;
            FormTitle.Text = Loc.T(TitleKey("Add", entityType));
            this.Title = FormTitle.Text;
            BuildForm();
            opened = CurrentInput();
        }

        public EntityForm(Entity existing)
        {
            InitializeComponent();
            this.existing = existing;
            this.entityType = existing.EntityType;
            FormTitle.Text = Loc.T(TitleKey("Edit", entityType));
            this.Title = FormTitle.Text;
            BuildForm();
            FillForm(existing);
            opened = CurrentInput();
        }

        // "Entity.Add.Vehicle", "Entity.Edit.Clothing"…; a type without its own title is an "item".
        private static string TitleKey(string action, string type) =>
            "Entity." + action + "." + (type == EntityTypes.Clothing || type == EntityTypes.Vehicle ? type : "Item");

        // What the form showed when it opened, to tell whether closing with X would throw anything away.
        private readonly string opened;

        private string CurrentInput() => string.Join("\n", nameBox?.Text, notesBox?.Text, brandBox?.Text, colorBox?.Text,
            priceBox?.Text, GetComboValue(materialBox), GetComboValue(seasonBox), PickedImageFile,
            JsonSerializer.Serialize(sizes),
            string.Join("\n", fieldControls.Select(f => FieldValue(f.Key, f.Value))),
            JsonSerializer.Serialize(logEditors.Values.Select(editor => editor.Rows)));

        // Closing without saving (X, Alt+F4) asks first when something was typed or changed; Save doesn't.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            // A log cell still being typed in counts too: leaving it stores it (TextEditing.InlineEditor).
            if (DialogResult != true)
                System.Windows.Input.Keyboard.ClearFocus();

            // A refused log value (red line under its table) counts as a change, so it isn't dropped without asking.
            if (DialogResult != true && (CurrentInput() != opened || logEditors.Values.Any(editor => editor.HasProblem))
                && Msg.Show(this, Loc.T("Common.DiscardChanges"), Loc.T("Common.UnsavedChanges"), MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;

            base.OnClosing(e);
        }

        // Esc closes like X (asking first if anything was typed or changed). An open dropdown takes its own Esc first.
        protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != System.Windows.Input.Key.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        }

        private void BuildForm()
        {
            FormPanel.Children.Clear();
            AddImagePicker();
            AddTextBox(EntityTypes.NameLabel(entityType), ref nameBox);
            if (EntityTypes.Fields(entityType).Count > 0)
                BuildDefinedFields();
            else
                BuildClothing();
            AddTextBoxMultiline(Loc.T("Entity.Field.Notes"), ref notesBox);
            BuildLogs();
        }

        // ---- Types with a definition (EntityTypes) -------------------------

        private void BuildDefinedFields()
        {
            foreach (FieldDefinition field in EntityTypes.Fields(entityType))
            {
                if (field.Kind == ValueKind.Choice)
                {
                    ComboBox combo = null;
                    AddComboBox(field.Label, ref combo, field.Choices);
                    fieldControls[field] = combo;
                }
                else if (field.Kind == ValueKind.Date)
                    fieldControls[field] = AddDatePicker(field);
                else
                {
                    TextBox box = null;
                    AddTextBox(field.Label, ref box);
                    fieldControls[field] = box;
                }
            }
        }

        // An expiry date says how close it is beside the picker ("in 12 days"), highlighted within 30 days.
        private DatePicker AddDatePicker(FieldDefinition field)
        {
            AddLabel(field.Label);
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal };
            DatePicker picker = new DatePicker { Width = 180, Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(picker, field.Label);
            row.Children.Add(picker);

            if (field.IsExpiry)
            {
                TextBlock status = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
                picker.SelectedDateChanged += (s, e) =>
                {
                    DateTime? date = picker.SelectedDate;
                    ExpiryLevel level = date == null ? ExpiryLevel.Later : Expiry.Level(date.Value, DateTime.Today);
                    status.Text = date == null ? "" : Expiry.Describe(date.Value, DateTime.Today);
                    status.Foreground = Expiry.Brush(level);
                    status.FontWeight = Expiry.Weight(level);
                };
                row.Children.Add(status);
            }

            FormPanel.Children.Add(row);
            return picker;
        }

        private void BuildLogs()
        {
            IReadOnlyList<LogDefinition> logs = EntityTypes.Logs(entityType);
            if (logs.Count > 0)
                Width = 600; // room for the log columns

            foreach (LogDefinition log in logs)
            {
                AddLabel(log.Label);
                LogEditor editor = new LogEditor(log);
                logEditors[log] = editor;
                FormPanel.Children.Add(editor);
            }
        }

        // The field's value in its stored form: "" for nothing, null if a number field holds something else.
        private string FieldValue(FieldDefinition field, Control control)
        {
            switch (control)
            {
                case ComboBox combo: return GetComboValue(combo) ?? "";
                case DatePicker picker: return Values.FromDate(picker.SelectedDate) ?? "";
                case TextBox box: return Values.Normalize(field.Kind, box.Text);
                default: return "";
            }
        }

        private void BuildClothing()
        {
            AddTextBox(Loc.T("Entity.Field.Brand"), ref brandBox);
            AddTextBox(Loc.T("Entity.Field.Color"), ref colorBox);
            AddComboBox(Loc.T("Entity.Field.Material"), ref materialBox, Choices.Material);
            AddComboBox(Loc.T("Entity.Field.Season"), ref seasonBox, Choices.Season);
            AddTextBox(Loc.T("Entity.Field.PurchasePrice"), ref priceBox);
            AddSizesSection();
        }

        private void AddSizesSection()
        {
            AddLabel(Loc.T("Entity.Field.Sizes"));
            sizesPanel = new StackPanel();
            FormPanel.Children.Add(sizesPanel);

            Button addSizeBtn = new Button();
            addSizeBtn.Content = "+ " + Loc.T("Entity.AddSize");
            addSizeBtn.MinWidth = 120;
            addSizeBtn.Padding = new Thickness(12, 0, 12, 0);
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
                condBox.Width = 150;
                condBox.Height = 32;
                condBox.Margin = new Thickness(0, 0, 6, 0);
                foreach (string c in Choices.Condition)
                    condBox.Items.Add(ChoiceItem(c));
                SetComboBox(condBox, sq.Condition);
                condBox.SelectionChanged += (s, e) => sq.Condition = GetComboValue(condBox);

                Button minusBtn = new Button();
                minusBtn.Content = "−";
                AutomationProperties.SetName(minusBtn, Loc.T("Entity.Size.Less"));
                minusBtn.Width = 32;
                minusBtn.Height = 32;
                minusBtn.Margin = new Thickness(0, 0, 4, 0);
                minusBtn.Click += (s, e) => { if (sq.Quantity > 0) sq.Quantity--; RenderSizes(); };

                TextBlock qty = new TextBlock();
                qty.Text = sq.Quantity.ToString();
                qty.Width = 24;
                qty.TextAlignment = TextAlignment.Center;
                qty.VerticalAlignment = VerticalAlignment.Center;
                qty.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1C1B19"));
                qty.FontSize = 14;

                Button plusBtn = new Button();
                plusBtn.Content = "+";
                AutomationProperties.SetName(plusBtn, Loc.T("Entity.Size.More"));
                plusBtn.Width = 32;
                plusBtn.Height = 32;
                plusBtn.Margin = new Thickness(4, 0, 6, 0);
                plusBtn.Click += (s, e) => { sq.Quantity++; RenderSizes(); };

                Button removeBtn = new Button();
                removeBtn.Content = "✕";
                AutomationProperties.SetName(removeBtn, Loc.T("Entity.Size.Remove"));
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
            AddLabel(Loc.T("Entity.Field.Image"));
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 0, 0, 4);

            Button pickBtn = new Button();
            pickBtn.Content = Loc.T("Entity.ChooseImage");
            pickBtn.Height = 32;
            pickBtn.Padding = new Thickness(12, 0, 12, 0);
            pickBtn.Click += PickImageButton_Click;

            imagePathText = new TextBlock();
            imagePathText.Text = Loc.T("Entity.NoImage");
            imagePathText.Foreground = (Brush)FindResource("TextSub"); // 4.7:1 and up, readable at this size
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
            label.Foreground = (Brush)FindResource("TextSub");
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
            foreach (string item in items)
                box.Items.Add(ChoiceItem(item));
            box.SelectedIndex = 0;
            FormPanel.Children.Add(box);
        }

        // A choice shows in the current language and keeps its stored (English) value in Tag.
        private static ComboBoxItem ChoiceItem(string value) => new ComboBoxItem { Content = Choices.Label(value), Tag = value };

        // Shows the stored value exactly: one that isn't among the choices (saved by an older version, or typed into
        // the file) is added as an extra choice, and an empty one stays empty, so saving the item unchanged never
        // replaces it with the first option.
        private void SetComboBox(ComboBox box, string value)
        {
            if (box == null) return; // a field this type doesn't have
            if (string.IsNullOrEmpty(value)) { box.SelectedIndex = -1; return; }
            foreach (ComboBoxItem item in box.Items)
            {
                if ((string)item.Tag == value)
                {
                    box.SelectedItem = item;
                    return;
                }
            }

            ComboBoxItem kept = new ComboBoxItem { Content = value, Tag = value };
            box.Items.Add(kept);
            box.SelectedItem = kept;
        }

        // The stored value of the picked choice, never its on-screen text.
        private string GetComboValue(ComboBox box)
            => (string)((ComboBoxItem)box?.SelectedItem)?.Tag;

        // Only previewed here; it's copied in when the item is saved (CollectionView).
        private void PickImageButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = Loc.T("Entity.ImageFilter");
            if (dialog.ShowDialog() == true)
            {
                PickedImageFile = dialog.FileName;
                imagePathText.Text = dialog.FileName;
                imagePreview.Source = EntityImages.Store.Decode(dialog.FileName);
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

            // A missing or unreadable picture just shows no preview.
            if (!string.IsNullOrEmpty(e.ImagePath))
            {
                imagePathText.Text = EntityImages.IsLegacyPath(e.ImagePath) ? e.ImagePath : Loc.T("Entity.CurrentImage");
                imagePreview.Source = EntityImages.Load(e);
            }

            foreach (KeyValuePair<FieldDefinition, Control> field in fieldControls)
            {
                string value = field.Key.Get(e);
                if (field.Value is ComboBox combo) SetComboBox(combo, value);
                else if (field.Value is DatePicker picker) picker.SelectedDate = Values.ParseDate(value);
                else if (field.Value is TextBox box) box.Text = value ?? "";
            }
            foreach (KeyValuePair<LogDefinition, LogEditor> log in logEditors)
                log.Value.Load(EntityTypes.Rows(e, log.Key));

            if (sizesPanel == null)
                return; // the rest is Clothing's

            if (brandBox != null) brandBox.Text = e.Brand;
            if (colorBox != null) colorBox.Text = e.Color;
            if (priceBox != null) priceBox.Text = e.PurchasePrice;
            SetComboBox(materialBox, e.Material);
            SetComboBox(seasonBox, e.Season);

            if (e.Sizes != null)
                foreach (var sq in e.Sizes)
                    sizes.Add(new SizeQuantity
                    {
                        Size = sq.Size,
                        Condition = sq.Condition,
                        Quantity = sq.Quantity,
                        ExtensionData = sq.ExtensionData
                    });
            RenderSizes();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // A log value that was just refused: fix it first (the reason is under its table).
            LogEditor? refused = logEditors.Values.FirstOrDefault(editor => editor.HasProblem);
            if (refused != null)
            {
                refused.BringIntoView();
                return;
            }

            if (string.IsNullOrWhiteSpace(nameBox?.Text))
            {
                Msg.Show(this, Loc.F("Entity.Required", EntityTypes.NameLabel(entityType)), Loc.T("Common.Error"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            foreach (KeyValuePair<FieldDefinition, Control> field in fieldControls)
            {
                if (FieldValue(field.Key, field.Value) == null)
                {
                    Msg.Show(this, Loc.F("Entity.NeedsNumber", field.Key.Label), Loc.T("Common.Error"), MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    field.Value.Focus();
                    return;
                }
            }

            // A copy through JSON carries everything, including fields this version doesn't know (ExtensionData).
            ResultEntity = existing != null
                ? JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(existing))
                : new Entity();
            ResultEntity.EntityType = entityType;
            ResultEntity.Name = nameBox.Text;
            ResultEntity.Notes = notesBox?.Text;
            ResultEntity.Brand = brandBox?.Text;
            ResultEntity.Color = colorBox?.Text;
            ResultEntity.PurchasePrice = priceBox?.Text;
            ResultEntity.Material = GetComboValue(materialBox);
            ResultEntity.Season = GetComboValue(seasonBox);
            ResultEntity.Sizes = sizes;

            foreach (KeyValuePair<FieldDefinition, Control> field in fieldControls)
            {
                string value = FieldValue(field.Key, field.Value);
                field.Key.Set(ResultEntity, value.Length == 0 ? null : value);
            }

            // Only logs with rows are stored; logs this version doesn't know stay as they were.
            foreach (KeyValuePair<LogDefinition, LogEditor> log in logEditors)
            {
                List<Dictionary<string, string>> rows = log.Value.FilledRows;
                if (rows.Count > 0)
                    (ResultEntity.Logs ??= new Dictionary<string, List<Dictionary<string, string>>>())[log.Key.Key] = rows;
                else
                    ResultEntity.Logs?.Remove(log.Key.Key);
            }
            if (ResultEntity.Logs?.Count == 0)
                ResultEntity.Logs = null;

            this.DialogResult = true;
            this.Close();
        }
    }
}
