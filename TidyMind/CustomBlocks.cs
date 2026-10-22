using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // Two half-width blocks of a custom item (LayoutBlock.Half) side by side with a thin line between them, or one under
    // the other when the row is narrower than two of them need. A half block with no half block beside it keeps its half
    // of the row (the other half stays empty), as the layout editor shows it.
    public sealed class HalfRow : Panel
    {
        public const double MinHalf = 320;

        private readonly UIElement left;
        private readonly UIElement? right;
        private readonly Border line = new Border { Background = ItemLook.Res("Border"), SnapsToDevicePixels = true };

        public HalfRow(UIElement left, UIElement? right)
        {
            this.left = left;
            this.right = right;
            Children.Add(left);
            if (right != null)
                Children.Add(right);
            Children.Add(line);
        }

        // Side by side while each half has MinHalf.
        public static bool Beside(double width) => double.IsInfinity(width) || width >= 2 * MinHalf + 1;

        protected override Size MeasureOverride(Size available)
        {
            double width = available.Width;
            if (Beside(width))
            {
                double half = double.IsInfinity(width) ? double.PositiveInfinity : (width - 1) / 2;
                left.Measure(new Size(half, double.PositiveInfinity));
                right?.Measure(new Size(half, double.PositiveInfinity));
                double height = Math.Max(left.DesiredSize.Height, right?.DesiredSize.Height ?? 0);
                line.Measure(new Size(1, height));
                return new Size(double.IsInfinity(width) ? left.DesiredSize.Width * 2 + 1 : width, height);
            }
            left.Measure(new Size(width, double.PositiveInfinity));
            right?.Measure(new Size(width, double.PositiveInfinity));
            line.Measure(new Size(width, 1));
            return new Size(width, left.DesiredSize.Height + (right == null ? 0 : 1 + right.DesiredSize.Height));
        }

        protected override Size ArrangeOverride(Size final)
        {
            if (Beside(final.Width))
            {
                double half = (final.Width - 1) / 2;
                left.Arrange(new Rect(0, 0, half, final.Height));
                right?.Arrange(new Rect(half + 1, 0, half, final.Height));
                line.Arrange(new Rect(half, 0, 1, final.Height));
                line.Visibility = right == null ? Visibility.Hidden : Visibility.Visible;
                return final;
            }
            left.Arrange(new Rect(0, 0, final.Width, left.DesiredSize.Height));
            line.Visibility = right == null ? Visibility.Hidden : Visibility.Visible;
            line.Arrange(new Rect(0, left.DesiredSize.Height, final.Width, 1));
            right?.Arrange(new Rect(0, left.DesiredSize.Height + 1, final.Width, right.DesiredSize.Height));
            return final;
        }

        // The blocks as rows: a full block alone, two half blocks in a row together (a half one next to a full one alone
        // in its half).
        public static List<UIElement> Rows(IReadOnlyList<(UIElement Element, bool Half)> blocks)
        {
            List<UIElement> rows = new List<UIElement>();
            for (int i = 0; i < blocks.Count; i++)
            {
                if (!blocks[i].Half)
                {
                    rows.Add(blocks[i].Element);
                    continue;
                }
                UIElement? partner = i + 1 < blocks.Count && blocks[i + 1].Half ? blocks[++i].Element : null;
                rows.Add(new HalfRow(blocks[i - (partner == null ? 0 : 1)].Element, partner));
            }
            return rows;
        }
    }

    // A checklist block of a custom item (BlockKinds.Checklist): a line each with its tick, the open ones above the done
    // ones (TaskOrder: shown so, stored in the order they were added), each change saved at once; a line's text edited in
    // place (TextEditing); a box at the end adds a line (Enter). Delete is a small icon at the line's end, shown while
    // the pointer or the keyboard is on it; several lines are selected and deleted at once as in the to-do lists
    // (ItemSelection: Ctrl, Shift, Ctrl+A, Esc, Delete).
    public sealed class ChecklistSection : StackPanel
    {
        private readonly RecordDefinition list;
        private readonly Func<Action<List<EntityRecord>>, bool> change;
        private readonly Action<Guid?> keep;
        private readonly ItemSelection<Guid> selection;
        private readonly Dictionary<Guid, CheckBox> ticks = new Dictionary<Guid, CheckBox>();
        private readonly TextBox input;

        // change: applies an edit to the item's stored lines and saves it, false if it couldn't (the user was told). keep:
        // which line's tick (or, Guid.Empty, the add box) has the keyboard, so the window gives it back once it has drawn
        // the list again. focus: that, from the last time.
        public ChecklistSection(RecordDefinition list, Entity owner, Func<Action<List<EntityRecord>>, bool> change, Action<Guid?> keep, Guid? focus)
        {
            this.list = list;
            this.change = change;
            this.keep = keep;
            Margin = new Thickness(0, 0, 0, ItemLook.SectionBottom - 12);

            List<(EntityRecord Line, bool Done)> lines = CustomType.ChecklistLines(list, owner);
            Grid heading = ItemLook.Heading(list.Label, out StackPanel actions);
            if (lines.Count > 0)
                actions.Children.Add(ItemLook.Muted(Loc.F("Todo.Count", lines.Count(l => l.Done), lines.Count)));
            Children.Add(heading);

            StackPanel rows = new StackPanel();
            selection = new ItemSelection<Guid>(rows, DeleteSelected,
                (row, on) => SelectionLook.PaintRow((Border)row, on, Brushes.Transparent, Brushes.Transparent));
            selection.BeginRender();
            foreach ((EntityRecord line, bool done) in lines)
                rows.Children.Add(Row(line, done));
            selection.EndRender();
            Children.Add(rows);

            // The add box, with its hint inside it while it is empty.
            input = new TextBox { Height = 32, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(input, list.AddLabel);
            TextBlock hint = new TextBlock { Text = list.AddLabel, Foreground = ItemLook.Res("TextSub"), Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            input.TextChanged += (s, e) => hint.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            input.KeyDown += Input_KeyDown;
            Grid add = new Grid { Margin = new Thickness(ItemLook.Gap, lines.Count == 0 ? 0 : 8, ItemLook.Gap, 12) };
            add.Children.Add(input);
            add.Children.Add(hint);
            Children.Add(add);

            if (focus is Guid wanted)
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (wanted == Guid.Empty)
                        input.Focus();
                    else if (ticks.TryGetValue(wanted, out CheckBox? box))
                        TaskOrder.KeepView(this, () => box.Focus()); // the line moved to its group; the view stays put
                }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private Border Row(EntityRecord line, bool done)
        {
            Grid row = new Grid { MinHeight = 32, Background = Brushes.Transparent };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // The selected look (ItemSelection) on the frame; taken first, so a Ctrl or Shift click neither ticks nor edits.
            Border frame = new Border { Child = row, CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Margin = new Thickness(ItemLook.Gap - 8, 0, ItemLook.Gap - 8, 0), Padding = new Thickness(7, 0, 7, 0) };
            SelectionLook.PaintRow(frame, false, Brushes.Transparent, Brushes.Transparent);
            selection.Attach(frame, line.Id);

            CheckBox check = new CheckBox { IsChecked = done, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 10, 4) };
            AutomationProperties.SetName(check, list.Title(line));
            check.Click += (s, e) =>
            {
                bool ticked = check.IsChecked == true;
                keep(check.IsKeyboardFocused ? line.Id : null);
                if (!change(stored => stored.First(r => r.Id == line.Id).Set(CustomType.DoneKey, ticked ? "true" : null)))
                    check.IsChecked = !ticked; // not saved: the tick goes back
            };
            check.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Delete && selection.Count <= 1)
                {
                    Delete(new[] { line.Id });
                    e.Handled = true;
                }
            };
            ticks[line.Id] = check;
            row.Children.Add(check);

            TextBox text = TextEditing.InlineEditor(() => list.Title(line),
                edited => change(stored => stored.First(r => r.Id == line.Id).Set(CustomType.TextKey, edited)), () => input.Focus());
            text.FontSize = 14;
            text.VerticalAlignment = VerticalAlignment.Center;
            text.TextDecorations = done ? TextDecorations.Strikethrough : null;
            text.Foreground = ItemLook.Res(done ? "TextSub" : "TextMain");
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            Button delete = ItemLook.RowAction("", Loc.T("Common.Delete"), () =>
            {
                if (selection.IsSelected(line.Id) && selection.Count > 1)
                    selection.DeleteSelected();
                else
                    Delete(new[] { line.Id });
            }, danger: true);
            delete.Visibility = Visibility.Hidden;
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);
            void Show() => delete.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) => Show();
            row.MouseLeave += (s, e) => Show();
            row.IsKeyboardFocusWithinChanged += (s, e) => Show();

            TextCopy.AttachMenu(frame, () => list.Title(line));
            return frame;
        }

        // Enter adds a line (the box keeps the keyboard for the next one); Esc empties a box with text in it.
        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && input.Text.Length > 0)
            {
                input.Clear();
                e.Handled = true;
                return;
            }
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            string text = input.Text.Trim();
            if (text.Length == 0)
                return;
            keep(Guid.Empty);
            EntityRecord line = new EntityRecord { Id = Guid.NewGuid() };
            line.Set(CustomType.TextKey, text);
            change(stored => stored.Add(line)); // saved: the window draws a new box; not saved: the text stays to try again
        }

        // Several: one question (undoable, like every delete); one line goes without asking, with its toast to undo it.
        private bool DeleteSelected(IReadOnlyList<Guid> ids)
        {
            if (ids.Count > 1)
            {
                Dictionary<Guid, string> names = ticks.Keys.ToDictionary(id => id, id => (string)AutomationProperties.GetName(ticks[id]));
                if (!BulkDelete.Confirm(Window.GetWindow(this), "Bulk.Delete.Lines", ids.Select(id => names.GetValueOrDefault(id, "")).ToList(), Loc.T("Bulk.UndoHint")))
                    return false;
            }
            return Delete(ids);
        }

        private bool Delete(IReadOnlyList<Guid> ids)
        {
            HashSet<Guid> doomed = ids.ToHashSet();
            keep(Guid.Empty);
            return change(stored => stored.RemoveAll(r => doomed.Contains(r.Id)));
        }
    }
}
