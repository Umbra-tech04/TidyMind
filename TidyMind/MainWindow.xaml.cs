using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;

namespace TidyMind
{
    public partial class MainWindow : Window
    {
        private const string HomeKey = "__home__";

        private static readonly string[] MemoryColors =
        {
            "#E74C3C", "#E67E22", "#F1C40F", "#2ECC71", "#3498DB", "#9B59B6"
        };

        private List<Profile> profiles;
        private string selectedKey = HomeKey;
        private readonly DragReorder<Profile> memoryDrag;

        // profiles: the list of memories, already read by App (which stops before this if it can't be read).
        public MainWindow(List<Profile> profiles)
        {
            InitializeComponent();
            this.profiles = profiles;

            // Only within a group: dropping a collection among projects would look like a type change.
            memoryDrag = new DragReorder<Profile>(NavPanel, Orientation.Vertical, 2,
                (dragged, target) => dragged.Type == target.Type, MoveMemory);

            NavigateHome();
        }

        // Native title bar is drawn by the OS, not our XAML — recolor it via DWM
        // so it doesn't visually merge into the light content area below it.
        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ApplyTitleBarTheme();
        }

        private void ApplyTitleBarTheme()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            int darkMode = 1;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

            int captionColor = ToColorRef("#1C1B19");
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));

            int textColor = ToColorRef("#E8E8E6");
            DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref textColor, sizeof(int));
        }

        private static int ToColorRef(string hex)
        {
            Color c = (Color)ColorConverter.ConvertFromString(hex);
            return c.R | (c.G << 8) | (c.B << 16); // COLORREF is 0x00BBGGRR
        }

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

        // ---- Navigation ---------------------------------------------------

        private void NavigateHome()
        {
            selectedKey = HomeKey;
            ContentArea.Content = new HomeView(profiles, GoToSearchResult); // rebuilt (and re-read from disk) on every visit
            BuildNav();
        }

        private void NavigateTo(Profile profile)
        {
            selectedKey = KeyOf(profile);

            if (profile.Type == ProfileType.Collection)
                ContentArea.Content = new CollectionView(profile, () => ProfileManager.TrySaveProfiles(profiles));
            else
                ContentArea.Content = new ProjectView(profile, () => ProfileManager.TrySaveProfiles(profiles));

            BuildNav();
        }

        private void BuildNav()
        {
            NavPanel.Children.Clear();
            memoryDrag.Clear();

            NavPanel.Children.Add(CreateNavItem("🏠", "Home", selectedKey == HomeKey,
                () => NavigateHome(), null, false));

            List<Profile> collections = profiles.Where(p => p.Type == ProfileType.Collection).ToList();
            List<Profile> projectMemories = profiles.Where(p => p.Type == ProfileType.Project).ToList();

            NavPanel.Children.Add(CreateSectionHeader("COLLECTIONS"));
            foreach (Profile profile in collections)
                NavPanel.Children.Add(CreateMemoryItem(profile));
            if (collections.Count == 0)
                NavPanel.Children.Add(CreateEmptyHint("No collections yet"));

            NavPanel.Children.Add(CreateSectionHeader("PROJECTS"));
            foreach (Profile profile in projectMemories)
                NavPanel.Children.Add(CreateMemoryItem(profile));
            if (projectMemories.Count == 0)
                NavPanel.Children.Add(CreateEmptyHint("No projects yet"));
        }

        private Border CreateMemoryItem(Profile profile)
        {
            ContextMenu menu = new ContextMenu();

            MenuItem rename = new MenuItem { Header = "Rename" };
            rename.Click += (s, e) => RenameMemory(profile);
            MenuItem delete = new MenuItem { Header = "Delete" };
            delete.Click += (s, e) => DeleteMemory(profile);
            MenuItem reminder = new MenuItem { Header = "Add Reminder" };
            reminder.Click += (s, e) => new AddReminderWindow(profile.Name).ShowDialog();
            MenuItem export = new MenuItem { Header = "Export" };
            export.Click += (s, e) => ExportMemory(profile);

            menu.Items.Add(rename);
            menu.Items.Add(delete);
            menu.Items.Add(reminder);
            menu.Items.Add(export);
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => profile.Name, "Copy name"));

            Border item = CreateNavItem("📁", profile.Name, selectedKey == KeyOf(profile),
                () => NavigateTo(profile), menu, true);
            memoryDrag.Attach(item, profile);
            return item;
        }

        // Drag-and-drop in the sidebar. The whole list is renumbered 0..n; the other group's relative order is untouched.
        private void MoveMemory(Profile dragged, Profile target, bool after)
        {
            DragReorder<Profile>.Move(profiles, dragged, target, after);
            for (int i = 0; i < profiles.Count; i++)
                profiles[i].Order = i;

            if (!ProfileManager.TrySaveProfiles(profiles))
                WarnProfilesNotSaved("The new order will be lost when TidyMind closes.");
            BuildNav();
        }

        private Border CreateNavItem(string icon, string text, bool active, Action onClick,
            ContextMenu menu, bool indent)
        {
            Border item = new Border
            {
                CornerRadius = new CornerRadius(6),
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(0, 2, 0, 2),
                Cursor = Cursors.Hand,
                Background = active ? B("#2A2926") : Brushes.Transparent,
                ContextMenu = menu
            };

            Grid grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            Brush accent = (Brush)FindResource("Accent");

            Rectangle bar = new Rectangle
            {
                Width = 3,
                Fill = accent,
                RadiusX = 2,
                RadiusY = 2,
                Visibility = active ? Visibility.Visible : Visibility.Hidden
            };
            Grid.SetColumn(bar, 0);
            grid.Children.Add(bar);

            StackPanel row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(indent ? 14 : 9, 7, 8, 7)
            };
            TextBlock iconBlock = new TextBlock
            {
                Text = icon,
                FontSize = 13,
                Foreground = active ? accent : B("#E8E8E6"),
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            TextBlock textBlock = new TextBlock
            {
                Text = text,
                FontSize = 13,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = active ? accent : B("#E8E8E6"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 150
            };
            row.Children.Add(iconBlock);
            row.Children.Add(textBlock);
            Grid.SetColumn(row, 1);
            grid.Children.Add(row);

            item.Child = grid;

            // Press and release on the same item: a drag that ends over another item doesn't open it.
            CardEffects.AttachClick(item, onClick);
            if (!active)
            {
                item.MouseEnter += (s, e) => item.Background = B("#2A2926");
                item.MouseLeave += (s, e) => item.Background = Brushes.Transparent;
            }

            return item;
        }

        private TextBlock CreateSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = B("#8F8F8B"),
                Margin = new Thickness(12, 18, 0, 6)
            };
        }

        private TextBlock CreateEmptyHint(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11,
                Foreground = B("#6A6A67"),
                FontStyle = FontStyles.Italic,
                Margin = new Thickness(23, 2, 0, 2)
            };
        }

        // ---- Memory operations --------------------------------------------

        private void NewProjectButton_Click(object sender, RoutedEventArgs e)
        {
            AddProfile(ProfileType.Project);
        }

        private void NewCollectionButton_Click(object sender, RoutedEventArgs e)
        {
            AddProfile(ProfileType.Collection);
        }

        private void AddProfile(ProfileType type)
        {
            string name = InputDialog.Prompt(this,
                type == ProfileType.Collection ? "New Collection" : "New Project",
                "Give your new " + TypeLabel(type) + " a name.");
            if (string.IsNullOrWhiteSpace(name)) return;

            if (NameTaken(name, type))
            {
                MessageBox.Show("A " + TypeLabel(type) + " with that name already exists.", "New Memory",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            Profile profile = new Profile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Color = MemoryColors[profiles.Count % MemoryColors.Length],
                Type = type,
                Order = profiles.Count == 0 ? 0 : profiles.Max(p => p.Order) + 1
            };
            profiles.Add(profile);
            if (!ProfileManager.TrySaveProfiles(profiles))
            {
                profiles.Remove(profile);
                WarnProfilesNotSaved("The new " + TypeLabel(type) + " wasn't added.");
                return;
            }

            NavigateTo(profile); // the view creates the memory's folder and first tab
        }

        // Only the name changes: the memory's files are in a folder named by its Id, so nothing on disk moves.
        private void RenameMemory(Profile profile)
        {
            string oldName = profile.Name;
            string newName = InputDialog.Prompt(this,
                profile.Type == ProfileType.Collection ? "Rename Collection" : "Rename Project",
                "Enter a new name.", oldName, "Rename");

            if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

            if (NameTaken(newName, profile.Type, except: profile))
            {
                MessageBox.Show("A " + TypeLabel(profile.Type) + " with that name already exists.", "Rename Memory",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            profile.Name = newName;
            if (!ProfileManager.TrySaveProfiles(profiles))
            {
                profile.Name = oldName;
                WarnProfilesNotSaved("The name wasn't changed.");
                return;
            }
            NavigateTo(profile);
        }

        // The memory leaves profiles.json first; only once that's saved are its folder and the pictures and attached
        // files it owns deleted (best effort: one that can't be deleted is left behind, unused). If the save fails,
        // nothing is deleted.
        private void DeleteMemory(Profile profile)
        {
            MessageBoxResult result = MessageBox.Show(
                "Delete '" + profile.Name + "'? This will delete all its data.",
                "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            int index = profiles.IndexOf(profile);
            profiles.Remove(profile);
            if (!ProfileManager.TrySaveProfiles(profiles))
            {
                profiles.Insert(index, profile);
                WarnProfilesNotSaved("Nothing was deleted.");
                return;
            }

            DeleteOwnedFiles(profile);
            MemoryFiles.DeleteFolder(profile);

            if (selectedKey == KeyOf(profile))
                NavigateHome();
            else
            {
                BuildNav();
                (ContentArea.Content as HomeView)?.Refresh(); // the deleted memory's projects/items were on it
            }
        }

        private static void WarnProfilesNotSaved(string consequence)
        {
            MessageBox.Show("Couldn't save the list of memories: this file can't be written:\n"
                + AppPaths.Data("profiles.json") + "\n\n" + consequence, "Couldn't Save",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // Names are only labels now, but two memories of a kind told apart only by capitals would be confusing.
        // A Project and a Collection may share a name.
        private bool NameTaken(string name, ProfileType type, Profile except = null)
        {
            return profiles.Any(p => p != except && p.Type == type && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // By Id: stays the same across a rename.
        private static string KeyOf(Profile profile)
        {
            return profile.Id.ToString("N");
        }

        private static string TypeLabel(ProfileType type)
        {
            return type == ProfileType.Collection ? "collection" : "project";
        }

        // The page background, card pictures, attached files and item pictures of a memory being deleted, kept in
        // the app-wide folders, so they aren't left behind there. An unreadable file just means nothing to clean up.
        private static void DeleteOwnedFiles(Profile memory)
        {
            if (memory.BackgroundType == BackgroundFill.Image)
                ImageStore.Backgrounds.Delete(memory.BackgroundImagePath);

            if (memory.Type == ProfileType.Collection)
            {
                foreach (Entity entity in EntityStore.LoadOrEmpty(memory))
                    EntityImages.DeleteCopy(entity.ImagePath);
                return;
            }

            List<Project> projects = ProjectStore.LoadOrEmpty(memory);
            foreach (Project project in projects.Where(p => p.CardBackgroundType == BackgroundFill.Image))
                ImageStore.Cards.Delete(project.CardBackgroundImagePath);
            AttachmentStore.Delete(projects.SelectMany(p => p.Attachments ?? new List<Attachment>()));
        }

        // ---- Export -------------------------------------------------------

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow settings = new SettingsWindow(ExportAll);
            settings.ShowDialog();
        }

        private void ExportMemory(Profile profile)
        {
            ExportFormatWindow formatWindow = new ExportFormatWindow();
            if (formatWindow.ShowDialog() != true) return;

            ExportFormat format = formatWindow.SelectedFormat;
            ExportMenu.SaveAs(format, profile.Name, path => ExportService.ExportMemory(profile, path, format));
        }

        private void ExportAll()
        {
            if (profiles.Count == 0)
            {
                MessageBox.Show("There are no memories to export.", "Export",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ExportFormatWindow formatWindow = new ExportFormatWindow();
            if (formatWindow.ShowDialog() != true) return;

            ExportFormat format = formatWindow.SelectedFormat;
            ExportMenu.SaveAs(format, "TidyMind", path => ExportService.ExportAll(profiles, path, format));
        }

        // ---- Global search -----------------------------------------------

        // Opening/closing goes through OverlayManager, which keeps only one overlay open at a time.
        public bool IsSearchOpen => SearchOverlay.IsOpen;

        public void OpenSearch()
        {
            Activate();
            SearchOverlay.Open(SearchIndexBuilder.Build(profiles), GoToSearchResult);
        }

        public void CloseSearch() => SearchOverlay.Close(true);

        // ---- Calendar -----------------------------------------------------

        public bool IsCalendarOpen => CalendarPanel.IsOpen;

        public void OpenCalendar()
        {
            Activate();
            CalendarPanel.Open();
        }

        public void CloseCalendar() => CalendarPanel.Close();

        public void ShowCalendarDate(DateTime date) => CalendarPanel.ShowDate(date);

        private void CalendarIconButton_Click(object sender, RoutedEventArgs e) => OverlayManager.Route(Overlay.Calendar);

        private void GoToSearchResult(SearchResult result)
        {
            NavigateTo(result.Profile);
            if (result.Kind == SearchResultKind.Memory) return;

            // After the new view has loaded, so a project opens over its visible card (and can pulse on close).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ContentArea.Content is ProjectView projectView)
                    projectView.Reveal(result.ItemId);
                else if (ContentArea.Content is CollectionView collectionView)
                    collectionView.Reveal(result.ItemId);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---- Corner utilities ---------------------------------------------

        private void QuickNotesButton_Click(object sender, RoutedEventArgs e) => OverlayManager.Route(Overlay.QuickNotes);

        private void RemindersIconButton_Click(object sender, RoutedEventArgs e) => OverlayManager.Route(Overlay.Reminders);

        private void HelpButton_Click(object sender, RoutedEventArgs e) => HelpPopup.IsOpen = !HelpPopup.IsOpen;

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
