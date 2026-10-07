using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TidyMind
{
    // The tab pills above a memory's card grid: selection, inline rename, add, delete, persistence.
    internal class MemoryTabStrip
    {
        private readonly StackPanel strip;
        private readonly Profile memory;
        private readonly string itemWord;
        private readonly Func<Guid, int> countItemsInTab;
        private readonly Action<Guid, Guid> moveItems;

        private readonly List<MemoryTab> tabs;
        private Guid renamingTabId;
        private Action commitPendingRename;
        private Guid newTabId;
        private Guid tabBeforeNew;
        private readonly DragReorder<MemoryTab> tabDrag;

        public Guid ActiveTabId { get; private set; }

        // Raised when the visible set of items may have changed (tab switched, added or deleted).
        public event Action TabsChanged;

        // tabs: the memory's tabs as read by TabStore.TryLoadOrCreate (at least one, sorted).
        public MemoryTabStrip(FrameworkElement view, StackPanel strip, Profile memory,
            List<MemoryTab> tabs, string itemWord, Func<Guid, int> countItemsInTab, Action<Guid, Guid> moveItems)
        {
            this.strip = strip;
            this.memory = memory;
            this.itemWord = itemWord;
            this.countItemsInTab = countItemsInTab;
            this.moveItems = moveItems;

            this.tabs = tabs;
            ActiveTabId = tabs[0].Id;

            // Pills sit 6px apart (their right margin).
            tabDrag = new DragReorder<MemoryTab>(strip, Orientation.Horizontal, 6, null, MoveTab);

            // Clicks outside the strip finish an inline rename; clicks inside it are handled by the pills / "+".
            view.PreviewMouseDown += (s, e) =>
            {
                if (commitPendingRename != null && !(e.OriginalSource is DependencyObject d && IsInsideStrip(d)))
                    commitPendingRename();
            };
        }

        public Guid FirstTabId => tabs.OrderBy(t => t.Order).First().Id;

        public bool Contains(Guid tabId) => tabs.Any(t => t.Id == tabId);

        public void Select(Guid tabId)
        {
            MemoryTab tab = tabs.FirstOrDefault(t => t.Id == tabId);
            if (tab != null)
                SelectTab(tab);
        }

        public void Render()
        {
            strip.Children.Clear();
            tabDrag.Clear();

            foreach (MemoryTab tab in tabs.OrderBy(t => t.Order))
                strip.Children.Add(tab.Id == renamingTabId ? CreateRenameBox(tab) : CreatePill(tab));

            Button add = new Button
            {
                Content = "+",
                Width = 30,
                Height = 30,
                FontSize = 15,
                ToolTip = "Add tab",
                Margin = new Thickness(2, 0, 0, 0),
                Style = (Style)Application.Current.FindResource("IconButtonStyle")
            };
            add.Click += (s, e) => AddTab();
            strip.Children.Add(add);
        }

        private Border CreatePill(MemoryTab tab)
        {
            bool active = tab.Id == ActiveTabId;

            Border pill = new Border
            {
                CornerRadius = new CornerRadius(15),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 6, 0),
                Cursor = Cursors.Hand,
                Background = active ? CardEffects.Accent : Brushes.Transparent,
                Child = new TextBlock
                {
                    Text = tab.Name,
                    FontSize = 13,
                    FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = active ? Brushes.White : B("#5F5F5B")
                }
            };

            if (!active)
            {
                pill.MouseEnter += (s, e) => pill.Background = B("#ECECE9");
                pill.MouseLeave += (s, e) => pill.Background = Brushes.Transparent;
            }

            // Select on release (a real click), not on press: a press may become a drag, and selecting
            // re-renders the strip, which would pull the pill out from under the drag.
            CardEffects.AttachClick(pill, () => SelectTab(tab));
            pill.MouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 2)
                    BeginRename(tab);
                e.Handled = true; // as before: the surrounding ScrollViewer mustn't grab focus
            };
            tabDrag.Attach(pill, tab);

            ContextMenu menu = new ContextMenu();
            MenuItem rename = new MenuItem { Header = "Rename" };
            rename.Click += (s, e) => BeginRename(tab);
            MenuItem delete = new MenuItem { Header = "Delete", IsEnabled = tabs.Count > 1 };
            delete.Click += (s, e) => DeleteTab(tab);
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            pill.ContextMenu = menu;

            return pill;
        }

        private TextBox CreateRenameBox(MemoryTab tab)
        {
            TextBox box = new TextBox
            {
                Text = tab.Name,
                MinWidth = 120,
                Height = 30,
                FontSize = 13,
                Margin = new Thickness(0, 0, 6, 0),
                Style = (Style)Application.Current.FindResource("InlineEditBoxStyle")
            };

            bool finished = false;
            void Finish(bool keep)
            {
                if (finished) return;
                finished = true;
                commitPendingRename = null;
                renamingTabId = Guid.Empty;

                bool isNew = tab.Id == newTabId;
                newTabId = Guid.Empty;
                if (isNew && !keep)
                {
                    DiscardNewTab(tab);
                    return;
                }

                string name = box.Text.Trim();
                bool renamed = keep && name.Length > 0 && name != tab.Name;
                if (renamed)
                    tab.Name = name;
                if (renamed || isNew)
                    Save();

                Render();
            }

            commitPendingRename = () => Finish(true);
            box.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Enter) { Finish(true); e.Handled = true; }
                else if (e.Key == Key.Escape) { Finish(false); e.Handled = true; }
            };
            box.LostKeyboardFocus += (s, e) => Finish(true);

            // Deferred so a closing context menu can't restore focus elsewhere and end the rename at once.
            box.Loaded += (s, e) => box.Dispatcher.BeginInvoke(new Action(() =>
            {
                box.Focus();
                box.SelectAll();
            }), DispatcherPriority.Input);

            return box;
        }

        private bool IsInsideStrip(DependencyObject element)
        {
            for (DependencyObject d = element; d != null; d = VisualTreeHelper.GetParent(d))
                if (d == strip)
                    return true;
            return false;
        }

        private void BeginRename(MemoryTab tab)
        {
            commitPendingRename?.Invoke();
            renamingTabId = tab.Id;
            Render();
        }

        private void SelectTab(MemoryTab tab)
        {
            commitPendingRename?.Invoke();
            if (tab.Id == ActiveTabId) return;

            ActiveTabId = tab.Id;
            Render();
            TabsChanged?.Invoke();
        }

        private void AddTab()
        {
            commitPendingRename?.Invoke();

            string name = "New Tab";
            for (int n = 2; tabs.Any(t => t.Name == name); n++)
                name = "New Tab " + n;

            MemoryTab tab = new MemoryTab
            {
                Id = Guid.NewGuid(),
                Name = name,
                Order = tabs.Max(t => t.Order) + 1
            };
            tabs.Add(tab);

            // Not saved yet: it opens straight into rename, where Enter or a click elsewhere keeps it
            // and Esc discards it as if "+" was never pressed.
            newTabId = tab.Id;
            tabBeforeNew = ActiveTabId;
            ActiveTabId = tab.Id;
            renamingTabId = tab.Id;
            Render();
            TabsChanged?.Invoke();
        }

        private void DiscardNewTab(MemoryTab tab)
        {
            tabs.Remove(tab);
            ActiveTabId = tabBeforeNew;
            Render();
            TabsChanged?.Invoke();
        }

        // Drag-and-drop on the pills: only the tab order changes, not what's in them.
        private void MoveTab(MemoryTab dragged, MemoryTab target, bool after)
        {
            commitPendingRename?.Invoke();

            List<MemoryTab> ordered = tabs.OrderBy(t => t.Order).ToList();
            DragReorder<MemoryTab>.Move(ordered, dragged, target, after);
            for (int i = 0; i < ordered.Count; i++)
                ordered[i].Order = i;

            Save();
            Render();
        }

        private void DeleteTab(MemoryTab tab)
        {
            commitPendingRename?.Invoke();
            if (tabs.Count <= 1) return;

            MemoryTab target = tabs.Where(t => t.Id != tab.Id).OrderBy(t => t.Order).First();
            int count = countItemsInTab(tab.Id);

            if (count > 0)
            {
                MessageBoxResult result = MessageBox.Show(
                    "Delete the tab '" + tab.Name + "'?\n\nIts " + count + " " + itemWord + (count == 1 ? "" : "s") +
                    " will be moved to '" + target.Name + "'.",
                    "Delete Tab", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;

                moveItems(tab.Id, target.Id);
            }

            tabs.Remove(tab);
            Save();

            if (ActiveTabId == tab.Id)
                ActiveTabId = target.Id;

            Render();
            TabsChanged?.Invoke();
        }

        private void Save()
        {
            if (!TabStore.TrySave(memory, tabs))
                MessageBox.Show("Couldn't save the tabs of '" + memory.Name + "': this file can't be written:\n"
                    + MemoryFiles.Tabs(memory) + "\n\nYour last change to the tabs wasn't saved.", "Couldn't Save",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
