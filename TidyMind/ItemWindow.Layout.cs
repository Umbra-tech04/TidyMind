using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // The layout editor of a custom collection (CustomLayout), in the item window: "Edit layout" turns the page into the
    // layout's blocks, top to bottom, half blocks side by side as on the item. Each block can be moved, made full or half
    // width, renamed (a log's amounts set) or removed; a Fields block's fields are added, changed (name, kind, choices,
    // unit, shown on cards), moved and removed; blocks are added at the end. Every change is saved at once for the whole
    // collection, as one undo step; one that takes away a block or field some item has something in says so first, and
    // its data goes from those items in the same step (Ctrl+Z brings both back). Done (or Esc) goes back to the item.
    public partial class ItemWindow
    {
        // A layout change for the collection view to save: the new layout, the blocks and fields whose data the items
        // lose with it (by the key it is stored under), the undo step's label and whether the toast offers to undo it.
        public sealed record LayoutChange(CustomLayout Layout, IReadOnlyCollection<string> Removed, string Label, bool Destructive, Guid Target);

        private bool layoutMode;

        // Turns the page into the layout editor (a custom item only).
        public void EditLayout()
        {
            if (Custom == null || !LeaveEdit())
                return;
            layoutMode = true;
            SearchBox.Text = "";
            Redraw();
            PageScroll.ScrollToTop();
        }

        // Back to the item; a window opened on the layout alone closes.
        private void LeaveLayout()
        {
            if (host.Preview)
            {
                Close();
                return;
            }
            layoutMode = false;
            Redraw();
        }

        private static readonly Dictionary<string, string> KindGlyphs = new Dictionary<string, string>
        {
            [BlockKinds.Fields] = "",      // Tag
            [BlockKinds.Gallery] = "",     // Photo2
            [BlockKinds.Notes] = "",       // QuickNote
            [BlockKinds.Checklist] = "",   // CheckboxComposite
            [BlockKinds.Documents] = "",   // Document
            [BlockKinds.Log] = "",         // History
            [BlockKinds.Quantities] = ""   // Calculator
        };

        // ---- The editor's page -----------------------------------------------------------------------------

        private List<UIElement> LayoutBlocks(CustomType custom)
        {
            List<UIElement> rows = new List<UIElement>();
            TextBlock hint = ItemLook.Muted(Loc.T("Custom.Layout.Hint"), 13);
            hint.Margin = new Thickness(ItemLook.Gap, 16, ItemLook.Gap, 16);
            rows.Add(hint);
            rows.AddRange(HalfRow.Rows(custom.Blocks.Select(b => ((UIElement)BlockFrame(custom, b), b.Half)).ToList()));

            Button add = new Button
            {
                Content = "+  " + Loc.T("Custom.Layout.AddBlock"),
                Style = (Style)FindResource("GhostButtonStyle"),
                Height = 36,
                Padding = new Thickness(16, 0, 16, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            add.Click += (s, e) => AddBlockMenu(add, custom).IsOpen = true;
            rows.Add(new Border { Padding = new Thickness(ItemLook.Gap, 16, ItemLook.Gap, 16), Child = add });
            return rows;
        }

        // A block: its kind's icon, its name over what it is, its controls (full or half, up, down, edit, remove); a Fields
        // block's fields under it.
        private UIElement BlockFrame(CustomType custom, CustomBlock block)
        {
            StackPanel frame = new StackPanel { Margin = new Thickness(ItemLook.Gap, 16, ItemLook.Gap, 16) };
            AutomationProperties.SetName(frame, block.Name);

            Grid head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Border icon = ItemLook.Icon(KindGlyphs.GetValueOrDefault(block.Kind, ""));
            icon.Margin = new Thickness(0, 0, 16, 0);
            head.Children.Add(icon);
            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = block.Name, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ItemLook.Res("TextMain"), TextWrapping = TextWrapping.Wrap });
            text.Children.Add(ItemLook.Muted(BlockSummary(block), 12));
            Grid.SetColumn(text, 1);
            head.Children.Add(text);
            frame.Children.Add(head);

            List<CustomBlock> blocks = custom.Blocks.ToList();
            int at = blocks.IndexOf(block);
            WrapPanel controls = new WrapPanel { Margin = new Thickness(48, 10, 0, 0) };
            controls.Children.Add(WidthSwitch(block));
            controls.Children.Add(LayoutAction("", Loc.T("Custom.Layout.Up"), at > 0, () => MoveBlock(block.Id, -1)));
            controls.Children.Add(LayoutAction("", Loc.T("Custom.Layout.Down"), at < blocks.Count - 1, () => MoveBlock(block.Id, +1)));
            controls.Children.Add(LayoutAction("", Loc.T("Custom.Layout.EditBlock"), true, () => EditBlock(block.Id)));
            controls.Children.Add(LayoutAction("", Loc.T("Custom.Layout.RemoveBlock"), true, () => RemoveBlock(block.Id), danger: true));
            frame.Children.Add(controls);

            if (block.Kind == BlockKinds.Fields)
                frame.Children.Add(FieldList(custom, block));
            return frame;
        }

        // "Fields · 5 fields", "Log · Amounts: money".
        private static string BlockSummary(CustomBlock block)
        {
            string kind = Loc.T("Custom.Kind." + block.Kind);
            string? more = block.Kind switch
            {
                BlockKinds.Fields => Loc.Plural("Custom.Layout.FieldCount", block.Section!.Fields.Length),
                BlockKinds.Log => block.Layout.IsMoney ? Loc.T("Custom.Layout.LogMoney")
                    : string.IsNullOrWhiteSpace(block.Layout.Unit) ? Loc.T("Custom.Layout.LogPlain") : Loc.F("Custom.Layout.LogUnit", block.Layout.Unit.Trim()),
                _ => null
            };
            return more == null ? kind : kind + " · " + more;
        }

        // Full | Half, as the filters' segments.
        private UIElement WidthSwitch(CustomBlock block)
        {
            StackPanel pair = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 4), VerticalAlignment = VerticalAlignment.Center };
            foreach (bool half in new[] { false, true })
            {
                RadioButton option = new RadioButton
                {
                    Content = Loc.T(half ? "Custom.Layout.Half" : "Custom.Layout.Full"),
                    Style = (Style)FindResource("SegmentStyle"),
                    GroupName = "Width" + block.Id,
                    Height = 28,
                    Margin = new Thickness(0, 0, 4, 0),
                    IsChecked = block.Half == half,
                    ToolTip = Loc.T(half ? "Custom.Layout.HalfTip" : "Custom.Layout.FullTip")
                };
                option.Checked += (s, e) =>
                {
                    if (block.Half != half)
                        SaveLayout(layout => layout.Block(block.Id)!.Half = half, Loc.T("Custom.Undo.Layout"));
                };
                pair.Children.Add(option);
            }
            return pair;
        }

        private Button LayoutAction(string glyph, string name, bool enabled, Action click, bool danger = false)
        {
            Button button = ItemLook.RowAction(glyph, name, click, danger);
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : 0.35; // the button style has no look of its own for a disabled one
            button.Margin = new Thickness(0, 0, 4, 4);
            return button;
        }

        // A Fields block's fields: name over kind (and unit, "On card"), each with its moves, Edit and Remove; Add field.
        private UIElement FieldList(CustomType custom, CustomBlock block)
        {
            StackPanel list = new StackPanel { Margin = new Thickness(48, 8, 0, 0) };
            List<LayoutField> fields = block.Layout.Fields ?? new List<LayoutField>();
            List<LayoutField> known = fields.Where(f => FieldKinds.ValueKindOf(f.Kind) != null).ToList();
            HashSet<string> onCard = (custom.Layout.CardFields ?? new List<string>()).ToHashSet();
            foreach (LayoutField field in known)
            {
                Grid row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                StackPanel names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                names.Children.Add(new TextBlock { Text = field.Name, FontSize = 14, Foreground = ItemLook.Res("TextMain"), TextTrimming = TextTrimming.CharacterEllipsis });
                List<string> about = new List<string> { Loc.T("Custom.FieldKind." + field.Kind) };
                if (field.Kind == FieldKinds.Number && !string.IsNullOrWhiteSpace(field.Unit))
                    about.Add(field.Unit.Trim());
                if (field.Kind == FieldKinds.Choice)
                    about.Add(string.Join(", ", field.Choices ?? new List<string>()));
                if (onCard.Contains(field.Id))
                    about.Add(Loc.T("Custom.Layout.OnCard"));
                TextBlock kind = ItemLook.Muted(string.Join(" · ", about), 12);
                kind.TextWrapping = TextWrapping.NoWrap;
                kind.TextTrimming = TextTrimming.CharacterEllipsis;
                names.Children.Add(kind);
                row.Children.Add(names);

                int at = known.IndexOf(field);
                StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
                actions.Children.Add(LayoutAction("", Loc.T("Custom.Layout.Up"), at > 0, () => MoveField(block.Id, field.Id, -1)));
                actions.Children.Add(LayoutAction("", Loc.T("Custom.Layout.Down"), at < known.Count - 1, () => MoveField(block.Id, field.Id, +1)));
                actions.Children.Add(LayoutAction("", Loc.T("Custom.Layout.EditField"), true, () => OpenField(block.Id, field.Id)));
                actions.Children.Add(LayoutAction("", Loc.T("Custom.Layout.RemoveField"), true, () => RemoveField(block.Id, field.Id), danger: true));
                Grid.SetColumn(actions, 1);
                row.Children.Add(actions);
                list.Children.Add(row);
            }
            if (known.Count == 0)
                list.Children.Add(new TextBlock { Text = Loc.T("Custom.Layout.NoFields"), FontSize = 13, Foreground = ItemLook.Res("TextSub"), Margin = new Thickness(0, 0, 0, 8) });
            Button add = ItemLook.Link("+ " + Loc.T("Custom.Layout.AddField"), () => OpenField(block.Id, null));
            add.Margin = new Thickness(0);
            add.HorizontalAlignment = HorizontalAlignment.Left;
            list.Children.Add(add);
            return list;
        }

        // Every kind of block, with what it is for; there is one gallery at most (an item has one set of pictures).
        private ContextMenu AddBlockMenu(Button button, CustomType custom)
        {
            ContextMenu menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom };
            foreach (string kind in BlockKinds.All)
            {
                StackPanel header = new StackPanel();
                header.Children.Add(new TextBlock { Text = Loc.T("Custom.Kind." + kind), FontWeight = FontWeights.SemiBold });
                header.Children.Add(new TextBlock { Text = Loc.T("Custom.Kind." + kind + ".Text"), FontSize = 12, Foreground = ItemLook.Res("TextSub") });
                MenuItem item = new MenuItem
                {
                    Header = header,
                    Icon = new TextBlock { Text = KindGlyphs[kind], FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 14, Foreground = ItemLook.Res("Accent") },
                    IsEnabled = kind != BlockKinds.Gallery || !custom.Layout.HasGallery
                };
                AutomationProperties.SetName(item, Loc.T("Custom.Kind." + kind));
                item.Click += (s, e) => AddBlock(kind);
                menu.Items.Add(item);
            }
            return menu;
        }

        // ---- Changes -------------------------------------------------------------------------------------------

        // The layout as `change` makes it from a copy, saved for the collection (and the data of `removed` taken from its
        // items) as one undo step. The page is drawn again from what was stored. False if it couldn't be saved (said).
        private bool SaveLayout(Action<CustomLayout> change, string label, IReadOnlyCollection<string>? removed = null, bool destructive = false)
        {
            if (Custom is not CustomType custom)
                return false;
            CustomLayout layout = custom.Layout.Copy();
            change(layout);
            layout.Clean();
            bool saved = host.ChangeLayout(new LayoutChange(layout, removed ?? Array.Empty<string>(), label, destructive, item.Id));
            if (saved && !host.Preview && host.Reread(item.Id) is Entity stored)
                item = stored; // the item as saved (a removed block's data gone from it)
            Redraw();
            return saved;
        }

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

        private void AddBlock(string kind)
        {
            string id = CustomLayout.NewId();
            if (!SaveLayout(layout => layout.Blocks.Add(new LayoutBlock
                {
                    Id = id,
                    Kind = kind,
                    Name = Loc.T("Custom.Block." + kind),
                    Fields = kind == BlockKinds.Fields ? new List<LayoutField>() : null
                }), Loc.F("Undo.Added", Loc.T("Custom.Block." + kind))))
                return;
            Dispatcher.BeginInvoke(new Action(() => Descendants(Page).OfType<StackPanel>()
                .LastOrDefault(p => AutomationProperties.GetName(p) == Loc.T("Custom.Block." + kind))?.BringIntoView()), System.Windows.Threading.DispatcherPriority.Loaded);
            if (kind == BlockKinds.Fields)
                OpenField(id, null); // a Fields block is for its fields: the first one straight away
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
            LayoutField field = Custom!.Layout.Block(blockId)!.Fields!.First(f => f.Id == fieldId);
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

        // ---- The block's and the field's dialogs --------------------------------------------------------------

        // Its name; a log's amounts: money, or numbers in a unit of the user's.
        private void EditBlock(string id)
        {
            LayoutBlock block = Custom!.Layout.Block(id)!;
            StackPanel content = new StackPanel();
            TextBox name = DialogBox(block.Name);
            content.Children.Add(DialogCell(Loc.T("Custom.Block.Name"), name));
            CheckBox? money = null;
            TextBox? unit = null;
            if (block.Kind == BlockKinds.Log)
            {
                money = new CheckBox { Content = Loc.T("Custom.Block.Money"), IsChecked = block.IsMoney, Margin = new Thickness(0, 0, 0, 16) };
                content.Children.Add(money);
                unit = DialogBox(block.Unit ?? "");
                unit.IsEnabled = !block.IsMoney;
                money.Checked += (s, e) => unit.IsEnabled = false;
                money.Unchecked += (s, e) => unit.IsEnabled = true;
                content.Children.Add(DialogCell(Loc.T("Custom.Block.Unit"), unit));
            }
            string opened = name.Text + "|" + money?.IsChecked + "|" + unit?.Text;
            EditDialog dialog = null!;
            dialog = new EditDialog(this, Loc.T("Custom.Layout.EditBlock"), content, () =>
            {
                if (name.Text.Trim().Length == 0)
                {
                    Msg.Show(dialog, Loc.T("Custom.Block.NeedsName"), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    name.Focus();
                    return Task.FromResult(false);
                }
                return Task.FromResult(SaveLayout(layout =>
                {
                    LayoutBlock target = layout.Block(id)!;
                    target.Name = name.Text.Trim();
                    if (money != null)
                    {
                        target.Money = money.IsChecked == true ? null : false;
                        target.Unit = money.IsChecked == true || string.IsNullOrWhiteSpace(unit!.Text) ? null : unit.Text.Trim();
                    }
                }, Loc.T("Custom.Undo.Layout")));
            }, () => name.Text + "|" + money?.IsChecked + "|" + unit?.Text != opened, width: 480);
            dialog.Loaded += (s, e) =>
            {
                name.Focus();
                name.SelectAll();
            };
            dialog.ShowDialog();
        }

        // A field of a Fields block, new (fieldId null) or to change: its name, kind, choices (one per line), unit, and
        // whether cards show it (three at most).
        private void OpenField(string blockId, string? fieldId)
        {
            CustomLayout current = Custom!.Layout;
            LayoutField field = fieldId == null ? new LayoutField { Id = CustomLayout.NewId(), Kind = FieldKinds.Text }
                : current.Block(blockId)!.Fields!.First(f => f.Id == fieldId).Copy();
            List<string> card = current.CardFields ?? new List<string>();

            StackPanel content = new StackPanel();
            TextBox name = DialogBox(field.Name);
            content.Children.Add(DialogCell(Loc.T("Custom.Field.Name"), name));
            ComboBox kind = new ComboBox { Height = 36 };
            foreach (string k in FieldKinds.All)
                kind.Items.Add(new ComboBoxItem { Content = Loc.T("Custom.FieldKind." + k), Tag = k });
            kind.SelectedItem = kind.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == field.Kind) ?? kind.Items[0];
            AutomationProperties.SetName(kind, Loc.T("Custom.Field.Kind"));
            content.Children.Add(DialogCell(Loc.T("Custom.Field.Kind"), kind));
            TextBox choices = DialogBox(string.Join("\n", field.Choices ?? new List<string>()));
            choices.AcceptsReturn = true;
            choices.Height = 96;
            choices.TextWrapping = TextWrapping.Wrap;
            choices.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            UIElement choicesCell = DialogCell(Loc.T("Custom.Field.Choices"), choices);
            content.Children.Add(choicesCell);
            TextBox unit = DialogBox(field.Unit ?? "");
            UIElement unitCell = DialogCell(Loc.T("Custom.Field.Unit"), unit);
            content.Children.Add(unitCell);
            bool wasOnCard = card.Contains(field.Id);
            CheckBox onCard = new CheckBox
            {
                Content = Loc.T("Custom.Field.ShowOnCard"),
                IsChecked = wasOnCard,
                IsEnabled = wasOnCard || card.Count < CustomLayout.MaxCardFields
            };
            content.Children.Add(onCard);
            if (!onCard.IsEnabled)
                content.Children.Add(new TextBlock { Text = Loc.T("Custom.Field.CardFull"), FontSize = 12, Foreground = ItemLook.Res("TextSub"), Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap });
            void ShowKind()
            {
                string chosen = (string)((ComboBoxItem)kind.SelectedItem).Tag;
                choicesCell.Visibility = chosen == FieldKinds.Choice ? Visibility.Visible : Visibility.Collapsed;
                unitCell.Visibility = chosen == FieldKinds.Number ? Visibility.Visible : Visibility.Collapsed;
            }
            kind.SelectionChanged += (s, e) => ShowKind();
            ShowKind();

            string State() => string.Join("|", name.Text, ((ComboBoxItem)kind.SelectedItem).Tag, choices.Text, unit.Text, onCard.IsChecked);
            string opened = State();
            EditDialog dialog = null!;
            dialog = new EditDialog(this, Loc.T(fieldId == null ? "Custom.Layout.AddField" : "Custom.Layout.EditField"), content, () =>
            {
                string chosen = (string)((ComboBoxItem)kind.SelectedItem).Tag;
                List<string> values = choices.Text.Split('\n').Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                string? problem = name.Text.Trim().Length == 0 ? "Custom.Field.NeedsName"
                    : chosen == FieldKinds.Choice && values.Count == 0 ? "Custom.Field.NeedsChoices" : null;
                if (problem != null)
                {
                    Msg.Show(dialog, Loc.T(problem), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                    (problem == "Custom.Field.NeedsName" ? name : choices).Focus();
                    return Task.FromResult(false);
                }
                return Task.FromResult(SaveLayout(layout =>
                {
                    List<LayoutField> fields = layout.Block(blockId)!.Fields ??= new List<LayoutField>();
                    LayoutField target = fields.FirstOrDefault(f => f.Id == field.Id) ?? field;
                    target.Name = name.Text.Trim();
                    target.Kind = chosen;
                    target.Choices = chosen == FieldKinds.Choice ? values : null;
                    target.Unit = chosen == FieldKinds.Number && unit.Text.Trim().Length > 0 ? unit.Text.Trim() : null;
                    if (!fields.Contains(target))
                        fields.Add(target);
                    layout.CardFields ??= new List<string>();
                    layout.CardFields.Remove(field.Id);
                    if (onCard.IsChecked == true)
                        layout.CardFields.Add(field.Id);
                }, fieldId == null ? Loc.F("Undo.Added", name.Text.Trim()) : Loc.T("Custom.Undo.Layout")));
            }, () => State() != opened, width: 480);
            dialog.Loaded += (s, e) =>
            {
                name.Focus();
                name.SelectAll();
            };
            dialog.ShowDialog();
        }

        private static TextBox DialogBox(string text) => new TextBox { Text = text, Height = 36, VerticalContentAlignment = VerticalAlignment.Center };

        private static UIElement DialogCell(string label, FrameworkElement input)
        {
            AutomationProperties.SetName(input, label);
            StackPanel cell = ItemLook.Cell(label, input);
            cell.Margin = new Thickness(0, 0, 0, 16);
            return cell;
        }
    }
}
