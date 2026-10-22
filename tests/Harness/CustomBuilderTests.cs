using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using TidyMind;

// Harness custom, M3: the layout editor in the item window. Blocks moved, made half width, renamed, a log's amounts
// set, added and removed; fields added (on the card, at most three), changed and removed; a removal that takes items'
// data asks first and goes with it in one step, which Ctrl+Z brings back byte for byte; Done and Esc; the editor on its
// own from the collection's menu; nothing cut off at three widths.
static partial class Program
{
    static CustomLayout GearLayout(Profile memory)
    {
        CustomLayoutStore.TryLoad(memory, out CustomLayout layout, out _);
        return layout;
    }

    static LayoutBlock GearBlock(Profile memory, string kind) => GearLayout(memory).Blocks.First(b => b.Kind == kind);

    // Fills the next block or field dialog (an EditDialog) with `fill`, then presses its Save.
    static void AnswerLayoutDialog(MainWindow main, Func<EditDialog, Task> fill) => AnswerRecord(main, fill);

    static TextBox DialogText(EditDialog dialog, int index) => Descendants(dialog).OfType<TextBox>().Where(t => t.IsVisible || index < 0).ElementAt(Math.Max(index, 0));

    static async Task CustomBuilder(MainWindow main)
    {
        Console.WriteLine("The layout editor");
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        Dictionary<string, string> ids = GearIds(memory);
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(400);
        Guid tentId = EntityStore.Load(memory).First(e => e.Name == "Tent").Id;
        string layoutFile = MemoryFiles.Layout(memory), itemsFile = MemoryFiles.Items(memory);

        await WithVehicle(main, view, tentId, async w =>
        {
            ClickNow(ButtonNamed(w, Loc.T("Custom.EditLayout")));
            await Wait(400);
            Check(Field<bool>(w, "layoutMode"), "Edit layout turns the page into the editor");
            Check(PageTexts(w).Contains(Loc.T("Custom.Layout.Hint")), "it says changes apply to every item and can be undone");
            Check(ButtonNamed(w, Loc.T("Custom.Layout.Done")) != null, "Done in place of the item's actions");
            Check(Descendants(PageOf(w)).OfType<StackPanel>().Count(p => System.Windows.Automation.AutomationProperties.GetName(p) is string n
                && GearLayout(memory).Blocks.Any(b => b.Name == n)) == 7, "a frame per known block");
            foreach (double width in new[] { 560.0, 760, 1100 })
            {
                w.Width = width;
                await Wait(350);
                List<string> cut = CutOff(w).Concat(Clipped(w)).ToList();
                Check(cut.Count == 0, "nothing cut off at " + width + " px" + (cut.Count > 0 ? ": " + string.Join("; ", cut.Take(4)) : ""));
            }
            RenderWindow(w, "custom-layout");

            // Moves: one step each, undone and redone by the window's own Ctrl+Z.
            string start = File.ReadAllText(layoutFile);
            List<string> Order() => GearLayout(memory).Blocks.Select(b => b.Kind).ToList();
            Invoke(w, "MoveBlock", ids["checklist"], -1);
            await Wait(300);
            Check(Order().IndexOf(BlockKinds.Checklist) == 2 && Order().IndexOf(BlockKinds.Notes) == 3, "a block moved up: " + string.Join(", ", Order()));
            Check(UndoHost.Of(w).Undo() && File.ReadAllText(layoutFile) == start, "undone byte for byte");
            await Wait(200);
            Check(Field<bool>(w, "layoutMode"), "still in the editor after an undo");
            Check(UndoHost.Of(w).Redo() && Order().IndexOf(BlockKinds.Checklist) == 2, "redone");
            Invoke(w, "MoveBlock", ids["checklist"], +1);
            Invoke(w, "MoveBlock", ids["log"], +1);
            await Wait(300);
            Check(Order().Last() == "Timeline" && Order()[Order().Count - 2] == BlockKinds.Log, "a block moved past the last known one keeps the unknown one where it is");
            Invoke(w, "MoveBlock", ids["log"], -1);
            await Wait(300);

            // Half and full width.
            RadioButton half = Descendants(PageOf(w)).OfType<RadioButton>().First(r => r.GroupName == "Width" + ids["fields"] && (string)r.Content == Loc.T("Custom.Layout.Half"));
            half.IsChecked = true;
            await Wait(300);
            Check(GearBlock(memory, BlockKinds.Fields).Half, "made half width");
            Descendants(PageOf(w)).OfType<RadioButton>().First(r => r.GroupName == "Width" + ids["fields"] && (string)r.Content == Loc.T("Custom.Layout.Full")).IsChecked = true;
            await Wait(300);
            Check(!GearBlock(memory, BlockKinds.Fields).Half, "and full again");

            // A block renamed; a log's amounts made a number in km.
            AnswerLayoutDialog(main, async dialog =>
            {
                DialogText(dialog, 0).Text = "Equipment";
                await Task.CompletedTask;
            });
            Invoke(w, "EditBlock", ids["fields"]);
            await Wait(300);
            Check(GearBlock(memory, BlockKinds.Fields).Name == "Equipment", "a block renamed");
            AnswerLayoutDialog(main, async dialog =>
            {
                Descendants(dialog).OfType<CheckBox>().First().IsChecked = false;
                await Wait(100);
                DialogText(dialog, 1).Text = "km";
                await Task.CompletedTask;
            });
            Invoke(w, "EditBlock", ids["log"]);
            await Wait(300);
            LayoutBlock log = GearBlock(memory, BlockKinds.Log);
            Check(log.Money == false && log.Unit == "km", "a log's amounts: a number in km");
            RecordDefinition logList = EntityTypes.Records(CustomTypes.TypeOf(memory.Id)).First(r => r.Key == ids["log"]);
            Check(logList.TotalKey == null && logList.Fields.First(f => f.Key == "Amount").Unit == Units.Own("km"), "no money totals, the amounts in km");

            // Fields: one added and put on the card (the third), a fourth can't go on it; one changed.
            AnswerLayoutDialog(main, async dialog =>
            {
                DialogText(dialog, 0).Text = "Serial";
                Descendants(dialog).OfType<CheckBox>().First().IsChecked = true;
                await Task.CompletedTask;
            });
            Invoke(w, "OpenField", ids["fields"], null);
            await Wait(300);
            CustomLayout layout = GearLayout(memory);
            LayoutField serial = layout.Blocks.First(b => b.Kind == BlockKinds.Fields).Fields.FirstOrDefault(f => f.Name == "Serial");
            Check(serial != null && serial.Kind == FieldKinds.Text && layout.CardFields.Count == 3 && layout.CardFields[2] == serial.Id, "a field added at the end, on the card");
            AnswerLayoutDialog(main, async dialog =>
            {
                Check(!Descendants(dialog).OfType<CheckBox>().First().IsEnabled, "a fourth field can't go on the card");
                Check(Descendants(dialog).OfType<TextBlock>().Any(t => t.Text == Loc.T("Custom.Field.CardFull")), "and that is said");
                DialogText(dialog, 0).Text = "Size";
                ComboBox kind = Descendants(dialog).OfType<ComboBox>().First();
                kind.SelectedItem = kind.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == FieldKinds.Choice);
                await Wait(100);
                DialogText(dialog, 1).Text = "S\nM\nL\nM";
                await Task.CompletedTask;
            });
            Invoke(w, "OpenField", ids["fields"], null);
            await Wait(300);
            LayoutField size = GearBlock(memory, BlockKinds.Fields).Fields.FirstOrDefault(f => f.Name == "Size");
            Check(size?.Kind == FieldKinds.Choice && size.Choices.SequenceEqual(new[] { "S", "M", "L" }), "a choice field, its choices once each");
            AnswerLayoutDialog(main, async dialog =>
            {
                DialogText(dialog, 1).Text = "Red\nBlue\nGreen";
                await Task.CompletedTask;
            });
            Invoke(w, "OpenField", ids["fields"], ids["colour"]);
            await Wait(300);
            Check(GearBlock(memory, BlockKinds.Fields).Fields.First(f => f.Id == ids["colour"]).Choices.SequenceEqual(new[] { "Red", "Blue", "Green" }), "a field changed");
            Invoke(w, "MoveField", ids["fields"], size.Id, -1);
            await Wait(300);
            List<string> fieldOrder = GearBlock(memory, BlockKinds.Fields).Fields.Select(f => f.Name).ToList();
            Check(fieldOrder.IndexOf("Size") == fieldOrder.IndexOf("Serial") - 1, "a field moved up: " + string.Join(", ", fieldOrder));

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

            // Answered No: nothing changes.
            answerCancel = true;
            Invoke(w, "RemoveBlock", ids["checklist"]);
            await Wait(400);
            answerCancel = false;
            Check(File.ReadAllText(layoutFile) == layoutBefore && File.ReadAllText(itemsFile) == itemsBefore, "No: nothing removed");

            // A block with data: its records go from the item (one step with the layout); its files only once nothing can
            // bring them back.
            Invoke(w, "RemoveBlock", ids["checklist"]);
            await Wait(400);
            Check(GearLayout(memory).Blocks.All(b => b.Id != ids["checklist"]) && StoredItem(memory, tentId).Records.ContainsKey(ids["checklist"]) == false,
                "a block removed with its lines");
            Check(UndoHost.Of(w).Undo() && File.ReadAllText(layoutFile) == layoutBefore && File.ReadAllText(itemsFile) == itemsBefore, "and brought back");

            // A block nothing is in goes without a question; one added lands at the end.
            boxes = selectBoxes.Count;
            Invoke(w, "AddBlock", BlockKinds.Quantities);
            await Wait(300);
            Check(GearLayout(memory).Blocks.Last().Kind == BlockKinds.Quantities && GearLayout(memory).Blocks.Last().Name == Loc.T("Custom.Block.Quantities"),
                "a block added at the end, named by its kind");
            Invoke(w, "RemoveBlock", GearLayout(memory).Blocks.Last().Id);
            await Wait(300);
            Check(selectBoxes.Count == boxes && GearLayout(memory).Blocks.Count(b => b.Kind == BlockKinds.Quantities) == 1, "an empty block removed without a question");

            ContextMenu menu = (ContextMenu)typeof(ItemWindow).GetMethod("AddBlockMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(w, new object[] { new Button(), CustomTypes.OfMemory(memory) });
            Check(menu.Items.Count == 7 && !menu.Items.OfType<MenuItem>().First(i => System.Windows.Automation.AutomationProperties.GetName(i) == Loc.T("Custom.Kind.Gallery")).IsEnabled,
                "every kind to add, the gallery not twice");

            ClickNow(ButtonNamed(w, Loc.T("Custom.Layout.Done")));
            await Wait(300);
            Check(!Field<bool>(w, "layoutMode") && Sections(w).Any(s => s.Definition.Label == "Equipment"), "Done: the item, with its renamed block");
            Check(Sections(w).First().Definition.Fields.Any(f => f.Label == "Serial"), "and its new field");
        });

        Console.WriteLine("The editor on its own: the collection's menu");
        ContextMenu memoryMenu = (ContextMenu)typeof(MainWindow).GetMethod("MemoryMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(main, new object[] { memory });
        Check(memoryMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header == Loc.T("Custom.Menu.EditLayout")), "Edit layout… in a custom collection's menu");
        Profile shoes = AddMemory(main, "Plain box", ProfileType.Collection);
        ContextMenu plainMenu = (ContextMenu)typeof(MainWindow).GetMethod("MemoryMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Invoke(main, new object[] { shoes });
        Check(!plainMenu.Items.OfType<MenuItem>().Any(i => (string)i.Header == Loc.T("Custom.Menu.EditLayout")), "not in a standard collection's");
        RemoveMemory(main, shoes);
        DriveItem(main, async w =>
        {
            Check(Field<bool>(w, "layoutMode") && w.Title == Loc.F("Custom.Layout.Title", "Gear"), "opened on the layout: " + w.Title);
            Invoke(w, "MoveBlock", ids["log"], -1);
            await Wait(300);
            Check(UndoHost.Of(w).Undo() && w.IsVisible && Field<bool>(w, "layoutMode"), "its own Ctrl+Z, and it stays open");
            PressKey(w, System.Windows.Input.Key.Escape);
            await Wait(300);
            Check(!w.IsVisible, "Esc closes it");
        });
        Invoke(main, "EditLayout", memory);
        await Wait(400);
    }
}
