using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // Windows-style multi-selection on a list the app draws itself (sidebar memories, card grids, to-dos, Day Plan
    // blocks, files, reminders). Ctrl+click toggles one item; Shift+click selects the run from the last clicked item
    // to this one (Ctrl+Shift adds the run); Ctrl+A selects everything shown; Esc or a click on blank space clears;
    // Delete hands the selection to the list's own delete. A plain click keeps doing what it always did (opens the
    // item) and starts the next Shift run. Right-click on a selected item while several are selected opens the bulk
    // menu instead of the item's own. The list keeps its items in `T` (an object, or an Id where redraws re-read them).
    public class ItemSelection<T> where T : notnull
    {
        private readonly Func<IReadOnlyList<T>, bool> deleteSelected;  // asks, deletes; true if it did
        private readonly Action<FrameworkElement, bool>? paint;

        // What's on screen, in order: the Shift run and Ctrl+A follow it.
        private readonly List<KeyValuePair<FrameworkElement, T>> shown = new List<KeyValuePair<FrameworkElement, T>>();
        private readonly HashSet<T> selected = new HashSet<T>();
        private T? anchor;
        private bool hasAnchor;

        // scope: the list's area, where Esc, Delete and Ctrl+A work while the keyboard is in it. paint: shows or
        // hides an element's selected look (null: the subclass overrides Paint).
        public ItemSelection(UIElement scope, Func<IReadOnlyList<T>, bool> deleteSelected, Action<FrameworkElement, bool>? paint)
        {
            this.deleteSelected = deleteSelected;
            this.paint = paint;
            FocusTarget = scope;
            scope.PreviewKeyDown += Scope_PreviewKeyDown;
        }

        // Takes the keyboard after a selecting click on an item that can't (inside `scope`, and focusable).
        public UIElement FocusTarget { get; set; }

        public event Action? SelectionChanged;

        // In the order shown.
        public IReadOnlyList<T> Selected => shown.Where(e => selected.Contains(e.Value)).Select(e => e.Value).Distinct().ToList();
        public int Count => selected.Count;
        public bool IsSelected(T item) => selected.Contains(item);

        // ---- Items (registered on every render) -----------------------------

        public virtual void BeginRender() => shown.Clear();

        // What is on screen now, in order.
        protected IReadOnlyList<KeyValuePair<FrameworkElement, T>> Shown => shown;

        protected virtual void Paint(FrameworkElement element, bool on) => paint?.Invoke(element, on);

        // Call while building each item, BEFORE its own click, drag and menu handlers are attached, so a Ctrl or
        // Shift click is taken here first and neither opens, ticks nor drags it. One item may have several elements
        // (the Day Plan's timeline block and list row).
        public void Attach(FrameworkElement element, T item)
        {
            shown.Add(new KeyValuePair<FrameworkElement, T>(element, item));
            element.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (ItemSelection.OnRowButton(e.OriginalSource as DependencyObject, element))
                    return; // the row's own ×, ✎ or + keeps working
                if (InEditedText(e.OriginalSource as DependencyObject, element))
                    return; // Shift+click in text being typed extends the text selection
                if (Click(item, Keyboard.Modifiers))
                {
                    e.Handled = true;
                    // So Esc, Delete and Ctrl+A reach the list: the item itself if it takes the keyboard.
                    if (!element.Focus())
                        FocusTarget.Focus();
                }
            };

            ContextMenu? own = null;
            element.ContextMenuOpening += (s, e) =>
            {
                if (!(element.ContextMenu?.Tag is BulkMenuTag))
                    own = element.ContextMenu;
                element.ContextMenu = selected.Contains(item) && selected.Count > 1 ? BulkMenu() : own;
            };

            if (selected.Contains(item))
                Paint(element, true);
        }

        // Call after the render: only items on screen stay selected (a bulk Delete never touches hidden ones).
        public void EndRender()
        {
            HashSet<T> onScreen = shown.Select(e => e.Value).ToHashSet();
            if (hasAnchor && !onScreen.Contains(anchor!))
                hasAnchor = false;
            if (selected.RemoveWhere(item => !onScreen.Contains(item)) > 0)
                SelectionChanged?.Invoke();
        }

        // A plain click (or one on blank space) anywhere in `area` that isn't on an item clears the selection.
        public void ClearOnBlankClick(UIElement area)
        {
            area.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (selected.Count > 0 && Keyboard.Modifiers == ModifierKeys.None && !OnItem(e.OriginalSource as DependencyObject, area))
                    ClearSelection();
            };
        }

        // ---- Selection ----------------------------------------------------

        // The selecting part of a click on `item`; true if it was one (Ctrl and/or Shift held). A plain click only
        // marks where the next Shift run starts, and drops a selection the user has clicked away from.
        public bool Click(T item, ModifierKeys modifiers)
        {
            bool ctrl = (modifiers & ModifierKeys.Control) != 0;
            bool shift = (modifiers & ModifierKeys.Shift) != 0;
            if (shift)
                SelectRange(item, add: ctrl);
            else if (ctrl)
                ToggleSelect(item);
            else
            {
                anchor = item;
                hasAnchor = true;
                ClearSelection();
                return false;
            }
            return true;
        }

        public void ToggleSelect(T item)
        {
            anchor = item;
            hasAnchor = true;
            if (!selected.Remove(item))
                selected.Add(item);
            Repaint();
        }

        // From the last clicked item to this one, in the order shown; with nothing clicked yet, just this one.
        // add: what's selected stays (Ctrl+Shift). The start stays where it was, as in Explorer.
        public void SelectRange(T item, bool add = false)
        {
            List<T> order = shown.Select(e => e.Value).Distinct().ToList();
            int to = order.IndexOf(item);
            int from = hasAnchor ? order.IndexOf(anchor!) : -1;
            if (from < 0)
            {
                anchor = item;
                hasAnchor = true;
                from = to;
            }
            if (!add)
                selected.Clear();
            for (int i = Math.Min(from, to); i <= Math.Max(from, to) && i >= 0; i++)
                selected.Add(order[i]);
            Repaint();
        }

        public void SelectAll()
        {
            selected.UnionWith(shown.Select(e => e.Value));
            Repaint();
        }

        public void ClearSelection()
        {
            if (selected.Count == 0) return;
            selected.Clear();
            Repaint();
        }

        // Replaces the selection wholesale (the card grid's marquee).
        protected void SetSelection(IEnumerable<T> items)
        {
            selected.Clear();
            selected.UnionWith(items);
            Repaint();
        }

        public bool DeleteSelected()
        {
            if (selected.Count == 0 || !deleteSelected(Selected))
                return false;
            selected.Clear();
            SelectionChanged?.Invoke();
            return true;
        }

        private void Repaint()
        {
            foreach (KeyValuePair<FrameworkElement, T> entry in shown)
                Paint(entry.Key, selected.Contains(entry.Value));
            SelectionChanged?.Invoke();
        }

        // ---- Keys ---------------------------------------------------------

        // Not while typing: a text box keeps its own Ctrl+A, Delete and Esc.
        private void Scope_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBoxBase || Keyboard.FocusedElement is PasswordBox)
                return;
            if (e.Key == Key.Escape && selected.Count > 0)
                ClearSelection();
            else if (e.Key == Key.Delete && selected.Count > 0 && Keyboard.Modifiers == ModifierKeys.None && !OnUnselectedItem())
                DeleteSelected();
            else if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control && shown.Count > 0)
                SelectAll();
            else
                return;
            e.Handled = true;
        }

        // The keyboard is in an item that isn't selected (Tab moved it there): Delete is that item's own.
        private bool OnUnselectedItem()
        {
            for (DependencyObject? d = Keyboard.FocusedElement as DependencyObject; d != null; d = Parent(d))
            {
                foreach (KeyValuePair<FrameworkElement, T> entry in shown)
                {
                    if (entry.Key == d)
                        return !selected.Contains(entry.Value);
                }
            }
            return false;
        }

        // ---- Bulk menu ----------------------------------------------------

        private class BulkMenuTag { }

        private ContextMenu BulkMenu()
        {
            MenuItem delete = new MenuItem { Header = Loc.F("Selection.DeleteSelected", selected.Count) };
            delete.Click += (s, e) => DeleteSelected();
            MenuItem clear = new MenuItem { Header = Loc.T("Selection.Clear") };
            clear.Click += (s, e) => ClearSelection();
            return new ContextMenu { Tag = new BulkMenuTag(), Items = { delete, clear } };
        }

        // ---- Hit testing --------------------------------------------------

        protected bool IsItemElement(DependencyObject d) => d is FrameworkElement element && shown.Any(e => e.Key == element);

        private bool OnItem(DependencyObject? source, UIElement stopAt)
        {
            for (DependencyObject? d = source; d != null && d != stopAt; d = Parent(d))
            {
                if (IsItemElement(d) || d is ButtonBase || d is ScrollBar || d is TextBoxBase)
                    return true;
            }
            return false;
        }

        private static bool InEditedText(DependencyObject? source, FrameworkElement row)
        {
            for (DependencyObject? d = source; d != null && d != row; d = Parent(d))
            {
                if (d is TextBoxBase box)
                    return box.IsKeyboardFocusWithin;
            }
            return false;
        }

        protected static DependencyObject? Parent(DependencyObject d) => ItemSelection.Parent(d);
    }

    // Tree walking shared by ItemSelection<T> and the task list (a ListBox with its own selection).
    internal static class ItemSelection
    {
        // On a button inside the row (its own ×, ✎ or +), which keeps doing its own thing.
        internal static bool OnRowButton(DependencyObject? source, DependencyObject row)
        {
            for (DependencyObject? d = source; d != null && d != row; d = Parent(d))
            {
                if (d is Button)
                    return true;
            }
            return false;
        }

        internal static DependencyObject? Parent(DependencyObject d)
        {
            return d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
    }
}
