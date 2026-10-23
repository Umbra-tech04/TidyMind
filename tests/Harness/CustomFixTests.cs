using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClosedXML.Excel;
using TidyMind;

// Harness custom, after the review: a field's kind changed while items hold values in it (kept in the new form, an
// unreadable one asked about and removed, No changes nothing, undone byte for byte); the quantities stepper past its top
// and with decimals; a layout that couldn't be read once is read again; an item of another collection's layout can't
// change this one's; Excel columns of fields named alike; fallback names after a language switch.
static partial class Program
{
    static async Task CustomReviewFixes(MainWindow main)
    {
        Console.WriteLine("Review fixes: a field's kind changed while items hold values");
        UndoService.EndAll();
        Profile memory = Profiles(main).First(p => p.Name == "Gear");
        Dictionary<string, string> ids = GearIds(memory);
        CollectionView view = OpenCollectionView(main, memory);
        await Wait(300);
        Guid tentId = EntityStore.Load(memory).First(e => e.Name == "Tent").Id;
        string layoutFile = MemoryFiles.Layout(memory), itemsFile = MemoryFiles.Items(memory);

        // The field dialog about to open: its kind set to `kind`, then Save (the question it may ask answered No when
        // sayNo); one left open by that No is closed (its "discard changes?" answered Yes).
        void ChangeKind(ItemWindow w, string field, string kind, bool sayNo = false) =>
            _ = main.Dispatcher.BeginInvoke(new Action(async () =>
            {
                EditDialog dialog = null;
                for (int i = 0; i < 50 && dialog == null; i++)
                {
                    await Wait(100);
                    dialog = Application.Current.Windows.OfType<EditDialog>().FirstOrDefault();
                }
                if (dialog == null)
                {
                    Check(false, "the field dialog opened");
                    return;
                }
                await Wait(200);
                ComboBox box = Descendants(dialog).OfType<ComboBox>().First();
                box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(i => (string)i.Tag == kind);
                await Wait(100);
                answerCancel = sayNo;
                ClickNow(ButtonNamed(dialog, Loc.T("Common.Save")));
                await Wait(800);
                answerCancel = false;
                if (dialog.IsVisible)
                    dialog.Close();
            }), System.Windows.Threading.DispatcherPriority.Background);

        await WithVehicle(main, view, tentId, async w =>
        {
            Invoke(w, "EditLayout");
            string layoutBefore = File.ReadAllText(layoutFile), itemsBefore = File.ReadAllText(itemsFile);

            // Number to Text: every value fits, nothing is asked, the value stays.
            int boxes = selectBoxes.Count;
            ChangeKind(w, ids["weight"], FieldKinds.Text);
            Invoke(w, "OpenField", ids["fields"], ids["weight"]);
            await Wait(400);
            Check(selectBoxes.Count == boxes && StoredItem(memory, tentId).Details[ids["weight"]] == "3.25", "Number to Text: kept as it was, nothing asked");
            Check(UndoHost.Of(w).Undo() && File.ReadAllText(layoutFile) == layoutBefore, "undone");
            await Wait(300);

            // Choice to Number: "Blue" can't be a number: asked; No changes nothing.
            ChangeKind(w, ids["colour"], FieldKinds.Number, sayNo: true);
            Invoke(w, "OpenField", ids["fields"], ids["colour"]); // returns once the dialog is closed
            await Wait(300);
            Check(selectBoxes.Skip(boxes).Any(b => b.Contains("Colour")), "asked first, naming it: " + selectBoxes.LastOrDefault());
            Check(File.ReadAllText(layoutFile) == layoutBefore && File.ReadAllText(itemsFile) == itemsBefore, "No: nothing changed");

            // Yes: the kind changes, the value that can't be read as it goes, in one step.
            ChangeKind(w, ids["colour"], FieldKinds.Number);
            Invoke(w, "OpenField", ids["fields"], ids["colour"]);
            await Wait(500);
            Check(GearBlock(memory, BlockKinds.Fields).Fields.First(f => f.Id == ids["colour"]).Kind == FieldKinds.Number
                  && !StoredItem(memory, tentId).Details.ContainsKey(ids["colour"]), "Yes: a number field, the value that wasn't one removed");
            Check(UndoHost.Of(w).Undo() && File.ReadAllText(layoutFile) == layoutBefore && File.ReadAllText(itemsFile) == itemsBefore,
                "Ctrl+Z: both files byte for byte");
            await Wait(300);
            Check(LayoutField.Fit(FieldKinds.Date, "2025-4-1") == "2025-04-01" && LayoutField.Fit(FieldKinds.Number, "1,5") == "1.5"
                  && LayoutField.Fit(FieldKinds.Flag, "yes") == null && LayoutField.Fit(FieldKinds.Text, "true") == "true", "values in a new kind's stored form");
            Invoke(w, "LeaveLayout");
        });

        Console.WriteLine("Review fixes: the stepper past its top and with decimals");
        string quantities = ids["quantities"];
        async Task Step(string start, bool plus, string expected, string what)
        {
            List<Entity> items = EntityStore.Load(memory);
            items.First(e => e.Id == tentId).Records[quantities][0].Set("Quantity", start);
            EntityStore.TrySave(memory, items);
            view.ReloadAfterUndo();
            await WithVehicle(main, view, tentId, async w =>
            {
                CompactRecords table = Descendants(PageOf(w)).OfType<CompactRecords>().Single();
                Button button = Descendants(table).OfType<Button>().First(b => System.Windows.Automation.AutomationProperties.GetName(b)
                    == Loc.T(plus ? "Entity.Records.CustomQuantities.More" : "Entity.Records.CustomQuantities.Fewer"));
                if (button.IsEnabled)
                    ClickNow(button);
                await Wait(400);
                string now = StoredLines(memory, tentId, quantities)[0].Get("Quantity");
                Check(now == expected, what + ": " + start + " → " + now);
            });
        }
        await Step("12500", false, "12499", "an amount past the top goes down by one, not to the top");
        await Step("12500", true, "12500", "and can't go up");
        await Step("1.0625", true, "2.0625", "its decimals stay");
        await Step("2.3", false, "1.3", "no rounding noise");
        await Step("0.5", false, "0", "never below none");
        await Step("12", true, "13", "a whole number as before");

        Console.WriteLine("Review fixes: a layout read again after it couldn't be");
        Profile birds = Profiles(main).First(p => p.Name == "Bird book");
        string birdLayout = File.ReadAllText(MemoryFiles.Layout(birds));
        CustomTypes.ForgetAll();
        File.WriteAllText(MemoryFiles.Layout(birds), "{ locked");
        Check(CustomTypes.OfMemory(birds) == null, "can't be read: no definitions");
        File.WriteAllText(MemoryFiles.Layout(birds), birdLayout);
        Check(CustomTypes.OfMemory(birds) != null, "readable again: read again, not kept as unreadable");

        Console.WriteLine("Review fixes: an item of another collection's layout");
        List<Entity> birdItems = EntityStore.Load(birds);
        Entity stray = new Entity { Id = Guid.NewGuid(), Name = "Stray", EntityType = CustomTypes.TypeOf(memory.Id), TabId = birdItems.FirstOrDefault()?.TabId ?? Guid.NewGuid() };
        birdItems.Add(stray);
        EntityStore.TrySave(birds, birdItems);
        CollectionView birdView = OpenCollectionView(main, birds);
        await Wait(300);
        await WithVehicle(main, birdView, stray.Id, async w =>
        {
            Check(!Descendants(w).OfType<Button>().Any(b => b.Content as string == Loc.T("Custom.EditLayout")), "no Edit layout for it here");
            Invoke(w, "EditLayout");
            Check(!Field<bool>(w, "layoutMode"), "and no editor");
            CustomLayout other = GearLayout(memory);
            bool changed = (bool)typeof(CollectionView).GetMethod("ChangeLayout", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .Invoke(birdView, new object[] { new ItemWindow.LayoutChange(memory.Id, other, Array.Empty<string>(), "x", false, stray.Id) });
            Check(!changed && File.ReadAllText(MemoryFiles.Layout(birds)) == birdLayout, "another layout is never written over this collection's");
            await Task.CompletedTask;
        });
        birdItems.RemoveAll(e => e.Id == stray.Id);
        EntityStore.TrySave(birds, birdItems);

        Console.WriteLine("Review fixes: Excel columns of fields named alike; names after a language switch");
        Profile twins = AddMemory(main, "Twins", ProfileType.Collection);
        string a = CustomLayout.NewId(), b = CustomLayout.NewId();
        CustomLayoutStore.TrySave(twins, new CustomLayout
        {
            Blocks = new List<LayoutBlock>
            {
                new LayoutBlock { Id = CustomLayout.NewId(), Kind = BlockKinds.Fields, Name = "Info", Fields = new List<LayoutField> { new LayoutField { Id = a, Name = "Size", Kind = FieldKinds.Text } } },
                new LayoutBlock { Id = CustomLayout.NewId(), Kind = BlockKinds.Fields, Name = "Info", Fields = new List<LayoutField> { new LayoutField { Id = b, Name = "Size", Kind = FieldKinds.Text } } },
                new LayoutBlock { Id = CustomLayout.NewId(), Kind = BlockKinds.Notes, Name = "" }
            }
        });
        Entity twin = new Entity { Id = Guid.NewGuid(), Name = "Twin", EntityType = CustomTypes.TypeOf(twins.Id), Details = new Dictionary<string, string> { [a] = "Left", [b] = "Right" } };
        File.WriteAllText(MemoryFiles.Items(twins), JsonSerializer.Serialize(new List<Entity> { twin }));
        string xlsx = Path.Combine(Path.GetDirectoryName(AppPaths.DataFolder), "custom-work", "twins.xlsx");
        Directory.CreateDirectory(Path.GetDirectoryName(xlsx));
        ExportService.ExportMemory(twins, xlsx, ExportFormat.Excel);
        using (XLWorkbook workbook = new XLWorkbook(xlsx))
        {
            List<string> cells = workbook.Worksheets.SelectMany(s => s.CellsUsed()).Select(c => c.GetString()).ToList();
            Check(cells.Contains("Size") && cells.Contains("Info › Size") && cells.Contains("Left") && cells.Contains("Right"),
                "two fields named alike: a column each, both values: " + string.Join(" | ", cells.Take(20)));
        }
        Check(CustomTypes.OfMemory(twins).Blocks[2].Name == "" && EntityTypes.Sections(twin.EntityType).Last().Label == Loc.T("Custom.Block.Notes"),
            "a block without a name is called by its kind");
        Loc.Switch(Loc.Hungarian);
        string hungarian = EntityTypes.Sections(twin.EntityType).Last().Label;
        Loc.Switch(Loc.English);
        Check(hungarian == "Jegyzetek", "after a language switch, in the new language: " + hungarian);
        RemoveMemory(main, twins);
    }
}
