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

        // The item's pictures as the form leaves them, cover first: ones it already had (by stored name) and files
        // picked in the form (the user's own). The caller copies the picked ones in (EntityImages.Apply).
        public IReadOnlyList<PictureSlot> Pictures => pictures;

        private readonly List<PictureSlot> pictures = new List<PictureSlot>();
        private int selectedPicture = -1;
        private PictureBrowser pictureBrowser;
        private WrapPanel pictureTools;
        private Button coverBtn, earlierBtn, laterBtn, removePictureBtn;

        private readonly Entity existing;
        private string entityType;

        // Every type
        private TextBox nameBox;
        private TextBox notesBox;
        private TextBlock imagePathText;

        // The fields of its type asked for here (FieldDefinition.InForm); the rest are filled in the item window.
        private readonly List<FieldEditor> fieldEditors = new List<FieldEditor>();

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

        private string CurrentInput() => string.Join("\n", nameBox?.Text, notesBox?.Text,
            string.Join("|", pictures.Select(p => p.Stored ?? p.PickedFile)),
            string.Join("\n", fieldEditors.Select(f => f.Value)));

        // Closing without saving (X, Alt+F4) asks first when something was typed or changed; Save doesn't.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DialogResult != true && CurrentInput() != opened
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
            AddTextBox(Loc.T("Entity.Field.Name"), ref nameBox);
            BuildDefinedFields();
            // A custom item's notes are its layout's Notes blocks; its own notes field only shows when something (from
            // elsewhere) is in it.
            if (!CustomTypes.IsCustom(entityType) || !string.IsNullOrEmpty(existing?.Notes))
                AddTextBoxMultiline(Loc.T("Entity.Field.Notes"), ref notesBox);
        }

        // ---- The type's fields (EntityTypes) -------------------------------

        // The fields it asks for here, starting from the item's values when editing.
        private void BuildDefinedFields()
        {
            Entity owner = existing ?? new Entity { EntityType = entityType };
            foreach (FieldDefinition field in EntityTypes.Fields(entityType).Where(f => f.InForm))
            {
                FieldEditor editor = new FieldEditor(field, owner, field.Read(owner));
                fieldEditors.Add(editor);
                if (!editor.HasOwnLabel)
                    AddLabel(field.Label);
                FormPanel.Children.Add(editor.Element);
            }
        }

        // The item's pictures: a button to add several at once, the selected picture large with arrows to browse,
        // the strip of thumbnails under it (the first is the cover), and what can be done with the selected one:
        // make it the cover, move it earlier or later (or drag it in the strip), remove it.
        private void AddImagePicker()
        {
            AddLabel(Loc.T("Entity.Field.Pictures"));
            StackPanel row = new StackPanel();
            row.Orientation = Orientation.Horizontal;
            row.Margin = new Thickness(0, 0, 0, 8);

            Button pickBtn = new Button();
            pickBtn.Content = Loc.T("Entity.ChoosePictures");
            pickBtn.Height = 32;
            pickBtn.Padding = new Thickness(12, 0, 12, 0);
            pickBtn.Click += PickImageButton_Click;

            imagePathText = new TextBlock();
            imagePathText.Foreground = (Brush)FindResource("TextSub"); // 4.7:1 and up, readable at this size
            imagePathText.FontSize = 11;
            imagePathText.VerticalAlignment = VerticalAlignment.Center;
            imagePathText.Margin = new Thickness(12, 0, 0, 0);

            row.Children.Add(pickBtn);
            row.Children.Add(imagePathText);
            FormPanel.Children.Add(row);

            pictureBrowser = new PictureBrowser(200, double.PositiveInfinity, canReorder: true, stripForOne: true)
            {
                Margin = new Thickness(0, 4, 0, 0)
            };
            pictureBrowser.Picked += index => { selectedPicture = index; ShowPictures(); };
            pictureBrowser.Moved += (from, to) => { selectedPicture = from; MovePicture(to); };
            FormPanel.Children.Add(pictureBrowser);

            pictureTools = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
            coverBtn = ToolButton("Entity.Picture.SetCover", () => MovePicture(0));
            earlierBtn = ToolButton("Entity.Picture.Earlier", () => MovePicture(selectedPicture - 1));
            laterBtn = ToolButton("Entity.Picture.Later", () => MovePicture(selectedPicture + 1));
            removePictureBtn = ToolButton("Entity.Picture.Remove", RemovePicture);
            foreach (Button tool in new[] { coverBtn, earlierBtn, laterBtn, removePictureBtn })
                pictureTools.Children.Add(tool);
            FormPanel.Children.Add(pictureTools);
            ShowPictures();
        }

        private Button ToolButton(string key, Action click)
        {
            Button button = new Button
            {
                Content = Loc.T(key),
                Style = (Style)FindResource("GhostButtonStyle"),
                Margin = new Thickness(0, 0, 8, 8)
            };
            button.Click += (s, e) => click();
            return button;
        }

        // Everything about the pictures is drawn from the list and selectedPicture, after any change to either.
        private void ShowPictures()
        {
            selectedPicture = pictures.Count == 0 ? -1 : Math.Clamp(selectedPicture, 0, pictures.Count - 1);

            foreach (PictureSlot slot in pictures)
                slot.Preview ??= slot.Stored != null ? EntityImages.Load(slot.Stored) : EntityImages.Store.Decode(slot.PickedFile);

            pictureBrowser.Show(pictures.Select(p => p.Preview).ToList(), selectedPicture);
            pictureBrowser.Visibility = pictures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            imagePathText.Text = pictures.Count == 0 ? Loc.T("Entity.NoPictures") : Loc.Plural("Entity.PictureCount", pictures.Count);

            // One picture: it is the cover and there's nowhere to move it, so only Remove.
            pictureTools.Visibility = pictures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (Button move in new[] { coverBtn, earlierBtn, laterBtn })
                move.Visibility = pictures.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            coverBtn.IsEnabled = earlierBtn.IsEnabled = selectedPicture > 0;
            laterBtn.IsEnabled = selectedPicture >= 0 && selectedPicture < pictures.Count - 1;
            foreach (Button move in new[] { coverBtn, earlierBtn, laterBtn })
                move.Opacity = move.IsEnabled ? 1 : 0.45; // the ghost style has no look of its own for "off"
        }

        // The selected picture to a place in the list (0 = the cover); it stays selected there.
        private void MovePicture(int to)
        {
            if (selectedPicture < 0 || to < 0 || to >= pictures.Count || to == selectedPicture)
                return;
            PictureSlot slot = pictures[selectedPicture];
            pictures.RemoveAt(selectedPicture);
            pictures.Insert(to, slot);
            selectedPicture = to;
            ShowPictures();
        }

        // The one after it is selected next, or the one before when it was the last.
        private void RemovePicture()
        {
            if (selectedPicture < 0)
                return;
            pictures.RemoveAt(selectedPicture);
            ShowPictures();
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

        // The dialogs' form box (NoteInputStyle), as the item window's fields have.
        private void AddTextBox(string label, ref TextBox box)
        {
            AddLabel(label);
            box = new TextBox { Style = (Style)FindResource("NoteInputStyle"), Height = 36 };
            FormPanel.Children.Add(box);
        }

        private void AddTextBoxMultiline(string label, ref TextBox box)
        {
            AddLabel(label);
            box = new TextBox
            {
                Style = (Style)FindResource("NoteInputStyle"),
                Height = 88,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            FormPanel.Children.Add(box);
        }

        // Several files at once, added after the pictures already there (the first of them selected). Only previewed
        // here; they're copied in when the item is saved (CollectionView). A file that isn't a readable picture is
        // left out, and so are the ones over the limit; the message says so.
        private void PickImageButton_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog();
            dialog.Filter = Loc.T("Entity.ImageFilter");
            dialog.Multiselect = true;
            if (dialog.ShowDialog() == true)
                AddPictures(dialog.FileNames);
        }

        internal void AddPictures(IEnumerable<string> files)
        {
            int firstNew = PictureSlot.AddPicked(pictures, files, this);
            if (firstNew >= 0)
                selectedPicture = firstNew;
            ShowPictures();
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

            // A missing or unreadable picture just shows an empty frame.
            foreach (string name in EntityImages.Names(e))
                pictures.Add(new PictureSlot { Stored = name });
            selectedPicture = 0;
            ShowPictures();
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(nameBox?.Text))
            {
                Msg.Show(this, Loc.F("Entity.Required", Loc.T("Entity.Field.Name")), Loc.T("Common.Error"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            FieldEditor unreadable = fieldEditors.FirstOrDefault(editor => editor.Value == null);
            if (unreadable != null)
            {
                Msg.Show(this, Loc.F("Entity.NeedsNumber", unreadable.Field.Label), Loc.T("Common.Error"), MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                unreadable.Focus();
                return;
            }

            // A copy through JSON carries everything, including fields this version doesn't know (ExtensionData).
            ResultEntity = existing != null
                ? JsonSerializer.Deserialize<Entity>(JsonSerializer.Serialize(existing))
                : new Entity();
            ResultEntity.EntityType = entityType;
            ResultEntity.Name = nameBox.Text;
            ResultEntity.Notes = notesBox?.Text;
            foreach (FieldEditor editor in fieldEditors)
                editor.Field.Write(ResultEntity, editor.Value);

            this.DialogResult = true;
            this.Close();
        }
    }
}
