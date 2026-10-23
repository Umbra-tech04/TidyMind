using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // Customizing a custom collection's item page (CustomLayout) on the page itself. The Customize switch in the title
    // block turns it on (a new custom collection opens with it on, on an empty page that is never stored). The blocks stay
    // as an item shows them; each gets a quiet toolbar over its heading while the pointer or the keyboard is on it (up,
    // down, half or full width, rename, a log's amounts, remove), and its name is edited where it stands. A Fields block
    // lists its fields instead of their values: a field's name, its kind in a dropdown (a number's unit, a choice's
    // choices), whether cards show it, and a box at the end that adds one (Enter; it is text until its kind is changed).
    // "+ Add" at the end adds a block, full width. Every change is saved at once for the whole collection, as one undo
    // step; one that takes away a block or field some item has something in asks first, and its data goes from those
    // items in the same step (Ctrl+Z brings both back). Switched off, the page is the item as always.
    public partial class ItemWindow
    {
        // A layout change for the collection view to save: the new layout, the blocks and fields whose data the items
        // lose with it (by the key it is stored under), the undo step's label and whether the toast offers to undo it.
        public sealed record LayoutChange(Guid Collection, CustomLayout Layout, IReadOnlyCollection<string> Removed, string Label, bool Destructive, Guid Target)
        {
            // Fields whose kind changed, by Id, with the new kind: their values are stored in its form (LayoutField.Fit).
            public IReadOnlyDictionary<string, string> Converted { get; init; } = new Dictionary<string, string>();
        }

        private bool customizing;
        private CheckBox? customizeSwitch;

        // What gets the keyboard once the page is drawn again after a change, by block and field: a field's name, a Fields
        // block's add box (AddTarget), or a block only brought into view (ViewTarget). Filled while the page is drawn.
        private const string AddTarget = "+", ViewTarget = "";
        private (string Block, string Field)? layoutFocus;
        private readonly Dictionary<(string Block, string Field), FrameworkElement> layoutTargets = new Dictionary<(string, string), FrameworkElement>();

        // A custom item of the collection showing it (Host.Collection): only then is its layout this collection's to change.
        private bool CanEditLayout => Custom is CustomType custom && custom.MemoryId == host.Collection;

        // Customize on or off (the switch, Esc, the collection's menu). A section being edited is left first (asked if
        // something was typed); if it stays, so does the switch.
        public void Customize(bool on = true)
        {
            if (on != customizing && (!on || CanEditLayout) && LeaveEdit())
                customizing = on;
            Redraw();
        }

        // The switch in the title block's actions.
        private CheckBox CustomizeSwitch()
        {
            CheckBox toggle = new CheckBox
            {
                Content = Loc.T("Custom.Customize"),
                Style = (Style)FindResource("ToggleSwitchStyle"),
                IsChecked = customizing,
                Height = 36,
                Margin = new Thickness(0, 0, host.Preview ? 8 : 16, 8), // alone, in line with the page's right edge
                ToolTip = Loc.T("Custom.Customize.Tip")
            };
            AutomationProperties.SetName(toggle, Loc.T("Custom.Customize"));
            toggle.Click += (s, e) =>
            {
                bool keyboard = toggle.IsKeyboardFocused;
                Customize(toggle.IsChecked == true);
                if (keyboard) // the header was drawn again: the keyboard stays on the switch
                    Dispatcher.BeginInvoke(new Action(() => customizeSwitch?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
            };
            customizeSwitch = toggle;
            return toggle;
        }

        // ---- The page while customizing --------------------------------------------------------------------

        // A block as the item shows it, its name made editable in place and its toolbar in the space over its heading,
        // shown while the pointer or the keyboard is on the block (its room is always kept, so nothing moves).
        private UIElement Shell(CustomType custom, CustomBlock block, UIElement element)
        {
            Grid shell = new Grid { Background = Brushes.Transparent };
            AutomationProperties.SetName(shell, block.Name);
            TextBox? name = null;
            if (ItemLook.HeadingOf(element) is (Grid heading, TextBlock title))
            {
                name = NameEditor(() => block.Name, title, edited => SaveLayout(layout => layout.Block(block.Id)!.Name = edited, Loc.T("Custom.Undo.Layout")));
                AutomationProperties.SetName(name, Loc.F("Custom.Layout.NameOf", Loc.T("Custom.Kind." + block.Kind)));
                int at = heading.Children.IndexOf(title);
                heading.Children.RemoveAt(at);
                heading.Children.Insert(at, name);
            }
            shell.Children.Add(new Border { Padding = new Thickness(0, ToolbarRoom, 0, 0), Child = element });

            List<CustomBlock> blocks = custom.Blocks.ToList();
            int index = blocks.IndexOf(block);
            StackPanel bar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, ItemLook.Gap - 6, 0),
                Visibility = Visibility.Hidden
            };
            bar.Children.Add(ToolButton("", Loc.T("Custom.Layout.Up"), index > 0, () => MoveBlock(block.Id, -1)));
            bar.Children.Add(ToolButton("", Loc.T("Custom.Layout.Down"), index < blocks.Count - 1, () => MoveBlock(block.Id, +1)));
            bar.Children.Add(ToolButton(block.Half ? "" : "", Loc.T(block.Half ? "Custom.Layout.ToFull" : "Custom.Layout.ToHalf"), true,
                () => SaveLayout(layout => layout.Block(block.Id)!.Half = !block.Half, Loc.T("Custom.Undo.Layout"))));
            if (name != null)
                bar.Children.Add(ToolButton("", Loc.T("Custom.Layout.Rename"), true, () =>
                {
                    name.Focus();
                    name.SelectAll();
                }));
            if (block.Kind == BlockKinds.Log)
            {
                Button amounts = null!;
                amounts = ToolButton("", Loc.T("Custom.Layout.Amounts"), true, () => AmountsMenu(amounts, block));
                bar.Children.Add(amounts);
            }
            bar.Children.Add(ToolButton("", Loc.T("Custom.Layout.RemoveBlock"), true, () => RemoveBlock(block.Id), danger: true));
            shell.Children.Add(bar);

            void Show() => bar.Visibility = shell.IsMouseOver || shell.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            shell.MouseEnter += (s, e) => Show();
            shell.MouseLeave += (s, e) => Show();
            shell.IsKeyboardFocusWithinChanged += (s, e) => Show();
            layoutTargets[(block.Id, ViewTarget)] = shell;
            return shell;
        }

        // The space a block gains on top while customizing, so its toolbar fits over its heading.
        private const double ToolbarRoom = 8;

        private static Button ToolButton(string glyph, string name, bool enabled, Action click, bool danger = false)
        {
            Button button = ItemLook.RowAction(glyph, name, click, danger);
            button.Width = 26;
            button.Height = 26;
            button.FontSize = 12;
            button.Margin = new Thickness(2, 0, 0, 0);
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : 0.35; // the button style has no look of its own for a disabled one
            return button;
        }

        // A name edited where it is shown, in the look of the text it stands for: Enter or clicking away saves, Esc puts
        // the stored one back, an empty one is never saved.
        private TextBox NameEditor(Func<string> stored, TextBlock look, Func<string, bool> save)
        {
            TextBox name = TextEditing.InlineEditor(stored, save, () => PageScroll.Focus());
            name.FontSize = look.FontSize;
            name.FontWeight = look.FontWeight;
            name.Foreground = look.Foreground;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.ToolTip = Loc.T("Custom.Layout.RenameTip");
            Grid.SetColumn(name, Grid.GetColumn(look));
            return name;
        }

        // A Fields block while customizing: a row per field (unknown kinds are kept and not shown), then the add box.
        private UIElement FieldsDesigner(CustomType custom, CustomBlock block)
        {
            StackPanel section = new StackPanel();
            section.Children.Add(ItemLook.Heading(block.Name, out _));
            StackPanel list = new StackPanel { Margin = ItemLook.FieldsMargin };
            Grid.SetIsSharedSizeScope(list, true); // the names in one column, the kinds lined up after them
            List<LayoutField> known = (block.Layout.Fields ?? new List<LayoutField>()).Where(f => FieldKinds.ValueKindOf(f.Kind) != null).ToList();
            List<string> card = custom.Layout.CardFields ?? new List<string>();
            foreach (LayoutField field in known)
                list.Children.Add(FieldRow(block, field, known, card));
            list.Children.Add(AddFieldBox(block));
            section.Children.Add(list);
            return section;
        }

        // Its name (edited in place), its kind, a number's unit or a choice's choices, On card; up, down and remove at its
        // end while the pointer or the keyboard is on it. Narrow, the kind and the rest go under the name.
        private UIElement FieldRow(CustomBlock block, LayoutField field, List<LayoutField> known, List<string> card)
        {
            Grid row = new Grid { Background = Brushes.Transparent, MinHeight = 32, Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "Name" });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            TextBlock look = new TextBlock { FontSize = 14, Foreground = ItemLook.Res("TextMain") };
            TextBox name = NameEditor(() => field.Name, look, edited => SaveLayout(layout => FieldOf(layout, block.Id, field.Id).Name = edited, Loc.T("Custom.Undo.Layout")));
            name.Margin = new Thickness(0, 0, 12, 0);
            name.MinWidth = 140;
            name.MaxWidth = 280;
            AutomationProperties.SetName(name, Loc.T("Custom.Field.Name"));
            row.Children.Add(name);
            layoutTargets[(block.Id, field.Id)] = name;

            WrapPanel settings = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            ComboBox kind = new ComboBox { Height = 28, MinWidth = 112, FontSize = 13, Margin = new Thickness(0, 2, 12, 2), VerticalContentAlignment = VerticalAlignment.Center };
            foreach (string k in FieldKinds.All)
                kind.Items.Add(new ComboBoxItem { Content = Loc.T("Custom.FieldKind." + k), Tag = k });
            kind.SelectedItem = kind.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == field.Kind);
            AutomationProperties.SetName(kind, Loc.F("Custom.Field.KindOf", field.Name));
            kind.SelectionChanged += (s, e) =>
            {
                string chosen = (string)((ComboBoxItem)kind.SelectedItem).Tag;
                if (chosen != field.Kind) // once the dropdown has closed: the page is drawn again from what was saved
                    Dispatcher.BeginInvoke(new Action(() => ChangeKind(block.Id, field, chosen)), System.Windows.Threading.DispatcherPriority.Input);
            };
            settings.Children.Add(kind);
            if (field.Kind == FieldKinds.Number)
            {
                UIElement unit = HintBox(field.Unit ?? "", Loc.T("Custom.Field.Unit"), 88, out TextBox box, height: 28);
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        e.Handled = true;
                        PageScroll.Focus(); // leaving the box saves (below)
                    }
                };
                box.LostKeyboardFocus += (s, e) =>
                {
                    string typed = box.Text.Trim();
                    if (typed != (field.Unit ?? "").Trim())
                        SaveLayout(layout => FieldOf(layout, block.Id, field.Id).Unit = typed.Length == 0 ? null : typed, Loc.T("Custom.Undo.Layout"));
                };
                ((FrameworkElement)unit).Margin = new Thickness(0, 2, 12, 2);
                settings.Children.Add(unit);
            }
            if (field.Kind == FieldKinds.Choice)
            {
                Button choices = ItemLook.Link(Loc.T("Custom.Field.EditChoices"), () => EditChoices(block.Id, field));
                choices.Margin = new Thickness(0, 2, 12, 2);
                choices.FontSize = 13;
                choices.ToolTip = string.Join(", ", field.Choices ?? new List<string>());
                settings.Children.Add(choices);
            }
            bool onCard = card.Contains(field.Id);
            CheckBox cardBox = new CheckBox
            {
                Content = Loc.T("Custom.Layout.OnCard"),
                FontSize = 12,
                Foreground = ItemLook.Res("TextSub"),
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 2),
                IsChecked = onCard,
                IsEnabled = onCard || card.Count < CustomLayout.MaxCardFields,
                ToolTip = Loc.T(onCard || card.Count < CustomLayout.MaxCardFields ? "Custom.Field.ShowOnCard" : "Custom.Field.CardFull")
            };
            ToolTipService.SetShowOnDisabled(cardBox, true);
            AutomationProperties.SetName(cardBox, Loc.F("Custom.Field.OnCardOf", field.Name));
            cardBox.Click += (s, e) => SaveLayout(layout =>
            {
                layout.CardFields ??= new List<string>();
                layout.CardFields.Remove(field.Id);
                if (cardBox.IsChecked == true)
                    layout.CardFields.Add(field.Id);
            }, Loc.T("Custom.Undo.Layout"));
            settings.Children.Add(cardBox);
            Grid.SetColumn(settings, 1);
            row.Children.Add(settings);

            int at = known.IndexOf(field);
            StackPanel icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden,
                Margin = new Thickness(8, 0, -6, 0) };
            icons.Children.Add(ToolButton("", Loc.T("Custom.Layout.Up"), at > 0, () => MoveField(block.Id, field.Id, -1)));
            icons.Children.Add(ToolButton("", Loc.T("Custom.Layout.Down"), at < known.Count - 1, () => MoveField(block.Id, field.Id, +1)));
            icons.Children.Add(ToolButton("", Loc.T("Custom.Layout.RemoveField"), true, () => RemoveField(block.Id, field.Id), danger: true));
            Grid.SetColumn(icons, 2);
            row.Children.Add(icons);
            void Show() => icons.Visibility = row.IsMouseOver || row.IsKeyboardFocusWithin ? Visibility.Visible : Visibility.Hidden;
            row.MouseEnter += (s, e) => Show();
            row.MouseLeave += (s, e) => Show();
            row.IsKeyboardFocusWithinChanged += (s, e) => Show();

            // Wide: one line. Narrow: the settings on a line of their own under the name.
            row.SizeChanged += (s, e) =>
            {
                bool narrow = e.NewSize.Width < 560;
                name.MaxWidth = narrow ? Math.Max(80, e.NewSize.Width - 110) : 280; // room for the icons beside it
                name.MinWidth = Math.Min(140, name.MaxWidth);
                Grid.SetRow(settings, narrow ? 1 : 0);
                Grid.SetColumn(settings, narrow ? 0 : 1);
                Grid.SetColumnSpan(settings, narrow ? 3 : 1);
            };
            return row;
        }

        // The box at the end of a Fields block: a name and Enter add a text field; the keyboard stays there for the next.
        private UIElement AddFieldBox(CustomBlock block)
        {
            UIElement add = HintBox("", Loc.T("Custom.Layout.AddField"), 260, out TextBox input);
            ((FrameworkElement)add).HorizontalAlignment = HorizontalAlignment.Left;
            input.KeyDown += (s, e) =>
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
                string name = input.Text.Trim();
                if (name.Length == 0)
                    return;
                layoutFocus = (block.Id, AddTarget);
                if (!SaveLayout(layout => (layout.Block(block.Id)!.Fields ??= new List<LayoutField>())
                        .Add(new LayoutField { Id = CustomLayout.NewId(), Name = name, Kind = FieldKinds.Text }), Loc.F("Undo.Added", name)))
                    layoutFocus = null; // not saved: the text stays to try again
            };
            layoutTargets[(block.Id, AddTarget)] = input;
            return add;
        }

        // A box with its hint inside it while it is empty.
        private static UIElement HintBox(string text, string hint, double width, out TextBox box, double height = 32)
        {
            TextBox input = new TextBox { Text = text, Height = height, Width = width, VerticalContentAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(input, hint);
            TextBlock shown = new TextBlock { Text = hint, Foreground = ItemLook.Res("TextSub"), Margin = new Thickness(11, 0, 0, 0), FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = text.Length == 0 ? Visibility.Visible : Visibility.Collapsed };
            input.TextChanged += (s, e) => shown.Visibility = input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Grid grid = new Grid();
            grid.Children.Add(input);
            grid.Children.Add(shown);
            box = input;
            return grid;
        }

        // The last row while customizing: "+ Add" with its menu of blocks, and what changes do. An empty page says what
        // it is for first.
        private UIElement AddRow(CustomType custom)
        {
            StackPanel row = new StackPanel { Margin = new Thickness(ItemLook.Gap, 20, ItemLook.Gap, 20) };
            if (custom.Blocks.Count == 0)
            {
                TextBlock start = ItemLook.Muted(Loc.T("Custom.Layout.Start"), 14);
                start.Margin = new Thickness(0, 4, 0, 16);
                row.Children.Add(start);
            }
            DockPanel line = new DockPanel();
            Button add = new Button
            {
                Content = "+  " + Loc.T("Custom.Layout.Add"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Height = 32,
                Padding = new Thickness(14, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(add, Loc.T("Custom.Layout.Add"));
            add.Click += (s, e) => AddMenu(add, custom).IsOpen = true;
            DockPanel.SetDock(add, Dock.Left);
            line.Children.Add(add);
            TextBlock hint = ItemLook.Muted(Loc.T("Custom.Layout.Hint"), 12);
            hint.VerticalAlignment = VerticalAlignment.Center;
            hint.Margin = new Thickness(16, 0, 0, 0);
            line.Children.Add(hint);
            row.Children.Add(line);
            return row;
        }

        // Every kind of block by its plain name, with a line on what it holds; one gallery at most (an item has one set of
        // pictures).
        private ContextMenu AddMenu(Button button, CustomType custom)
        {
            ContextMenu menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (string kind in BlockKinds.All)
            {
                StackPanel header = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                header.Children.Add(new TextBlock { Text = Loc.T("Custom.Kind." + kind), FontSize = 13, Foreground = ItemLook.Res("TextMain") });
                header.Children.Add(new TextBlock { Text = Loc.T("Custom.Kind." + kind + ".Text"), FontSize = 12, Foreground = ItemLook.Res("TextSub") });
                MenuItem item = new MenuItem { Header = header, IsEnabled = kind != BlockKinds.Gallery || !custom.Layout.HasGallery };
                AutomationProperties.SetName(item, Loc.T("Custom.Kind." + kind));
                item.Click += (s, e) => AddBlock(kind);
                menu.Items.Add(item);
            }
            return menu;
        }

        // A log's amounts: money (with the currency, totals per year), plain numbers, or numbers in a unit of the user's.
        private void AmountsMenu(Button button, CustomBlock block)
        {
            LayoutBlock log = block.Layout;
            string? unit = string.IsNullOrWhiteSpace(log.Unit) ? null : log.Unit.Trim();
            ContextMenu menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            button.ContextMenu = menu; // a right-click opens it too
            void Option(string header, bool chosen, Action pick)
            {
                MenuItem option = new MenuItem { Header = header, IsChecked = chosen };
                option.Click += (s, e) => pick();
                menu.Items.Add(option);
            }
            Option(Loc.T("Custom.Layout.LogMoney"), log.IsMoney, () => SetAmounts(block.Id, null, null));
            Option(Loc.T("Custom.Layout.LogPlain"), !log.IsMoney && unit == null, () => SetAmounts(block.Id, false, null));
            Option(!log.IsMoney && unit != null ? Loc.F("Custom.Layout.LogUnit", unit) : Loc.T("Custom.Layout.LogUnitPick"), !log.IsMoney && unit != null, () =>
            {
                if (InputDialog.Prompt(this, block.Name, Loc.T("Custom.Block.Unit"), unit ?? "", Loc.T("Common.Save")) is string typed)
                    SetAmounts(block.Id, false, typed);
            });
            menu.IsOpen = true;
        }

        private void SetAmounts(string id, bool? money, string? unit) => SaveLayout(layout =>
        {
            LayoutBlock log = layout.Block(id)!;
            log.Money = money;
            log.Unit = unit;
        }, Loc.T("Custom.Undo.Layout"));

        // The empty page of a new custom collection (Host.Preview) is never stored: what its blocks would hold is shown as
        // an item shows it, and nothing in it takes a click or the keyboard.
        private static void Inert(UIElement element)
        {
            foreach (Control control in Descendants(element).OfType<Control>())
            {
                control.IsHitTestVisible = false;
                control.Focusable = false;
                control.IsTabStop = false;
            }
        }

        // After the page is drawn again: the keyboard back where the change was made (or the new block brought into view).
        private void RestoreLayoutFocus()
        {
            if (layoutFocus is not (string, string) wanted)
                return;
            layoutFocus = null;
            if (!layoutTargets.TryGetValue(wanted, out FrameworkElement? target))
                return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                target.BringIntoView();
                if (wanted.Field != ViewTarget)
                    target.Focus();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        // ---- Changes -------------------------------------------------------------------------------------------

        // The layout as `change` makes it from a copy, saved for the collection (and the data of `removed` taken from its
        // items) as one undo step. The page is drawn again from what was stored. False if it couldn't be saved (said).
        private bool SaveLayout(Action<CustomLayout> change, string label, IReadOnlyCollection<string>? removed = null, bool destructive = false,
            IReadOnlyDictionary<string, string>? converted = null)
        {
            if (Custom is not CustomType custom)
            {
                // Its layout can't be read just now (CustomTypes reads it again next time): nothing is changed.
                if (CustomTypes.MemoryOf(item.EntityType) is Guid memoryId)
                    Msg.Show(this, Loc.F("Custom.Layout.CantRead", MemoryFiles.Layout(memoryId)), Loc.T("Common.CouldntSave"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                Redraw();
                return false;
            }
            CustomLayout layout = custom.Layout.Copy();
            change(layout);
            layout.Clean();
            bool saved = host.ChangeLayout(new LayoutChange(custom.MemoryId, layout, removed ?? Array.Empty<string>(), label, destructive, item.Id)
            {
                Converted = converted ?? new Dictionary<string, string>()
            });
            if (saved && !host.Preview && host.Reread(item.Id) is Entity stored)
                item = stored; // the item as saved (a removed block's data gone from it)
            Redraw();
            return saved;
        }

        private static LayoutField FieldOf(CustomLayout layout, string blockId, string fieldId) => layout.Block(blockId)!.Fields!.First(f => f.Id == fieldId);

        private void MoveBlock(string id, int step) => SaveLayout(layout =>
        {
            // Past its known neighbour: a block of a kind this version doesn't know stays where it is.
            int at = layout.Blocks.FindIndex(b => b.Id == id);
            int to = at + step;
            while (to >= 0 && to < layout.Blocks.Count && !BlockKinds.Known(layout.Blocks[to].Kind))
                to += step;
            if (to >= 0 && to < layout.Blocks.Count)
                (layout.Blocks[at], layout.Blocks[to]) = (layout.Blocks[to], layout.Blocks[at]);
        }, Loc.T("Custom.Undo.Layout"));

        // At the end, full width, named by its kind; a Fields block has the keyboard in its add box for the first field.
        private void AddBlock(string kind)
        {
            string id = CustomLayout.NewId();
            layoutFocus = (id, kind == BlockKinds.Fields ? AddTarget : ViewTarget);
            if (!SaveLayout(layout => layout.Blocks.Add(new LayoutBlock
                {
                    Id = id,
                    Kind = kind,
                    Name = Loc.T("Custom.Block." + kind),
                    Fields = kind == BlockKinds.Fields ? new List<LayoutField>() : null
                }), Loc.F("Undo.Added", Loc.T("Custom.Block." + kind))))
                layoutFocus = null;
        }

        // What an item holds of a block, by the keys it is stored under: a Fields block's fields, a Notes block's text, a
        // list's records. A gallery is the item's own pictures, which stay (the pictures on top show them again).
        private static List<string> DataKeys(LayoutBlock block) => block.Kind switch
        {
            BlockKinds.Fields => (block.Fields ?? new List<LayoutField>()).Select(f => f.Id).ToList(),
            BlockKinds.Gallery => new List<string>(),
            _ => new List<string> { block.Id }
        };

        // Asked first when an item has something in it; that goes from every item with it, in the same step.
        private void RemoveBlock(string id)
        {
            LayoutBlock block = Custom!.Layout.Block(id)!;
            List<string> keys = DataKeys(block);
            if (!ConfirmRemove(block.Name, keys))
                return;
            SaveLayout(layout =>
            {
                layout.Blocks.RemoveAll(b => b.Id == id);
                layout.CardFields?.RemoveAll(keys.Contains);
            }, Loc.F("Undo.Deleted", block.Name), keys, destructive: true);
        }

        private void RemoveField(string blockId, string fieldId)
        {
            LayoutField field = FieldOf(Custom!.Layout, blockId, fieldId);
            if (!ConfirmRemove(field.Name, new[] { fieldId }))
                return;
            SaveLayout(layout =>
            {
                layout.Block(blockId)!.Fields!.RemoveAll(f => f.Id == fieldId);
                layout.CardFields?.Remove(fieldId);
            }, Loc.F("Undo.Deleted", field.Name), new[] { fieldId }, destructive: true);
        }

        // True to go on: nothing in any item, or the user said yes to it going.
        private bool ConfirmRemove(string name, IReadOnlyCollection<string> keys)
        {
            int holding = keys.Count == 0 ? 0 : host.ItemsHolding(keys);
            return holding == 0 || Msg.Show(this, Loc.Plural("Custom.Layout.RemoveData", holding, name), Loc.T("Common.ConfirmDelete"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        private void MoveField(string blockId, string fieldId, int step) => SaveLayout(layout =>
        {
            List<LayoutField> fields = layout.Block(blockId)!.Fields!;
            int at = fields.FindIndex(f => f.Id == fieldId);
            int to = at + step;
            while (to >= 0 && to < fields.Count && FieldKinds.ValueKindOf(fields[to].Kind) == null)
                to += step;
            if (to >= 0 && to < fields.Count)
                (fields[at], fields[to]) = (fields[to], fields[at]);
        }, Loc.T("Custom.Undo.Layout"));

        // A field's new kind. Each value items hold in it is kept in the new kind's form (LayoutField.Fit); one that can't
        // be read as that kind (a text that is no date) goes, which is asked first. A choice asks for its choices; no
        // answer, no change.
        private void ChangeKind(string blockId, LayoutField field, string kind)
        {
            List<string>? choices = null;
            if (kind == FieldKinds.Choice && (choices = AskChoices(field)) == null)
            {
                Redraw(); // the dropdown back to the kind it has
                return;
            }
            int unfit = host.Unfit(field.Id, kind);
            if (unfit > 0 && Msg.Show(this, Loc.Plural("Custom.Layout.KindUnfit", unfit, field.Name, Loc.T("Custom.FieldKind." + kind)),
                    Loc.T("Custom.Field.ChangeKind"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                Redraw();
                return;
            }
            SaveLayout(layout =>
            {
                LayoutField target = FieldOf(layout, blockId, field.Id);
                target.Kind = kind;
                target.Choices = kind == FieldKinds.Choice ? choices : null;
                target.Unit = kind == FieldKinds.Number ? target.Unit : null;
            }, Loc.T("Custom.Undo.Layout"), destructive: unfit > 0, converted: new Dictionary<string, string> { [field.Id] = kind });
        }

        private void EditChoices(string blockId, LayoutField field)
        {
            if (AskChoices(field) is List<string> choices)
                SaveLayout(layout => FieldOf(layout, blockId, field.Id).Choices = choices, Loc.T("Custom.Undo.Layout"));
        }

        // A choice field's choices, one per line, each once; null if the question was closed.
        private List<string>? AskChoices(LayoutField field) =>
            InputDialog.Prompt(this, field.Name, Loc.T("Custom.Field.Choices"), string.Join("\n", field.Choices ?? new List<string>()), Loc.T("Common.Save"), multiline: true)
                is string typed ? typed.Split('\n').Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList() : null;
    }
}
