using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using TidyMind;

// Harness custom, M3: customizing the layout on the item's own page. The Customize switch, the blocks as the item shows
// them with their toolbars (moves, half and full width, rename in place, a log's amounts, remove), a Fields block's
// fields (added by typing a name, the kind from a dropdown, a number's unit, a choice's choices, On card with at most
// three, moved, renamed, removed); a removal that takes items' data asks first and goes with it in one step, which Ctrl+Z
// brings back byte for byte; "+ Add" and its menu; Esc; the collection's menu; nothing cut off at three widths.
static partial class Program
{
    static CustomLayout GearLayout(Profile memory)
    {
        CustomLayoutStore.TryLoad(memory, out CustomLayout layout, out _);
        return layout;
    }

    static LayoutBlock GearBlock(Profile memory, string kind) => GearLayout(memory).Blocks.First(b => b.Kind == kind);

    // Like DriveItem, but for the item window about to open whose title is `title` (one closing meanwhile isn't taken for
    // it); whatever happens it is closed afterwards, so the caller's ShowDialog always returns.
    static void DriveTitled(MainWindow main, string title, Func<ItemWindow, Task> act) => DriveWindow(main, w => w.Title == title, "titled " + title, act);

    static void DriveWindow(MainWindow main, Func<ItemWindow, bool> which, string what, Func<ItemWindow, Task> act)
    {
        _ = main.Dispatcher.BeginInvoke(new Action(async () =>
        {
            ItemWindow window = null;
            for (int i = 0; i < 80 && window == null; i++)
            {
                await Wait(100);
                window = Application.Current.Windows.OfType<ItemWindow>().FirstOrDefault(w => w.IsVisible && which(w));
            }
            if (window == null)
            {
                Check(false, "a window " + what + " opened: " + string.Join(", ", Application.Current.Windows.OfType<ItemWindow>().Select(w => w.Title)));
                foreach (ItemWindow other in Application.Current.Windows.OfType<ItemWindow>().ToList())
                    other.Close();
                return;
            }
            try
            {
                await Wait(300);
                await act(window);
            }
            catch (Exception e)
            {
                Check(false, "the window driver threw: " + e);
            }
            if (window.IsVisible)
                window.Close();
        }), DispatcherPriority.Background);
    }

    static bool Customizing(ItemWindow w) => Field<bool>(w, "customizing");

    static CheckBox CustomizeSwitch(ItemWindow w) =>
        Descendants(w).OfType<CheckBox>().FirstOrDefault(c => AutomationProperties.GetName(c) == Loc.T("Custom.Customize"));

    // A block on the page while customizing (named by its block), and a button of its toolbar.
    static Grid ShellOf(ItemWindow w, string block) =>
        Descendants(PageOf(w)).OfType<Grid>().First(g => AutomationProperties.GetName(g) == block && Descendants(g).OfType<Button>().Any(b =>
            AutomationProperties.GetName(b) == Loc.T("Custom.Layout.RemoveBlock")));

    static Button Tool(ItemWindow w, string block, string name) =>
        Descendants(ShellOf(w, block)).OfType<Button>().First(b => AutomationProperties.GetName(b) == Loc.T(name));

    // A block's name, edited where it stands.
    static TextBox NameBox(ItemWindow w, string block, string kind) =>
        Descendants(ShellOf(w, block)).OfType<TextBox>().First(t => AutomationProperties.GetName(t) == Loc.F("Custom.Layout.NameOf", Loc.T("Custom.Kind." + kind)));

    static TextBox LayoutTarget(ItemWindow w, string block, string field) =>
        (TextBox)Field<Dictionary<(string, string), FrameworkElement>>(w, "layoutTargets")[(block, field)];

    // An edit typed into a box that saves when it loses the keyboard (a name edited in place, a unit).
    static void TypeAndLeave(TextBox box, string text)
    {
        box.Text = text;
        box.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, box, null) { RoutedEvent = Keyboard.LostKeyboardFocusEvent });
    }

    static void TypeAndEnter(TextBox box, string text)
    {
        box.Text = text;
        PressKey(box, Key.Enter);
    }

    static ComboBox KindBox(ItemWindow w, string field) =>
        Descendants(PageOf(w)).OfType<ComboBox>().First(c => AutomationProperties.GetName(c) == Loc.F("Custom.Field.KindOf", field));

    static void PickKind(ItemWindow w, string field, string kind)
    {
        ComboBox box = KindBox(w, field);
        box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == kind);
    }

    static CheckBox OnCardBox(ItemWindow w, string field) =>
        Descendants(PageOf(w)).OfType<CheckBox>().First(c => AutomationProperties.GetName(c) == Loc.F("Custom.Field.OnCardOf", field));

    static void Click(ToggleButton box)
    {
        box.IsChecked = box.IsChecked != true;
        box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, box));
    }

    static async Task CustomBuilder(MainWindow main)
    {
        Console.WriteLine("Customizing the layout on the item's page");
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        Dictionary<string, string> ids = GearIds(memory);
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(400);
        Guid tentId = EntityStore.Load(memory).First(e => e.Name == "Tent").Id;
        string layoutFile = MemoryFiles.Layout(memory), itemsFile = MemoryFiles.Items(memory);
        string Named(string block) => GearLayout(memory).Block(ids[block]).Name;
        string specs = Named("fields"), packing = Named("checklist"), repairs = Named("log"), spares = Named("quantities");

        await WithVehicle(main, view, tentId, async w =>
        {
            Check(CustomizeSwitch(w) is CheckBox off && off.IsChecked == false && !Customizing(w), "the item opens as always, Customize off");
            Check(!Descendants(PageOf(w)).OfType<Button>().Any(b => AutomationProperties.GetName(b) == Loc.T("Custom.Layout.RemoveBlock")), "no toolbars while it is off");
            Click(CustomizeSwitch(w));
            await Wait(400);
            Check(Customizing(w) && CustomizeSwitch(w).IsChecked == true, "the switch turns it on");
            Check(PageTexts(w).Contains(Loc.T("Custom.Layout.Hint")), "it says changes apply to every item and can be undone");
            Check(ButtonNamed(w, Loc.T("Custom.Layout.Add")) != null, "+ Add at the end");
            Check(ButtonNamed(w, Loc.T("Common.Delete")) != null, "the item's own actions stay");
            List<string> known = GearLayout(memory).Blocks.Where(b => BlockKinds.Known(b.Kind)).Select(b => b.Name).ToList();
            List<Grid> shells = known.Select(n => ShellOf(w, n)).ToList();
            Check(shells.Count == 7, "every known block on the page, with its toolbar");
            Check(shells.All(s => Descendants(s).OfType<Button>().Where(b => AutomationProperties.GetName(b) == Loc.T("Custom.Layout.RemoveBlock"))
                .All(b => ((FrameworkElement)VisualTreeParent(b)).Visibility == Visibility.Hidden || s.IsMouseOver || s.IsKeyboardFocusWithin)), "the toolbars are hidden until the pointer or the keyboard is on a block");
            Check(Descendants(PageOf(w)).OfType<ChecklistSection>().Count() == 1 && Descendants(PageOf(w)).OfType<CompactRecords>().Count() == 1,
                "the blocks are the item's own, still in use");
            Check(!Sections(w).Any(s => s.Definition.Label == specs) && KindBox(w, "Brand") != null, "a Fields block lists its fields with their kind");
            Check(NameBox(w, Named("notes"), BlockKinds.Notes).Text == Named("notes"), "a block's name is edited where it stands");

            foreach (double width in new[] { 560.0, 760, 1100 })
            {
                w.Width = width;
                await Wait(350);
                List<string> cut = CutOff(w).Concat(Clipped(w)).ToList();
                Check(cut.Count == 0, "nothing cut off at " + width + " px" + (cut.Count > 0 ? ": " + string.Join("; ", cut.Take(4)) : ""));
            }
            w.Width = 1100;
            await Wait(300);
            ((FrameworkElement)VisualTreeParent(Tool(w, packing, "Custom.Layout.RemoveBlock"))).Visibility = Visibility.Visible; // as on hover
            RenderWindow(w, "custom-customize");

            // Moves: one step each, undone and redone by the window's own Ctrl+Z.
            string start = File.ReadAllText(layoutFile);
            List<string> Order() => GearLayout(memory).Blocks.Select(b => b.Kind).ToList();
            ClickNow(Tool(w, packing, "Custom.Layout.Up"));
            await Wait(300);
            Check(Order().IndexOf(BlockKinds.Checklist) == 2 && Order().IndexOf(BlockKinds.Notes) == 3, "a block moved up from its toolbar: " + string.Join(", ", Order()));
            Check(UndoHost.Of(w).Undo() && File.ReadAllText(layoutFile) == start, "undone byte for byte");
            await Wait(200);
            Check(Customizing(w), "still customizing after an undo");
            Check(UndoHost.Of(w).Redo() && Order().IndexOf(BlockKinds.Checklist) == 2, "redone");
            Invoke(w, "MoveBlock", ids["checklist"], +1);
            Invoke(w, "MoveBlock", ids["log"], +1);
            await Wait(300);
            Check(Order().Last() == "Timeline" && Order()[Order().Count - 2] == BlockKinds.Log, "a block moved past the last known one keeps the unknown one where it is");
            Check(!Tool(w, repairs, "Custom.Layout.Down").IsEnabled && !Tool(w, specs, "Custom.Layout.Up").IsEnabled, "no move past either end");
            Invoke(w, "MoveBlock", ids["log"], -1);
            await Wait(300);

            // Half and full width: one small toggle.
            ClickNow(Tool(w, specs, "Custom.Layout.ToHalf"));
            await Wait(300);
            Check(GearBlock(memory, BlockKinds.Fields).Half, "made half width");
            ClickNow(Tool(w, specs, "Custom.Layout.ToFull"));
            await Wait(300);
            Check(!GearBlock(memory, BlockKinds.Fields).Half, "and full again");

            // A block renamed where its name stands; Esc in it puts the stored name back; an empty name is never saved.
            TypeAndLeave(NameBox(w, GearBlock(memory, BlockKinds.Fields).Name, BlockKinds.Fields), "Equipment");
            await Wait(300);
            Check(GearBlock(memory, BlockKinds.Fields).Name == "Equipment" && ShellOf(w, "Equipment") != null, "a block renamed in place");
            string named = File.ReadAllText(layoutFile);
            TypeAndLeave(NameBox(w, GearBlock(memory, BlockKinds.Fields).Name, BlockKinds.Fields), "  ");
            await Wait(200);
            Check(File.ReadAllText(layoutFile) == named, "an empty name isn't saved");

            // A log's amounts, from its toolbar: a number in km.
            Button amounts = Tool(w, repairs, "Custom.Layout.Amounts");
            ClickNow(amounts);
            await Wait(200);
            ContextMenu amountMenu = amounts.ContextMenu;
            Check(amountMenu?.Items.Count == 3 && ((MenuItem)amountMenu.Items[0]).IsChecked, "money, plain numbers or a unit; money now");
            amountMenu.IsOpen = false;
            AnswerPrompt(main, "km");
            ((MenuItem)amountMenu.Items[2]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Wait(400);
            LayoutBlock log = GearBlock(memory, BlockKinds.Log);
            Check(log.Money == false && log.Unit == "km", "a log's amounts: a number in km");
            RecordDefinition logList = EntityTypes.Records(CustomTypes.TypeOf(memory.Id)).First(r => r.Key == ids["log"]);
            Check(logList.TotalKey == null && logList.Fields.First(f => f.Key == "Amount").Unit == Units.Own("km"), "no money totals, the amounts in km");
            Check(!Descendants(ShellOf(w, packing)).OfType<Button>().Any(b => AutomationProperties.GetName(b) == Loc.T("Custom.Layout.Amounts")), "only a log has amounts");

            // Fields: one added by typing its name (text, at the end, the keyboard back in the box); on the card (the
            // third), a fourth can't go on it.
            TypeAndEnter(LayoutTarget(w, ids["fields"], "+"), "Serial");
            await Wait(400);
            CustomLayout layout = GearLayout(memory);
            LayoutField serial = layout.Blocks.First(b => b.Kind == BlockKinds.Fields).Fields.LastOrDefault();
            Check(serial?.Name == "Serial" && serial.Kind == FieldKinds.Text, "a field added by its name and Enter, text at the end");
            Check(FocusManager.GetFocusedElement(w) == LayoutTarget(w, ids["fields"], "+") && LayoutTarget(w, ids["fields"], "+").Text == "",
                "the keyboard back in the empty box for the next one");
            Check(KindBox(w, "Serial") != null && (string)((ComboBoxItem)KindBox(w, "Serial").SelectedItem).Tag == FieldKinds.Text, "its kind beside it: text");
            Click(OnCardBox(w, "Serial"));
            await Wait(300);
            layout = GearLayout(memory);
            Check(layout.CardFields.Count == 3 && layout.CardFields[2] == serial.Id, "put on the card");
            TypeAndEnter(LayoutTarget(w, ids["fields"], "+"), "Size");
            await Wait(400);
            Check(!OnCardBox(w, "Size").IsEnabled && (string)OnCardBox(w, "Size").ToolTip == Loc.T("Custom.Field.CardFull"), "a fourth field can't go on the card, and that is said");

            // A kind changed from the dropdown: a choice asks for its choices (closed: nothing changes).
            string beforeChoice = File.ReadAllText(layoutFile);
            AnswerPrompt(main, null);
            PickKind(w, "Size", FieldKinds.Choice);
            await Wait(700);
            Check(File.ReadAllText(layoutFile) == beforeChoice && (string)((ComboBoxItem)KindBox(w, "Size").SelectedItem).Tag == FieldKinds.Text,
                "no choices given: still text, the dropdown back");
            AnswerPrompt(main, "S\nM\nL\nM");
            PickKind(w, "Size", FieldKinds.Choice);
            await Wait(700);
            LayoutField size = GearBlock(memory, BlockKinds.Fields).Fields.FirstOrDefault(f => f.Name == "Size");
            Check(size?.Kind == FieldKinds.Choice && size.Choices.SequenceEqual(new[] { "S", "M", "L" }), "a choice field, its choices once each");
            Check(ButtonNamed(w, Loc.T("Custom.Field.EditChoices")) != null, "Choices… beside a choice field");
            AnswerPrompt(main, "Red\nBlue\nGreen");
            Invoke(w, "EditChoices", ids["fields"], GearBlock(memory, BlockKinds.Fields).Fields.First(f => f.Id == ids["colour"]));
            await Wait(300);
            Check(GearBlock(memory, BlockKinds.Fields).Fields.First(f => f.Id == ids["colour"]).Choices.SequenceEqual(new[] { "Red", "Blue", "Green" }), "a field's choices changed");

            // A number's unit, typed beside it; a field renamed in place.
            TextBox unit = Descendants(PageOf(w)).OfType<TextBox>().First(t => AutomationProperties.GetName(t) == Loc.T("Custom.Field.Unit"));
            Check(unit.Text == "kg", "a number shows its unit");
            TypeAndLeave(unit, "lb");
            await Wait(300);
            Check(GearBlock(memory, BlockKinds.Fields).Fields.First(f => f.Id == ids["weight"]).Unit == "lb", "the unit changed");
            TypeAndLeave(Descendants(PageOf(w)).OfType<TextBox>().First(t => AutomationProperties.GetName(t) == Loc.T("Custom.Field.Unit")), "kg");
            await Wait(300);
            TypeAndLeave(LayoutTarget(w, ids["fields"], size.Id), "Sizes");
            await Wait(300);
            Check(GearBlock(memory, BlockKinds.Fields).Fields.First(f => f.Id == size.Id).Name == "Sizes", "a field renamed in place");
            Invoke(w, "MoveField", ids["fields"], size.Id, -1);
            await Wait(300);
            List<string> fieldOrder = GearBlock(memory, BlockKinds.Fields).Fields.Select(f => f.Name).ToList();
            Check(fieldOrder.IndexOf("Sizes") == fieldOrder.IndexOf("Serial") - 1, "a field moved up: " + string.Join(", ", fieldOrder));

            // A field the item has a value in: asked, then gone from the layout and the item in one step.
            string layoutBefore = File.ReadAllText(layoutFile), itemsBefore = File.ReadAllText(itemsFile);
            int boxes = selectBoxes.Count;
            Invoke(w, "RemoveField", ids["fields"], ids["boxed"]);
            await Wait(400);
            Check(selectBoxes.Count == boxes + 1 && selectBoxes.Last().Contains("Boxed"), "asked first, naming it: " + selectBoxes.LastOrDefault());
            Check(GearBlock(memory, BlockKinds.Fields).Fields.All(f => f.Id != ids["boxed"]) && !StoredItem(memory, tentId).Details.ContainsKey(ids["boxed"]),
                "gone from the layout and from the item");
            Check(UndoHost.Of(w).Undo() && File.ReadAllText(layoutFile) == layoutBefore && File.ReadAllText(itemsFile) == itemsBefore,
                "Ctrl+Z: both files byte for byte");
            await Wait(300);

            // Answered No: nothing changes.
            answerCancel = true;
            ClickNow(Tool(w, packing, "Custom.Layout.RemoveBlock"));
            await Wait(400);
            answerCancel = false;
            Check(File.ReadAllText(layoutFile) == layoutBefore && File.ReadAllText(itemsFile) == itemsBefore, "No: nothing removed");

            // A block with data: its records go from the item (one step with the layout).
            Invoke(w, "RemoveBlock", ids["checklist"]);
            await Wait(400);
            Check(GearLayout(memory).Blocks.All(b => b.Id != ids["checklist"]) && StoredItem(memory, tentId).Records.ContainsKey(ids["checklist"]) == false,
                "a block removed with its lines");
            Check(UndoHost.Of(w).Undo() && File.ReadAllText(layoutFile) == layoutBefore && File.ReadAllText(itemsFile) == itemsBefore, "and brought back");
            await Wait(300);

            // "+ Add": every kind by its plain name, the gallery not twice; one added lands at the end, full width, named by
            // its kind; an empty one goes without a question.
            ContextMenu menu = (ContextMenu)typeof(ItemWindow).GetMethod("AddMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(w, new object[] { new Button(), CustomTypes.OfMemory(memory) });
            Check(menu.Items.Count == 7 && BlockKinds.All.All(k => menu.Items.OfType<MenuItem>().Any(i => AutomationProperties.GetName(i) == Loc.T("Custom.Kind." + k)))
                && !menu.Items.OfType<MenuItem>().First(i => AutomationProperties.GetName(i) == Loc.T("Custom.Kind.Gallery")).IsEnabled,
                "every kind to add, the gallery not twice");
            boxes = selectBoxes.Count;
            ((MenuItem)menu.Items[BlockKinds.All.ToList().IndexOf(BlockKinds.Quantities)]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Wait(300);
            LayoutBlock added = GearLayout(memory).Blocks.Last();
            Check(added.Kind == BlockKinds.Quantities && added.Name == Loc.T("Custom.Block.Quantities") && !added.Half, "a block added at the end, full width, named by its kind");
            Check(ShellOf(w, added.Name) != null && Descendants(PageOf(w)).OfType<CompactRecords>().Count() == 2, "on the page at once, ready to use");
            Invoke(w, "RemoveBlock", added.Id);
            await Wait(300);
            Check(selectBoxes.Count == boxes && GearLayout(memory).Blocks.Count(b => b.Kind == BlockKinds.Quantities) == 1, "an empty block removed without a question");

            // Off: the page is the item again, with the new layout.
            Click(CustomizeSwitch(w));
            await Wait(300);
            Check(!Customizing(w) && Sections(w).Any(s => s.Definition.Label == "Equipment"), "switched off: the item, with its renamed block");
            Check(Sections(w).First().Definition.Fields.Any(f => f.Label == "Serial") && !PageTexts(w).Contains(Loc.T("Custom.Layout.Hint")), "its new field, no toolbars or + Add");
            Invoke(w, "Customize", true);
            await Wait(200);
            PressKey(w, Key.Escape);
            await Wait(300);
            Check(w.IsVisible && !Customizing(w), "Esc turns Customize off first");
        });

        Console.WriteLine("Customize layout from the collection's menu");
        ContextMenu memoryMenu = (ContextMenu)typeof(MainWindow).GetMethod("MemoryMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(main, new object[] { memory });
        Check(memoryMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header == Loc.T("Custom.Menu.Customize")), "Customize layout in a custom collection's menu");
        Profile shoes = AddMemory(main, "Plain box", ProfileType.Collection);
        ContextMenu plainMenu = (ContextMenu)typeof(MainWindow).GetMethod("MemoryMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(main, new object[] { shoes });
        Check(!plainMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header == Loc.T("Custom.Menu.Customize")), "not in a standard collection's");
        RemoveMemory(main, shoes);
        DriveWindow(main, w => true, "of the collection", async w =>
        {
            Check(Customizing(w) && !Field<ItemWindow.Host>(w, "host").Preview && EntityStore.Load(memory).Any(e => e.Name == w.Title),
                "an item of the collection opens, customizing: " + w.Title);
            Invoke(w, "MoveBlock", ids["log"], -1);
            await Wait(300);
            Check(UndoHost.Of(w).Undo() && w.IsVisible && Customizing(w), "its own Ctrl+Z, and it stays open");
        });
        Invoke(main, "CustomizeLayout", memory);
        await Wait(400);
    }

    static DependencyObject VisualTreeParent(DependencyObject child) => System.Windows.Media.VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child);
}
