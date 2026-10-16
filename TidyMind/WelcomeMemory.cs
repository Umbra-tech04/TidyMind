using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace TidyMind
{
    // The guided tour's example data: a project memory and a small collection, both called "Welcome" (in the language
    // shown when they're made) and marked Profile.IsSample. Written through the normal stores, so search, export, backup and delete treat them like any
    // other memory. They are written in the language shown when they are created and are the user's data from then
    // on: a later language switch doesn't translate them. Choice values (Material, Season, the vehicle's Kind) are
    // stored in English like every item's, and shown in the current language.
    public static class WelcomeMemory
    {
        public static Profile? Find(IEnumerable<Profile> profiles, ProfileType type) =>
            profiles.FirstOrDefault(p => p.IsSample == true && p.Type == type);

        public static bool Exists(IEnumerable<Profile> profiles) =>
            Find(profiles, ProfileType.Project) != null && Find(profiles, ProfileType.Collection) != null;

        // The example project with notes on two pages (the tour's notebook step), or null if it's gone.
        public static Guid? NotebookProject(Profile memory) =>
            ProjectStore.LoadOrEmpty(memory).FirstOrDefault(p => p.NotePages != null && p.NotePages.Count > 1)?.Id;

        // Adds whichever of the two is missing. Each memory's files are written first and profiles.json last; if that
        // fails, the new folders and picture are removed again and the list of memories is as it was.
        public static bool TryCreate(List<Profile> profiles, Func<string> nextColor)
        {
            List<Profile> added = new List<Profile>();
            List<string> pictures = new List<string>();
            int order = profiles.Count == 0 ? 0 : profiles.Max(p => p.Order) + 1;

            foreach (ProfileType type in new[] { ProfileType.Project, ProfileType.Collection })
            {
                if (Find(profiles, type) != null)
                    continue;

                Profile memory = new Profile
                {
                    Id = Guid.NewGuid(),
                    Name = Loc.T("Welcome.Name"),
                    Color = nextColor(),
                    Type = type,
                    Order = order++,
                    IsSample = true
                };
                added.Add(memory);
                bool written = type == ProfileType.Project ? WriteProjects(memory, pictures) : WriteCollection(memory);
                if (!written)
                    return Undo(profiles, added, pictures);
                profiles.Add(memory); // here, so nextColor gives the next one its own color
            }

            if (added.Count == 0 || ProfileManager.TrySaveProfiles(profiles))
                return true;
            return Undo(profiles, added, pictures);
        }

        private static bool Undo(List<Profile> profiles, List<Profile> added, List<string> pictures)
        {
            foreach (Profile memory in added)
            {
                profiles.Remove(memory);
                MemoryFiles.DeleteFolder(memory);
            }
            foreach (string picture in pictures)
                ImageStore.Cards.Delete(picture);
            return false;
        }

        private static bool WriteProjects(Profile memory, List<string> pictures)
        {
            MemoryTab start = new MemoryTab { Id = Guid.NewGuid(), Name = Loc.T("Welcome.Tab.Start"), Order = 0 };
            MemoryTab home = new MemoryTab { Id = Guid.NewGuid(), Name = Loc.T("Welcome.Tab.Home"), Order = 1 };

            string? picture = MakeCardPicture();
            if (picture != null)
                pictures.Add(picture);

            DateTime now = DateTime.Now;
            List<Project> projects = new List<Project>
            {
                new Project
                {
                    Id = Guid.NewGuid(), Name = Loc.T("Welcome.Notebook.Name"), TabId = start.Id, Order = 0, LastModified = now,
                    Description = Loc.T("Welcome.Notebook.Description"),
                    Tasks = Tasks((Loc.T("Welcome.Notebook.Task1"), true), (Loc.T("Welcome.Notebook.Task2"), false)),
                    NotePages = new List<NotePage>
                    {
                        new NotePage { Body = Loc.T("Welcome.Notebook.Page1") },
                        new NotePage { Body = Loc.T("Welcome.Notebook.Page2") }
                    }
                },
                new Project
                {
                    Id = Guid.NewGuid(), Name = Loc.T("Welcome.Trip.Name"), TabId = start.Id, Order = 1, LastModified = now,
                    Tasks = Tasks((Loc.T("Welcome.Trip.Task1"), true), (Loc.T("Welcome.Trip.Task2"), true), (Loc.T("Welcome.Trip.Task3"), false),
                        (Loc.T("Welcome.Trip.Task4"), false)),
                    CardBackgroundType = BackgroundFill.Color, CardBackgroundColor = "#1F6B6B"
                },
                new Project
                {
                    Id = Guid.NewGuid(), Name = Loc.T("Welcome.Reading.Name"), TabId = start.Id, Order = 2, LastModified = now,
                    Tasks = Tasks((Loc.T("Welcome.Reading.Task1"), false)),
                    CardBackgroundType = picture != null ? BackgroundFill.Image : BackgroundFill.None,
                    CardBackgroundImagePath = picture
                },
                new Project
                {
                    Id = Guid.NewGuid(), Name = Loc.T("Welcome.Bike.Name"), TabId = home.Id, Order = 3, LastModified = now,
                    Tasks = Tasks((Loc.T("Welcome.Bike.Task1"), false), (Loc.T("Welcome.Bike.Task2"), false)),
                    CardBackgroundType = BackgroundFill.Color, CardBackgroundColor = "#C1562F"
                }
            };

            List<TodoEntry> todos = new List<TodoEntry>
            {
                new TodoEntry { Id = Guid.NewGuid(), Text = Loc.T("Welcome.Todo1") },
                new TodoEntry { Id = Guid.NewGuid(), Text = Loc.T("Welcome.Todo2") },
                new TodoEntry { Id = Guid.NewGuid(), Text = Loc.T("Welcome.Todo3") }
            };

            return TabStore.TrySave(memory, new List<MemoryTab> { start, home })
                && MemoryTodoService.TrySave(memory, todos)
                && ProjectStore.SaveOrWarn(memory, projects);
        }

        private static bool WriteCollection(Profile memory)
        {
            MemoryTab things = new MemoryTab { Id = Guid.NewGuid(), Name = Loc.T("Welcome.Tab.Things"), Order = 0 };
            DateTime now = DateTime.Now;
            List<Entity> items = new List<Entity>
            {
                new Entity
                {
                    Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = Loc.T("Welcome.Jacket.Name"), TabId = things.Id, Order = 0,
                    CreatedDate = now, Color = Loc.T("Welcome.Jacket.Color"), Material = "Polyester", Season = "Spring/Autumn",
                    Notes = Loc.T("Welcome.Jacket.Notes"),
                    Sizes = new List<SizeQuantity> { new SizeQuantity { Size = "M", Quantity = 1 } }
                },
                new Entity
                {
                    Id = Guid.NewGuid(), EntityType = EntityTypes.Clothing, Name = Loc.T("Welcome.Sweater.Name"), TabId = things.Id, Order = 1,
                    CreatedDate = now, Color = Loc.T("Welcome.Sweater.Color"), Material = "Wool", Season = "Winter",
                    Sizes = new List<SizeQuantity> { new SizeQuantity { Size = "L", Quantity = 2 } }
                },
                // The last item: the tour's search step suggests the last word of its name (MainWindow.SampleSearchWord).
                new Entity
                {
                    Id = Guid.NewGuid(), EntityType = EntityTypes.Vehicle, Name = Loc.T("Welcome.Bike.Item"), TabId = things.Id, Order = 2,
                    CreatedDate = now, VehicleKind = "Bicycle", Year = 2021,
                    Notes = Loc.T("Welcome.Bike.ItemNotes")
                }
            };

            return TabStore.TrySave(memory, new List<MemoryTab> { things })
                && EntityStore.SaveOrWarn(memory, items);
        }

        private static List<TaskItem> Tasks(params (string Title, bool Done)[] tasks) =>
            tasks.Select(t => new TaskItem { Title = t.Title, IsDone = t.Done }).ToList();

        // A soft two-colour picture drawn here, so the example needs no download and no licence. Null if it couldn't
        // be made; the card then just keeps the plain look.
        private static string? MakeCardPicture()
        {
            const int size = 400;
            string temp = Path.Combine(Path.GetTempPath(), "tidymind-welcome-" + Guid.NewGuid().ToString("N") + ".png");
            try
            {
                DrawingVisual drawing = new DrawingVisual();
                using (DrawingContext dc = drawing.RenderOpen())
                {
                    dc.DrawRectangle(new LinearGradientBrush(Color.FromRgb(0x2C, 0x4A, 0x6E), Color.FromRgb(0x6B, 0x3B, 0x5E), 45),
                        null, new Rect(0, 0, size, size));
                    Brush glow = new SolidColorBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
                    dc.DrawEllipse(glow, null, new Point(300, 100), 110, 110);
                    dc.DrawEllipse(glow, null, new Point(80, 330), 150, 150);
                }
                RenderTargetBitmap bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(drawing);
                PngBitmapEncoder encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (FileStream file = File.Create(temp))
                    encoder.Save(file);
                return ImageStore.Cards.Import(temp);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return null;
            }
            finally
            {
                try { File.Delete(temp); } catch (Exception) { /* best effort: a stray file in the temp folder */ }
            }
        }
    }
}
