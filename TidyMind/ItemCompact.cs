using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // Something the item window lets one at a time be edited in place (a section's fields, a line of a compact list):
    // a second one starting asks the first to stop, and stopping asks first when something was typed.
    public interface IInlineEdit
    {
        bool IsEditing { get; }

        // Asks before throwing typed changes away; false if the user wants to go on editing.
        bool ConfirmLeave();
        void CancelEdit();
    }

    // A short list of small records (a garment's sizes, RecordDefinition.Compact) as a table that is all there at once:
    // a line each with a column per field, no row to open. The number named by StepKey has - and + beside it that
    // save at once; Edit and Delete are small icons at the line's end, shown while the pointer is on it or the keyboard
    // is in it. Add and Edit open a line in the table itself (its inputs under the column names), not a form.
    public sealed class CompactRecords : StackPanel, IInlineEdit
    {
        private const double StepperWidth = 116, IconsWidth = 72, MaxPieces = 9999;

        private readonly RecordDefinition records;
        private readonly Entity owner;
        private readonly List<EntityRecord> list;
        private readonly FieldDefinition step;
        private readonly Func<Action<List<EntityRecord>>, bool> change;
        private readonly Action<EntityRecord> delete;
        private readonly Action<Guid, bool> stepped;
        private readonly FieldsSection.EditGate gate;
        private readonly StackPanel body = new StackPanel();

        // The line being edited: editors set, and either an existing record's Id or (editedId null) the new one in draft.
        private List<FieldEditor>? editors;
        private Guid? editedId;
        private EntityRecord? draft;

        // change: applies an edit to the item's stored list and saves it, false if it couldn't (the user was told).
        // delete: asks, then removes. stepped: which button was pressed, so the window can put the keyboard back on it
        // when it has drawn the new count. focus: that button, from the last time.
        public CompactRecords(RecordDefinition records, List<EntityRecord> list, Entity owner, Func<Action<List<EntityRecord>>, bool> change,
            Action<EntityRecord> delete, Action<Guid, bool> stepped, FieldsSection.EditGate gate, (Guid Record, bool Plus)? focus)
        {
            this.records = records;
            this.list = list;
            this.owner = owner;
            this.change = change;
            this.delete = delete;
            this.stepped = stepped;
            this.gate = gate;
            step = records.Fields.First(f => f.Key == records.StepKey);

            Grid heading = ItemLook.Heading(records.Label, out StackPanel actions);
            actions.Children.Add(ItemLook.Link("+ " + records.AddLabel, BeginAdd));
            Children.Add(heading);
            Children.Add(body);
            Margin = new Thickness(0, 0, 0, ItemLook.SectionBottom - 12);
            Render(focus);
        }

        public bool IsEditing => editors != null;
        public bool HasChanges => editors != null && editors.Any(e => e.Changed);

        public bool ConfirmLeave() =>
            !HasChanges || Msg.Show(Window.GetWindow(this), Loc.T("Common.DiscardChanges"), Loc.T("Common.UnsavedChanges"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        public void CancelEdit()
        {
            editors = null;
            editedId = null;
            draft = null;
            Render(null);
        }

        // ---- Drawing -----------------------------------------------------------------------------

        // The columns every line shares: a field each (the stepped one a fixed width), then the room for the icons.
        private Grid Line()
        {
            Grid line = new Grid();
            foreach (FieldDefinition field in records.Fields)
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = field == step ? new GridLength(StepperWidth) : new GridLength(1, GridUnitType.Star), MinWidth = field == step ? 0 : 72 });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconsWidth) });
            return line;
        }

        private string Heading(FieldDefinition field) =>
            field == step ? records.Text("Count") : field.Label;

        private void Render((Guid Record, bool Plus)? focus)
        {
            body.Children.Clear();
            if (list.Count == 0 && editors == null)
            {
                body.Children.Add(new StackPanel { Margin = new Thickness(ItemLook.Gap, 0, ItemLook.Gap, 12), Children = { ItemLook.Muted(records.Text("Empty")) } });
                return;
            }

            Grid names = Line();
            names.Margin = new Thickness(ItemLook.Gap, 0, ItemLook.Gap, 6);
            for (int i = 0; i < records.Fields.Length; i++)
            {
                TextBlock name = ItemLook.Muted(Heading(records.Fields[i]), 12);
                name.TextWrapping = TextWrapping.NoWrap;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
                Grid.SetColumn(name, i);
                names.Children.Add(name);
            }
            body.Children.Add(names);

            foreach (EntityRecord record in list)
            {
                body.Children.Add(ItemLook.Divider());
                body.Children.Add(editors != null && editedId == record.Id ? EditLine() : ReadLine(record, focus));
            }
            if (editors != null && editedId == null)
            {
                body.Children.Add(ItemLook.Divider());
                body.Children.Add(EditLine());
            }
        }

        private FrameworkElement ReadLine(EntityRecord record, (Guid Record, bool Plus)? focus)
        {
            Grid line = Line();
            for (int i = 0; i < records.Fields.Length; i++)
            {
                FieldDefinition field = records.Fields[i];
                FrameworkElement cell;
                if (field == step)
                    cell = Stepper(record, focus);
                else
                {
                    string text = i == 0 ? records.Title(record) : field.Show(record.Get(field.Key), owner);
                    cell = new TextBlock
                    {
                        Text = text,
                        FontSize = i == 0 ? 14 : 13,
                        FontWeight = i == 0 ? FontWeights.SemiBold : FontWeights.Normal,
                        Foreground = ItemLook.Res(i == 0 ? "TextMain" : "TextBody"),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                        Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                        ToolTip = text.Length > 0 ? text : null
                    };
                }
                Grid.SetColumn(cell, i);
                line.Children.Add(cell);
            }

            StackPanel icons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
            icons.Children.Add(ItemLook.RowAction("", Loc.T("Common.Edit"), () => BeginEdit(record)));
            icons.Children.Add(ItemLook.RowAction("", Loc.T("Common.Delete"), () => delete(record), danger: true));
            Grid.SetColumn(icons, records.Fields.Length);
            line.Children.Add(icons);

            Border row = new Border { Padding = new Thickness(ItemLook.Gap, 6, ItemLook.Gap, 6), MinHeight = 44, Background = Brushes.Transparent, Child = line };
            AutomationProperties.SetName(row, records.Title(record));
            void Show() => icons.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) =>
            {
                row.Background = ItemLook.Res("BgSubtle");
                Show();
            };
            row.MouseLeave += (s, e) =>
            {
                row.Background = row.IsKeyboardFocusWithin ? ItemLook.Res("BgSubtle") : Brushes.Transparent;
                Show();
            };
            row.IsKeyboardFocusWithinChanged += (s, e) =>
            {
                row.Background = row.IsMouseOver || row.IsKeyboardFocusWithin ? ItemLook.Res("BgSubtle") : Brushes.Transparent;
                Show();
            };
            return row;
        }

        // - count +: each press is saved at once. Never below none.
        private FrameworkElement Stepper(EntityRecord record, (Guid Record, bool Plus)? focus)
        {
            double count = Values.Number(record.Get(step.Key)) ?? 0; // a whole count (a garment's pieces) or an amount (2.5 kg)
            string title = records.Title(record);
            Button less = ItemLook.RowAction("", records.Text("Fewer"), () => Step(record, -1));
            Button more = ItemLook.RowAction("", records.Text("More"), () => Step(record, +1));
            less.IsEnabled = count > 0;
            more.IsEnabled = count < MaxPieces;
            less.Opacity = less.IsEnabled ? 1 : 0.35; // the button style has no look of its own for a disabled one
            more.Opacity = more.IsEnabled ? 1 : 0.35;
            less.Margin = more.Margin = new Thickness(0);
            AutomationProperties.SetHelpText(less, title);
            AutomationProperties.SetHelpText(more, title);

            TextBlock shown = new TextBlock
            {
                Text = count.ToString("0.###", Loc.Culture),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = ItemLook.Res("TextMain"),
                MinWidth = 36,
                TextAlignment = TextAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = Units.Format(count, step.Unit, owner)
            };
            StackPanel stepper = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4, 0, 0, 0) };
            stepper.Children.Add(less);
            stepper.Children.Add(shown);
            stepper.Children.Add(more);

            // The line was drawn again by the press that made it: the keyboard goes back where it was.
            if (focus is { } was && was.Record == record.Id)
            {
                Button back = was.Plus ? more : less;
                if (back.IsEnabled)
                    back.Dispatcher.BeginInvoke(new Action(() => back.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            }
            return stepper;
        }

        private void Step(EntityRecord record, int by)
        {
            if (editors != null && !ConfirmLeave())
                return;
            stepped(record.Id, by > 0);
            change(stored =>
            {
                EntityRecord target = stored.First(r => r.Id == record.Id);
                double now = Values.Number(target.Get(step.Key)) ?? 0;
                target.Set(step.Key, Math.Clamp(now + by, 0, MaxPieces).ToString("0.###", CultureInfo.InvariantCulture));
            });
        }

        // ---- Add and Edit, in the table ------------------------------------------------------------

        private void BeginAdd()
        {
            if (editors != null && editedId == null)
            {
                editors[0].Focus(); // the line for a new one is open already
                return;
            }
            Begin(null, records.New(owner));
        }

        private void BeginEdit(EntityRecord record) => Begin(record.Id, record);

        private void Begin(Guid? id, EntityRecord source)
        {
            if (editors != null && !ConfirmLeave())
                return;
            if (!gate(this))
                return;
            editedId = id;
            draft = id == null ? source : null;
            editors = records.Fields.Select(f => new FieldEditor(f, owner, source.Get(f.Key), source)).ToList();
            Render(null);
            Dispatcher.BeginInvoke(new Action(() => editors?.FirstOrDefault()?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        // The inputs in the columns of the lines above, then Save and Cancel under them.
        private FrameworkElement EditLine()
        {
            Grid line = Line();
            for (int i = 0; i < editors!.Count; i++)
            {
                FrameworkElement input = editors[i].Element;
                input.Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0);
                Grid.SetColumn(input, i);
                line.Children.Add(input);
            }

            Button save = new Button { Content = Loc.T("Common.Save"), MinWidth = 80, Height = 30, Padding = new Thickness(14, 0, 14, 0), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 8, 0) };
            Button cancel = new Button { Content = Loc.T("Common.Cancel"), MinWidth = 80, Height = 30, Padding = new Thickness(14, 0, 14, 0), Style = (Style)Application.Current.FindResource("GhostButtonStyle") };
            save.Click += (s, e) => Save();
            cancel.Click += (s, e) =>
            {
                if (ConfirmLeave())
                    CancelEdit();
            };
            StackPanel buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);

            StackPanel panel = new StackPanel();
            panel.Children.Add(line);
            panel.Children.Add(buttons);
            panel.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter && e.OriginalSource is TextBox && Keyboard.Modifiers == ModifierKeys.None)
                {
                    e.Handled = true;
                    Save();
                }
            };
            return new Border { Padding = new Thickness(ItemLook.Gap, 12, ItemLook.Gap, 12), Background = ItemLook.Res("BgSubtle"), Child = panel };
        }

        private void Save()
        {
            if (editors == null || !EditDialog.Check(editors, Window.GetWindow(this)))
                return;
            List<FieldEditor> typed = editors;
            Guid? id = editedId;
            EntityRecord? added = draft;
            // The window draws the list again from what was stored (and so this table); if it couldn't be stored, the line stays.
            change(stored =>
            {
                EntityRecord target = id == null ? added! : stored.First(r => r.Id == id);
                foreach (FieldEditor editor in typed)
                    target.Set(editor.Field.Key, editor.Value);
                if (id == null)
                    stored.Add(target);
            });
        }
    }
}
