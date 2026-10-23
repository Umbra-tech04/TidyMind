using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TidyMind
{
    // The pieces the item window is drawn with, on the app's look: quiet section headings (SectionLabelStyle), labels in
    // small muted text over prominent values, thin dividers rather than boxes, one accent color, spacing on 8 / 16 / 24.
    public static class ItemLook
    {
        public const double Gap = 24;

        public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

        public static Button Link(string text, Action click, bool danger = false)
        {
            Button link = new Button
            {
                Content = text,
                Style = (Style)Application.Current.FindResource("LinkButtonStyle"),
                Margin = new Thickness(16, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            if (danger)
                link.Foreground = Res("Danger");
            link.Click += (s, e) => click();
            return link;
        }

        // A small icon button at a row's end (Edit, Delete): focusable, so the keyboard reaches it, and its click is its
        // own (a Click bubbles, and these sit inside the row's own button).
        public static Button RowAction(string glyph, string name, Action click, bool danger = false)
        {
            Button button = new Button
            {
                Content = glyph,
                Style = (Style)Application.Current.FindResource("HoverIconButtonStyle"),
                Focusable = true,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = name
            };
            if (danger)
                button.Foreground = Res("Danger");
            AutomationProperties.SetName(button, name);
            button.Click += (s, e) =>
            {
                e.Handled = true;
                click();
            };
            return button;
        }

        public static TextBlock Muted(string text, double size = 13) => new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = Res("TextSub"),
            TextWrapping = TextWrapping.Wrap
        };

        public static Border Divider() => new Border { Height = 1, Background = Res("Border"), SnapsToDevicePixels = true };

        // Every section of every page is laid out the same way (one template, so switching pages changes the content
        // and nothing else): the same space above, a heading line of one height, the same space under it, the same
        // space below the content. Fields are padded by Gap on both sides; rows run edge to edge and pad themselves.
        public const double SectionTop = 20, HeadingBottom = 12, SectionBottom = 24, HeadingHeight = 28;

        // Where a section's heading stands: a section with nothing in it (one line saying so and the way to fill it)
        // has no space under it.
        public static Thickness HeadingMargin(bool compact) =>
            compact ? new Thickness(Gap, 16, Gap, 16) : new Thickness(Gap, SectionTop, Gap, HeadingBottom);

        // The space around a section's fields under its heading.
        public static Thickness FieldsMargin => new Thickness(Gap, 0, Gap, SectionBottom);

        // A section's heading (quiet: SectionLabelStyle) with its actions on the right.
        public static Grid Heading(string title, out StackPanel actions, bool compact = false)
        {
            Grid heading = new Grid { MinHeight = HeadingHeight, Margin = HeadingMargin(compact), Tag = HeadingTag };
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            heading.Children.Add(new TextBlock
            {
                Text = title,
                Style = (Style)Application.Current.FindResource("SectionLabelStyle"),
                Margin = new Thickness(0),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(actions, 1);
            heading.Children.Add(actions);
            return heading;
        }

        private static readonly object HeadingTag = new object();

        // The first heading inside a section (customizing a custom item renames its block there) and its title.
        public static (Grid Heading, TextBlock Title)? HeadingOf(DependencyObject section)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(section))
            {
                if (child is Grid grid && grid.Tag == HeadingTag && grid.Children.OfType<TextBlock>().FirstOrDefault() is TextBlock title)
                    return (grid, title);
                if (child is DependencyObject inner && HeadingOf(inner) is var found && found != null)
                    return found;
            }
            return null;
        }

        // A field's name in small muted text over its value.
        public static StackPanel Cell(string label, UIElement value)
        {
            StackPanel cell = new StackPanel();
            cell.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Res("TextSub"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
            cell.Children.Add(value);
            return cell;
        }

        public static TextBox Value(string text, Brush? color = null, FontWeight? weight = null) =>
            TextCopy.Selectable(text, 14, color ?? Res("TextMain"), weight);

        public static CellPanel Cells() => new CellPanel { MinColumn = 200, ColumnGap = 32, RowGap = 16 };

        // The filled fields of a section or a record as cells (empty ones left out): a date that runs out with how close
        // it is, a file as a link that opens it, specs as a little table, pictures to browse, links by name. A field named
        // like the section over it (Notes under Notes) shows its value alone.
        public static List<UIElement> ReadCells(IEnumerable<FieldDefinition> fields, Func<FieldDefinition, string?> read, Entity owner, EntityRecord? record,
            string? heading = null)
        {
            List<UIElement> cells = new List<UIElement>();
            foreach (FieldDefinition field in fields)
            {
                UIElement? value = field.Kind switch
                {
                    ValueKind.File => record?.File == null ? null : FileLink(record.File),
                    ValueKind.Specs => Specs(record?.Specs),
                    ValueKind.Pictures => Pictures(record?.Pictures),
                    _ => Shown(field, read(field), owner)
                };
                if (value == null)
                    continue;
                UIElement cell = field.Label == heading ? value : Cell(field.Label, value);
                CellPanel.SetWide(cell, field.Kind is ValueKind.LongText or ValueKind.CheckList or ValueKind.Links or ValueKind.Specs or ValueKind.Pictures);
                cells.Add(cell);
            }
            return cells;
        }

        private static UIElement? Shown(FieldDefinition field, string? stored, Entity owner)
        {
            string shown = field.Show(stored, owner);
            if (shown.Length == 0)
                return null;
            if (field.IsExpiry && Values.ParseDate(stored) is DateTime date)
            {
                ExpiryLevel level = Expiry.Level(date, DateTime.Today);
                return Value(shown + ", " + Expiry.Describe(date, DateTime.Today), Expiry.Brush(level), Expiry.Weight(level));
            }
            return Value(shown);
        }

        // "Size   M10x1.25x40", a line each.
        private static UIElement? Specs(List<SpecLine>? specs)
        {
            List<SpecLine> lines = (specs ?? new List<SpecLine>()).Where(s => s != null).ToList();
            if (lines.Count == 0)
                return null;
            Grid table = new Grid();
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < lines.Count; i++)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                TextBox name = TextCopy.Selectable(lines[i].Name, 14, Res("TextSub"));
                name.Margin = new Thickness(0, 0, 24, 4);
                TextBox value = Value(lines[i].Value);
                Grid.SetRow(name, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                table.Children.Add(name);
                table.Children.Add(value);
            }
            return table;
        }

        private static UIElement? Pictures(List<string>? names)
        {
            List<string> shown = (names ?? new List<string>()).Where(n => !string.IsNullOrEmpty(n)).ToList();
            if (shown.Count == 0)
                return null;
            PictureBrowser browser = new PictureBrowser(180, PictureBrowser.ThumbSize + 10, canReorder: false, stripForOne: false);
            browser.Picked += index => browser.ShowStored(shown, index, () => browser.IsVisible);
            browser.ShowStored(shown, 0, () => true);
            return browser;
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
            link.FontSize = 14;
            link.HorizontalAlignment = HorizontalAlignment.Left;
            link.ToolTip = Loc.T("Entity.File.Open");
            return link;
        }

        // A record's figure in its row: a date that runs out as how close it is (in its urgency's color), else the value.
        public static TextBlock Figure(FieldDefinition field, string? stored, Entity owner)
        {
            TextBlock figure = new TextBlock { FontSize = 13, Foreground = Res("TextBody"), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            if (field.IsExpiry && Values.ParseDate(stored) is DateTime date)
            {
                ExpiryLevel level = Expiry.Level(date, DateTime.Today);
                figure.Text = Expiry.Describe(date, DateTime.Today);
                figure.Foreground = Expiry.Brush(level);
                figure.FontWeight = Expiry.Weight(level);
                figure.ToolTip = field.Show(stored, owner);
            }
            else
                figure.Text = field.Show(stored, owner);
            return figure;
        }

        // A round plate with a kind's glyph (Segoe MDL2).
        public static Border Icon(string glyph) => new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Res("AccentSoft"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                Foreground = Res("Accent"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    // A section of the item's fields (FieldDefinition): read, the filled ones as label over value with Edit beside the
    // heading (none filled: one line and Fill in); editing, an input per field (FieldEditor) in the same place with
    // Save and Cancel. The window lets one section at a time be edited (gate); what Save does is the window's.
    public sealed class FieldsSection : StackPanel, IInlineEdit
    {
        public delegate bool EditGate(IInlineEdit section);

        private readonly SectionDefinition section;
        private readonly Entity owner;
        private readonly EditGate gate;
        private readonly Func<IReadOnlyList<FieldEditor>, bool> save;
        private readonly Grid heading;
        private readonly StackPanel actions;
        private readonly StackPanel body = new StackPanel();
        private List<FieldEditor>? editors;

        // Beside another block (a custom item's half blocks): an empty one keeps the heading where a filled one has it, so
        // the two headings stay in line.
        private readonly bool keepHeadingLine;

        public FieldsSection(SectionDefinition section, Entity owner, EditGate gate, Func<IReadOnlyList<FieldEditor>, bool> save,
            bool keepHeadingLine = false)
        {
            this.section = section;
            this.owner = owner;
            this.gate = gate;
            this.save = save;
            this.keepHeadingLine = keepHeadingLine;
            heading = ItemLook.Heading(section.Label, out actions);
            Children.Add(heading);
            Children.Add(body);
            ShowRead();
        }

        public SectionDefinition Definition => section;
        public bool IsEditing => editors != null;
        public bool HasChanges => editors != null && editors.Any(e => e.Changed);

        public void CancelEdit()
        {
            editors = null;
            ShowRead();
        }

        // Asks before throwing typed changes away; false if the user wants to go on editing.
        public bool ConfirmLeave() =>
            !HasChanges || Msg.Show(Window.GetWindow(this), Loc.T("Common.DiscardChanges"), Loc.T("Common.UnsavedChanges"),
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        public void BeginEdit()
        {
            if (editors != null || !gate(this))
                return;
            editors = section.Fields.Select(f => new FieldEditor(f, owner, f.Read(owner))).ToList();
            actions.Children.Clear();
            body.Children.Clear();
            Spacing(compact: false);
            body.Children.Add(EditDialog.Inputs(editors));
            body.Children.Add(EditDialog.Buttons(() => Task.FromResult(Save()), () => { if (ConfirmLeave()) CancelEdit(); }, new Thickness(0, ItemLook.Gap, 0, 0)));
            Dispatcher.BeginInvoke(new Action(() => editors?.FirstOrDefault()?.Focus()), System.Windows.Threading.DispatcherPriority.Input);
        }

        private bool Save()
        {
            if (editors == null || !EditDialog.Check(editors, Window.GetWindow(this)) || !save(editors))
                return false;
            editors = null;
            ShowRead();
            return true;
        }

        // Read with nothing in it: one line. Otherwise the heading over its fields.
        private void Spacing(bool compact)
        {
            compact &= !keepHeadingLine;
            heading.Margin = ItemLook.HeadingMargin(compact);
            body.Margin = compact ? new Thickness(0) : ItemLook.FieldsMargin;
        }

        private void ShowRead()
        {
            actions.Children.Clear();
            body.Children.Clear();
            List<UIElement> cells = ItemLook.ReadCells(section.Fields, f => f.Read(owner), owner, null, section.Label);
            Spacing(compact: cells.Count == 0);
            if (cells.Count == 0)
            {
                // Nothing filled in: one quiet line saying so, and the way to fill it.
                actions.Children.Add(ItemLook.Muted(Loc.T("Entity.Card.Empty")));
                actions.Children.Add(ItemLook.Link(Loc.T("Entity.Card.Fill"), BeginEdit));
                return;
            }
            actions.Children.Add(ItemLook.Link(Loc.T("Common.Edit"), BeginEdit));
            CellPanel panel = ItemLook.Cells();
            foreach (UIElement cell in cells)
                panel.Children.Add(cell);
            body.Children.Add(panel);
        }
    }

    // One record as a row of its list: an icon for its kind, its title over a quieter line, its figures on the right
    // (RecordDefinition.Subtitle / Figures / Icon). Edit (which the window opens in a dialog) and Delete are small icons
    // at its right end, shown while the pointer is on the row or the keyboard is in it, so they are never behind a click.
    // A click opens it: every other filled field, then what else the window adds (a part's moves). Nested ones are indented.
    public sealed class RecordRow : StackPanel
    {
        private readonly Border row;
        private readonly TextBlock chevron;
        private readonly StackPanel details = new StackPanel { Visibility = Visibility.Collapsed };
        private readonly bool hasDetails;
        private readonly Action showActions;

        public EntityRecord Record { get; }
        public event Action<RecordRow>? Toggled;

        // edit, delete: the icons (none: the row has none, as the Overview's). actions: what else the opened row offers.
        public RecordRow(RecordDefinition records, EntityRecord record, Entity owner, int depth, Action? edit, Action? delete,
            IEnumerable<UIElement> actions, UIElement? status, UIElement? outline, UIElement? extra)
        {
            Record = record;
            Tag = record.Id;
            double indent = depth * 24;

            Grid line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // An outline's open / close arrow (nested lists), then the kind's icon.
            if (outline != null)
                line.Children.Add(outline);
            if (records.Icon?.Invoke(record) is string glyph)
            {
                Border icon = ItemLook.Icon(glyph);
                icon.Margin = new Thickness(0, 0, 16, 0);
                Grid.SetColumn(icon, 1);
                line.Children.Add(icon);
            }

            StackPanel text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            WrapPanel title = new WrapPanel();
            title.Children.Add(new TextBlock { Text = records.Title(record), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = ItemLook.Res("TextMain"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 8, 0) });
            if (status != null)
                title.Children.Add(status);
            text.Children.Add(title);
            string subtitle = records.Subtitle?.Invoke(record, owner) ?? "";
            if (subtitle.Length > 0)
                text.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = ItemLook.Res("TextSub"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(text, 2);
            line.Children.Add(text);

            StackPanel figures = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (FieldDefinition field in records.Figures.Select(k => records.Fields.First(f => f.Key == k)))
            {
                TextBlock figure = ItemLook.Figure(field, record.Get(field.Key), owner);
                figure.MinWidth = 88;
                figure.Margin = new Thickness(16, 0, 0, 0);
                figures.Children.Add(figure);
            }
            Grid.SetColumn(figures, 3);
            line.Children.Add(figures);

            chevron = new TextBlock { Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 10, Foreground = ItemLook.Res("TextSub"),
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
            // An outline's rows have their open / close arrow on the left: a second chevron would only compete with it.
            chevron.Visibility = outline != null ? Visibility.Collapsed : Visibility.Visible;
            Grid.SetColumn(chevron, 5);
            line.Children.Add(chevron);

            // Edit and Delete: their room is always there (the row never changes size), they show on hover or focus.
            StackPanel icons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Hidden };
            if (edit != null)
                icons.Children.Add(ItemLook.RowAction("", Loc.T("Common.Edit"), edit));
            if (delete != null)
                icons.Children.Add(ItemLook.RowAction("", Loc.T("Common.Delete"), delete, danger: true));
            Grid.SetColumn(icons, 4);
            line.Children.Add(icons);

            row = new Border { Padding = new Thickness(ItemLook.Gap + indent, 12, ItemLook.Gap, 12), Background = Brushes.Transparent, Child = line };
            Button open = new Button { Template = PictureBrowser.FlatTemplate, Cursor = Cursors.Hand, Content = row, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(open, records.Title(record));
            open.MouseEnter += (s, e) => row.Background = ItemLook.Res("BgSubtle");
            open.MouseLeave += (s, e) => row.Background = IsOpen ? ItemLook.Res("BgSubtle") : Brushes.Transparent;
            open.Click += (s, e) => Toggled?.Invoke(this);
            showActions = () => icons.Visibility = open.IsMouseOver || open.IsKeyboardFocusWithin || IsOpen ? Visibility.Visible : Visibility.Hidden;
            open.MouseEnter += (s, e) => showActions();
            open.MouseLeave += (s, e) => showActions();
            open.IsKeyboardFocusWithinChanged += (s, e) => showActions();
            Children.Add(open);

            // Opened: every filled field the row doesn't show already (the title's, the figures, a value its quieter line
            // holds whole; a date figure stays, the row may show only how far off it is), what else the window shows (a
            // part's history), the other actions. A row with nothing of that has nothing to open.
            details.Margin = new Thickness(ItemLook.Gap + indent + (records.Icon == null ? 0 : 48), 4, ItemLook.Gap, 20);
            CellPanel cells = ItemLook.Cells();
            HashSet<string> inSubtitle = subtitle.Split(" · ").ToHashSet();
            IEnumerable<FieldDefinition> shown = records.Fields.Where(f => !records.TitleKeys.Contains(f.Key)
                && (f.Kind == ValueKind.Date || !records.Figures.Contains(f.Key))
                && !(f.Kind is ValueKind.Text or ValueKind.LongText or ValueKind.Choice or ValueKind.Number && inSubtitle.Contains(f.Show(record.Get(f.Key), owner))));
            foreach (UIElement cell in ItemLook.ReadCells(shown, f => record.Get(f.Key), owner, record))
                cells.Children.Add(cell);
            if (cells.Children.Count > 0)
                details.Children.Add(cells);
            if (extra != null)
                details.Children.Add(extra);
            List<UIElement> more = actions.ToList();
            if (more.Count > 0)
            {
                WrapPanel bar = new WrapPanel { Margin = new Thickness(-16, 16, 0, 0) };
                foreach (UIElement action in more)
                    bar.Children.Add(action);
                details.Children.Add(bar);
            }
            hasDetails = details.Children.Count > 0;
            if (!hasDetails)
                chevron.Visibility = Visibility.Collapsed;
            Children.Add(details);
        }

        public bool IsOpen => details.Visibility == Visibility.Visible;

        // A row whose click goes to the record where it is listed (the Overview's latest one): pointing right, not down.
        public void PointRight()
        {
            chevron.Text = "";
            chevron.Visibility = Visibility.Visible;
        }

        public void SetOpen(bool open)
        {
            open &= hasDetails;
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Text = open ? "" : "";
            row.Background = open ? ItemLook.Res("BgSubtle") : Brushes.Transparent;
            showActions();
        }
    }

    // A log (fuel, odometer readings) as a small table: a column per log column (numbers right-aligned with their unit),
    // newest first, its summary under it. Edit opens its table editor (LogEditor) in a dialog.
    public sealed class LogSection : StackPanel
    {
        public LogSection(LogDefinition log, List<Dictionary<string, string>> rows, Entity owner, Action edit)
        {
            Grid heading = ItemLook.Heading(log.Label, out StackPanel actions, compact: rows.Count == 0);
            Children.Add(heading);
            if (rows.Count == 0)
            {
                // Nothing written down: one line, as an empty section.
                actions.Children.Add(ItemLook.Muted(Loc.T("Entity.Log.Empty")));
                actions.Children.Add(ItemLook.Link("+ " + Loc.T("Log.AddRow"), edit));
                return;
            }
            actions.Children.Add(ItemLook.Link(Loc.T("Common.Edit"), edit));

            Grid table = new Grid { Margin = new Thickness(ItemLook.Gap, 0, ItemLook.Gap, 0) };
            // Columns as wide as their content, side by side; the rest of the width left empty.
            foreach (LogColumn _ in log.Columns)
                table.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            table.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            List<Dictionary<string, string>> sorted = EntityTypes.Sorted(log, Enumerable.Reverse(rows)).ToList();
            for (int r = 0; r <= sorted.Count; r++)
            {
                table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                for (int c = 0; c < log.Columns.Length; c++)
                {
                    LogColumn column = log.Columns[c];
                    bool number = column.Kind == ValueKind.Number;
                    TextBlock cell = r == 0
                        ? new TextBlock { Text = column.Label, FontSize = 12, Foreground = ItemLook.Res("TextSub") }
                        : new TextBlock { Text = Cell(column, sorted[r - 1].GetValueOrDefault(column.Key), owner), FontSize = 14, Foreground = ItemLook.Res("TextMain") };
                    cell.TextAlignment = number ? TextAlignment.Right : TextAlignment.Left;
                    cell.Margin = new Thickness(c == 0 ? 0 : 40, 0, 0, 8);
                    Grid.SetRow(cell, r);
                    Grid.SetColumn(cell, c);
                    table.Children.Add(cell);
                }
            }
            Children.Add(table);
            Thickness below = new Thickness(ItemLook.Gap, 0, ItemLook.Gap, ItemLook.SectionBottom);
            if (log.Summary?.Invoke(rows) is string summary)
            {
                TextBlock note = ItemLook.Muted(summary, 12);
                note.Margin = below;
                Children.Add(note);
            }
            else
                table.Margin = new Thickness(below.Left, 0, below.Right, below.Bottom - 8); // the rows carry 8 under each
        }

        private static string Cell(LogColumn column, string? stored, Entity owner)
        {
            if (string.IsNullOrWhiteSpace(stored))
                return "";
            if (column.Kind == ValueKind.Date && Values.ParseDate(stored) is DateTime date)
                return date.ToString(Loc.T("Format.ShortDate"), Loc.Culture);
            if (column.Kind == ValueKind.Number && Values.Number(stored) is double number)
                return Units.Format(number, column.Unit, owner);
            return stored;
        }
    }

    // A record's or a log's editor in a window of its own over the item window (one at a time): a heading, the inputs,
    // Save and Cancel. Esc or X asks first when something was changed. Save runs the window's save; the dialog closes
    // once that worked.
    public sealed class EditDialog : Window
    {
        private readonly Func<bool> hasChanges;
        private bool saved;

        // A record dialog's inputs (none for a log's table).
        public IReadOnlyList<FieldEditor> Editors { get; init; } = Array.Empty<FieldEditor>();

        public EditDialog(Window owner, string title, UIElement content, Func<Task<bool>> save, Func<bool> hasChanges, double width = 640)
        {
            this.hasChanges = hasChanges;
            Owner = owner;
            Title = title;
            Width = Math.Min(width, SystemParameters.WorkArea.Width - 40);
            SizeToContent = SizeToContent.Height;
            MaxHeight = SystemParameters.WorkArea.Height * 0.88;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            UseLayoutRounding = true; // hairlines stay one pixel at any scale

            Grid layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.Children.Add(new Border
            {
                Background = ItemLook.Res("BgPanel"),
                BorderBrush = ItemLook.Res("Border"),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(ItemLook.Gap, 20, ItemLook.Gap, 16),
                Child = new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeights.Bold, Foreground = ItemLook.Res("TextMain"), TextWrapping = TextWrapping.Wrap }
            });
            ScrollViewer scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Border { Padding = new Thickness(ItemLook.Gap), Child = content } };
            Grid.SetRow(scroll, 1);
            layout.Children.Add(scroll);
            Border footer = new Border
            {
                Background = ItemLook.Res("BgPanel"),
                BorderBrush = ItemLook.Res("Border"),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(ItemLook.Gap, 12, ItemLook.Gap, 12),
                Child = Buttons(async () =>
                {
                    if (!await save())
                        return false;
                    saved = true;
                    Close();
                    return true;
                }, Close, new Thickness(0))
            };
            Grid.SetRow(footer, 2);
            layout.Children.Add(footer);
            Content = layout;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key != Key.Escape || e.Handled)
                return;
            e.Handled = true;
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!saved && hasChanges() && Msg.Show(this, Loc.T("Common.DiscardChanges"), Loc.T("Common.UnsavedChanges"),
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                e.Cancel = true;
            base.OnClosing(e);
        }

        // The inputs of a set of fields, label over input, in columns that reflow.
        public static CellPanel Inputs(IReadOnlyList<FieldEditor> editors)
        {
            CellPanel cells = new CellPanel { MinColumn = 220, ColumnGap = ItemLook.Gap, RowGap = 16 };
            foreach (FieldEditor editor in editors)
            {
                UIElement cell = editor.HasOwnLabel ? editor.Element : ItemLook.Cell(editor.Field.Label, editor.Element);
                CellPanel.SetWide(cell, editor.IsWide);
                cells.Children.Add(cell);
            }
            return cells;
        }

        // A number that can't be read as one: said, and the keyboard put there. True when all can be stored.
        public static bool Check(IReadOnlyList<FieldEditor> editors, Window? owner)
        {
            Keyboard.ClearFocus();
            FieldEditor? unreadable = editors.FirstOrDefault(e => e.Value == null);
            if (unreadable == null)
                return true;
            Msg.Show(owner, Loc.F("Entity.NeedsNumber", unreadable.Field.Label), Loc.T("Common.Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
            unreadable.Focus();
            return false;
        }

        // Save (primary) and Cancel, on the right.
        public static StackPanel Buttons(Func<Task<bool>> save, Action cancel, Thickness margin)
        {
            StackPanel row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = margin };
            Button saveButton = new Button { Content = Loc.T("Common.Save"), MinWidth = 96, Height = 36, Padding = new Thickness(16, 0, 16, 0), FontWeight = FontWeights.SemiBold };
            Button cancelButton = new Button { Content = Loc.T("Common.Cancel"), MinWidth = 96, Height = 36, Padding = new Thickness(16, 0, 16, 0),
                Style = (Style)Application.Current.FindResource("GhostButtonStyle"), Margin = new Thickness(8, 0, 0, 0) };
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

        // The records' editor: its fields' inputs; the log's: its table editor (whose open cell counts as typed).
        public static Func<bool> Changed(IReadOnlyList<FieldEditor> editors) => () => editors.Any(e => e.Changed);

        public static Func<bool> Changed(LogEditor editor, string opened) => () =>
        {
            Keyboard.ClearFocus();
            return editor.HasProblem || JsonSerializer.Serialize(editor.FilledRows) != opened;
        };
    }
}
