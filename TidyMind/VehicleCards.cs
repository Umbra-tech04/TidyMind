using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // A white card of the vehicle window: a heading with its actions on the right, and what it shows under it. The
    // window lets one card at a time be edited (EditGate): starting an edit asks the one being edited to stop first.
    public class VehicleCard : Border
    {
        // The window's say before a card starts editing: false keeps the other card's edit (the user wants to go on).
        public delegate bool EditGate(VehicleCard card);

        public readonly StackPanel Actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        public readonly StackPanel Body = new StackPanel();

        public VehicleCard(string title)
        {
            Background = Res("BgPanel");
            BorderBrush = Res("Border");
            BorderThickness = new Thickness(1);
            CornerRadius = new CornerRadius(12);
            Padding = new Thickness(18, 14, 18, 16);

            Grid header = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Res("TextMain"),
                TextWrapping = TextWrapping.Wrap
            });
            Grid.SetColumn(Actions, 1);
            header.Children.Add(Actions);

            StackPanel content = new StackPanel();
            content.Children.Add(header);
            content.Children.Add(Body);
            Child = content;
        }

        public virtual bool IsEditing => false;
        public virtual bool HasChanges => false;
        public virtual void CancelEdit() { }

        // Asks before throwing typed changes away; false if the user wants to go on editing. Nothing to lose: true.
        public bool ConfirmLeave() =>
            !HasChanges || Msg.Show(Window.GetWindow(this), Loc.T("Common.DiscardChanges"), Loc.T("Common.UnsavedChanges"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        public static Button Link(string text, Action click, bool danger = false)
        {
            Button link = new Button
            {
                Content = text,
                Style = (Style)Application.Current.FindResource("LinkButtonStyle"),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (danger)
                link.Foreground = Res("Danger");
            link.Click += (s, e) => click();
            return link;
        }

        public static TextBlock Muted(string text, double size = 13) => new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = Res("TextSub"),
            TextWrapping = TextWrapping.Wrap
        };

        // A field's name over its value, as the cards show them.
        public static StackPanel Cell(string label, UIElement value)
        {
            StackPanel cell = new StackPanel();
            cell.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Res("TextSub"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 2) });
            cell.Children.Add(value);
            return cell;
        }

        // Nothing in it yet: one line saying so, and the way to fill it.
        public void ShowEmpty(string text, string action, Action act)
        {
            Body.Children.Add(Muted(text));
            Button button = new Button
            {
                Content = action,
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 10, 0, 0)
            };
            button.Click += (s, e) => act();
            Body.Children.Add(button);
        }

        // Save and Cancel under an edit.
        protected StackPanel EditButtons(Func<Task> save, Action cancel)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            Button saveButton = new Button { Content = Loc.T("Common.Save"), MinWidth = 90, Height = 32, Padding = new Thickness(14, 0, 14, 0), FontWeight = FontWeights.SemiBold };
            Button cancelButton = new Button { Content = Loc.T("Common.Cancel"), MinWidth = 90, Padding = new Thickness(14, 0, 14, 0),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"), Margin = new Thickness(10, 0, 0, 0) };
            saveButton.Click += async (s, e) =>
            {
                saveButton.IsEnabled = cancelButton.IsEnabled = false;
                await save();
                saveButton.IsEnabled = cancelButton.IsEnabled = true;
            };
            cancelButton.Click += (s, e) => cancel();
            row.Children.Add(saveButton);
            row.Children.Add(cancelButton);
            return row;
        }
    }

    // A card of fields: a section of the vehicle, or one record (a document). Read, it lists the filled fields (a
    // document's date with how close it is, its file as a link) and offers Edit; editing, an input per field
    // (FieldEditor) with Save and Cancel. What Save does with the values is the window's (save); the card checks them.
    public sealed class SectionCard : VehicleCard
    {
        public delegate Task<bool> SaveHandler(IReadOnlyList<FieldEditor> editors);

        private readonly IReadOnlyList<FieldDefinition> fields;
        private readonly Entity owner;
        private readonly Func<FieldDefinition, string?> read;
        private readonly Attachment? file;
        private readonly EditGate gate;
        private readonly SaveHandler save;
        private List<FieldEditor>? editors;

        // A record's card: Delete beside Edit. A new record's card: Cancel takes it away again (Cancelled).
        public Action? Delete { get; init; }
        public Action? Cancelled { get; init; }

        public SectionCard(string title, IReadOnlyList<FieldDefinition> fields, Entity owner, Func<FieldDefinition, string?> read,
            Attachment? file, EditGate gate, SaveHandler save) : base(title)
        {
            this.fields = fields;
            this.owner = owner;
            this.read = read;
            this.file = file;
            this.gate = gate;
            this.save = save;
            Loaded += (s, e) => { if (editors == null) ShowRead(); };
        }

        public override bool IsEditing => editors != null;
        public override bool HasChanges => editors != null && editors.Any(e => e.Changed);

        public override void CancelEdit()
        {
            editors = null;
            if (Cancelled != null)
                Cancelled();
            else
                ShowRead();
        }

        public void BeginEdit()
        {
            if (editors != null || !gate(this))
                return;
            editors = fields.Select(f => new FieldEditor(f, owner, f.Kind == ValueKind.File ? null : read(f), file)).ToList();
            Actions.Children.Clear();
            Body.Children.Clear();

            CellPanel cells = new CellPanel { MinColumn = 190, RowGap = 12 };
            foreach (FieldEditor editor in editors)
            {
                UIElement cell = editor.HasOwnLabel ? editor.Element : Cell(editor.Field.Label, editor.Element);
                CellPanel.SetWide(cell, editor.IsWide);
                cells.Children.Add(cell);
            }
            Body.Children.Add(cells);
            Body.Children.Add(EditButtons(Save, () => { if (ConfirmLeave()) CancelEdit(); }));
            Dispatcher.BeginInvoke(new Action(() => editors?.FirstOrDefault()?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        private async Task Save()
        {
            Keyboard.ClearFocus();
            FieldEditor? unreadable = editors?.FirstOrDefault(e => e.Value == null);
            if (unreadable != null)
            {
                Msg.Show(Window.GetWindow(this), Loc.F("Entity.NeedsNumber", unreadable.Field.Label), Loc.T("Common.Error"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                unreadable.Focus();
                return;
            }
            if (editors != null && await save(editors))
            {
                editors = null;
                ShowRead();
            }
        }

        private void ShowRead()
        {
            Actions.Children.Clear();
            Body.Children.Clear();
            List<UIElement> shown = fields.Select(ReadCell).Where(c => c != null).ToList()!;
            if (shown.Count == 0)
            {
                ShowEmpty(Loc.T("Entity.Card.Empty"), Loc.T("Entity.Card.Fill"), BeginEdit);
                if (Delete != null)
                    Actions.Children.Add(Link(Loc.T("Common.Delete"), Delete, danger: true));
                return;
            }

            Actions.Children.Add(Link(Loc.T("Common.Edit"), BeginEdit));
            if (Delete != null)
                Actions.Children.Add(Link(Loc.T("Common.Delete"), Delete, danger: true));
            CellPanel cells = new CellPanel { MinColumn = 170, RowGap = 12 };
            foreach (UIElement cell in shown)
                cells.Children.Add(cell);
            Body.Children.Add(cells);
        }

        // The field's cell, or null when it's empty.
        private UIElement? ReadCell(FieldDefinition field)
        {
            if (field.Kind == ValueKind.File)
                return file == null ? null : Cell(field.Label, FileLink(file));

            string? stored = read(field);
            string shown = field.Show(stored, owner);
            if (shown.Length == 0)
                return null;

            UIElement value;
            if (field.IsExpiry && Values.ParseDate(stored) is DateTime date)
            {
                ExpiryLevel level = Expiry.Level(date, DateTime.Today);
                value = TextCopy.Selectable(shown + ", " + Expiry.Describe(date, DateTime.Today), 13, Expiry.Brush(level), Expiry.Weight(level));
            }
            else
                value = TextCopy.Selectable(shown, 13, Res("TextMain"));
            StackPanel cell = Cell(field.Label, value);
            CellPanel.SetWide(cell, field.Kind == ValueKind.LongText || field.Kind == ValueKind.CheckList);
            return cell;
        }

        // Opens the file with its program; one that has gone missing says so.
        private static UIElement FileLink(Attachment attachment)
        {
            Button link = Link(attachment.DisplayName ?? "", () =>
            {
                if (!AttachmentStore.Exists(attachment))
                {
                    Msg.Show(Loc.F("Entity.File.Missing", attachment.DisplayName), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                Process.Start(new ProcessStartInfo(AttachmentStore.PathOf(attachment.StoredFileName)) { UseShellExecute = true });
            });
            link.Margin = new Thickness(0);
            link.HorizontalAlignment = HorizontalAlignment.Left;
            link.ToolTip = Loc.T("Entity.File.Open");
            return link;
        }
    }

    // A log of the vehicle (service, fuel, odometer readings): read, its rows newest first and its summary; editing,
    // the log's table (LogEditor) with Save and Cancel.
    public sealed class LogCard : VehicleCard
    {
        private readonly LogDefinition log;
        public LogDefinition Log => log;
        private readonly List<Dictionary<string, string>> rows;
        private readonly EditGate gate;
        private readonly Func<List<Dictionary<string, string>>, bool> save;
        private LogEditor? editor;
        private string opened = "";

        public LogCard(LogDefinition log, List<Dictionary<string, string>> rows, EditGate gate, Func<List<Dictionary<string, string>>, bool> save)
            : base(log.Label)
        {
            this.log = log;
            this.rows = rows;
            this.gate = gate;
            this.save = save;
            ShowRead();
        }

        public override bool IsEditing => editor != null;

        // A cell still being typed in counts too (leaving it stores it), and so does a refused value.
        public override bool HasChanges
        {
            get
            {
                if (editor == null)
                    return false;
                Keyboard.ClearFocus();
                return editor.HasProblem || JsonSerializer.Serialize(editor.FilledRows) != opened;
            }
        }

        public override void CancelEdit()
        {
            editor = null;
            ShowRead();
        }

        public void BeginEdit()
        {
            if (editor != null || !gate(this))
                return;
            editor = new LogEditor(log);
            editor.Load(rows);
            opened = JsonSerializer.Serialize(editor.FilledRows);
            Actions.Children.Clear();
            Body.Children.Clear();
            Body.Children.Add(editor);
            Body.Children.Add(EditButtons(() =>
            {
                Keyboard.ClearFocus();
                if (editor.HasProblem)
                    editor.BringIntoView();
                else if (save(editor.FilledRows))
                {
                    editor = null;
                    ShowRead();
                }
                return Task.CompletedTask;
            }, () => { if (ConfirmLeave()) CancelEdit(); }));
        }

        private void ShowRead()
        {
            Actions.Children.Clear();
            Body.Children.Clear();
            if (rows.Count == 0)
            {
                ShowEmpty(Loc.T("Entity.Log.Empty"), Loc.T("Log.AddRow"), BeginEdit);
                return;
            }
            Actions.Children.Add(Link(Loc.T("Common.Edit"), BeginEdit));
            foreach (Dictionary<string, string> row in EntityTypes.Sorted(log, rows))
            {
                TextBox line = TextCopy.Selectable(EntityTypes.FormatRow(log, row), 13, Res("TextMain"));
                line.Margin = new Thickness(0, 0, 0, 6);
                Body.Children.Add(line);
            }
            if (log.Summary?.Invoke(rows) is string summary)
                Body.Children.Add(Muted(summary, 12));
        }
    }
}
