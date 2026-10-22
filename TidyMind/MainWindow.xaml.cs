using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        private const string DayPlanKey = "__dayplan__";

        private static readonly string[] MemoryColors =
        {
            "#E74C3C", "#E67E22", "#F1C40F", "#2ECC71", "#3498DB", "#9B59B6"
        };

        private List<Profile> profiles;
        private string selectedKey = HomeKey;
        private readonly DragReorder<Profile> memoryDrag;
        private readonly ItemSelection<Profile> memorySelection;

        // profiles: the list of memories, already read by App (which stops before this if it can't be read).
        public MainWindow(List<Profile> profiles)
        {
            InitializeComponent();
            this.profiles = profiles;

            // Only within a group: dropping a collection among projects would look like a type change.
            memoryDrag = new DragReorder<Profile>(NavPanel, Orientation.Vertical, 2,
                (dragged, target) => dragged.Type == target.Type, MoveMemory);
            memorySelection = new ItemSelection<Profile>(NavScroll, DeleteMemories, PaintNavItem);
            memorySelection.ClearOnBlankClick(NavScroll);
            memorySelection.SelectionChanged += ShowNavSelection;
            ShowLanguage();
            // The sidebar and the switch are drawn here; each page and window follows the switch on its own.
            Loc.Track(this, () =>
            {
                ShowLanguage();
                BuildNav();
                ShowUpdateIndicator();
            });
            Updates.AvailableChanged += ShowUpdateIndicator;
            Closed += (s, e) => Updates.AvailableChanged -= ShowUpdateIndicator;
            ShowUpdateIndicator();

            // Ctrl+Z here: the page's last step or the sidebar's, whichever is newer. Not while the tour runs.
            Undo = UndoHost.Attach(this, () => new[] { UndoService.Page, UndoService.App }, AfterUndo,
                ready: () => GuidedTour.Current == null);

            NavigateHome();

            // A fresh install (no memories, the tour never offered): the welcome card, once the window is up. Otherwise,
            // after an update, "What's new" once (WhatsNew notes the version either way).
            bool fresh = profiles.Count == 0 && AppSettings.Load().Tour == null;
            Loaded += (s, e) =>
            {
                if (fresh)
                    GuidedTour.Run(new[] { TourSteps.Welcome }, TourEnded);
                Dispatcher.BeginInvoke(new Action(() => WhatsNew.ShowIfNew(this, profiles.Count > 0)), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
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

        // ---- Language -----------------------------------------------------

        private void ShowLanguage()
        {
            LanguageCode.Text = Loc.CurrentOption.ShortName;
            AutomationProperties.SetName(LanguageButton, Loc.F("Language.Button.Name", Loc.CurrentOption.Name));
        }

        // The three languages, each in its own language, the current one ticked; a click switches at once. Opened from
        // the keyboard too (Enter or Space on the button), with the arrow keys and Esc working in the list.
        private void LanguageButton_Click(object sender, RoutedEventArgs e)
        {
            ContextMenu menu = new ContextMenu
            {
                PlacementTarget = LanguageButton,
                Placement = PlacementMode.Top
            };
            foreach (LanguageOption language in Loc.Languages)
            {
                MenuItem item = new MenuItem
                {
                    Header = language.Name,
                    IsChecked = language.Code == Loc.Current
                };
                item.Click += (s, args) =>
                {
                    if (language.Code != Loc.Current && !Loc.Switch(language.Code))
                        Msg.Show(this, Loc.T("Settings.Language.NotSaved"), Loc.T("Settings.Language.Title"),
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // ---- Update available -------------------------------------------

        // The sidebar button for a newer version (Updates.Available). Showing it never moves the focus.
        private void ShowUpdateIndicator()
        {
            UpdateRelease? release = Updates.Available;
            UpdateIndicator.Visibility = release == null ? Visibility.Collapsed : Visibility.Visible;
            if (release == null)
                return;
            string version = release.Version.ToString(3);
            UpdateIndicatorText.Text = Loc.F("Update.Indicator", version);
            UpdateIndicator.ToolTip = Loc.F("Update.Indicator.Tip", version);
            AutomationProperties.SetName(UpdateIndicator, UpdateIndicatorText.Text);
        }

        // The same dialog as the automatic one: Update now, Later, Skip this version.
        private void UpdateIndicator_Click(object sender, RoutedEventArgs e)
        {
            if (Updates.Available != null)
                Updates.Offer(this, Updates.Available);
        }

        // ---- Navigation ---------------------------------------------------

        // Each page has its own undo history (UndoService.Page): going to another page ends the one before.
        private void NavigateHome()
        {
            selectedKey = HomeKey;
            UndoService.ShowPage(selectedKey);
            ContentArea.Content = new HomeView(profiles, GoToSearchResult, () => ShowDayPlan(DateTime.Today)); // rebuilt (and re-read from disk) on every visit
            BuildNav();
        }

        // That day's plan; `select`: a block to bring into view (a search result). The calendar closes, so the
        // plan isn't left behind it.
        public void ShowDayPlan(DateTime date, Guid? select = null)
        {
            CloseCalendar();
            selectedKey = DayPlanKey;
            UndoService.ShowPage(selectedKey);
            ContentArea.Content = new DayPlanView(profiles, date, select);
            BuildNav();
        }

        private void NavigateTo(Profile profile)
        {
            selectedKey = KeyOf(profile);
            UndoService.ShowPage(selectedKey);

            // The page's ⋯ button opens the same menu as the memory's right-click in the sidebar.
            if (profile.Type == ProfileType.Collection)
                ContentArea.Content = new CollectionView(profile, () => ProfileManager.TrySaveProfiles(profiles)) { MemoryMenu = () => MemoryMenu(profile) };
            else
                ContentArea.Content = new ProjectView(profile, () => ProfileManager.TrySaveProfiles(profiles)) { MemoryMenu = () => MemoryMenu(profile) };

            BuildNav();
        }

        private void BuildNav()
        {
            NavPanel.Children.Clear();
            memoryDrag.Clear();
            memorySelection.BeginRender();

            NavPanel.Children.Add(CreateNavItem("🏠", Loc.T("Nav.Home"), selectedKey == HomeKey,
                () => NavigateHome(), null, false));
            Border dayPlan = CreateNavItem("🗓", Loc.T("Nav.DayPlan"), selectedKey == DayPlanKey,
                () => ShowDayPlan(DateTime.Today), null, false);
            NavPanel.Children.Add(dayPlan);
            // Search has its shortcut (Ctrl+Shift+F); this is where it can be seen. The guided tour points at it.
            Border search = CreateNavItem("🔍", Loc.T("Nav.Search"), false, () => OverlayManager.Route(Overlay.Search), null, false);
            search.Name = "NavSearch";
            search.ToolTip = Loc.T("Nav.SearchTip");
            AutomationProperties.SetName(search, Loc.T("Nav.Search"));
            NavPanel.Children.Add(search);

            List<Profile> collections = profiles.Where(p => p.Type == ProfileType.Collection).ToList();
            List<Profile> projectMemories = profiles.Where(p => p.Type == ProfileType.Project).ToList();

            NavPanel.Children.Add(CreateSectionHeader(Loc.T("Nav.Collections")));
            foreach (Profile profile in collections)
                NavPanel.Children.Add(CreateMemoryItem(profile));
            if (collections.Count == 0)
                NavPanel.Children.Add(CreateEmptyHint(Loc.T("Nav.NoCollections")));

            NavPanel.Children.Add(CreateSectionHeader(Loc.T("Nav.Projects")));
            foreach (Profile profile in projectMemories)
                NavPanel.Children.Add(CreateMemoryItem(profile));
            if (projectMemories.Count == 0)
                NavPanel.Children.Add(CreateEmptyHint(Loc.T("Nav.NoProjects")));

            memorySelection.EndRender();
            ShowNavSelection();
        }

        // ---- Several memories at once ---------------------------------------

        // The bar above the sidebar's buttons while any memory is selected.
        private void ShowNavSelection()
        {
            NavSelectionCount.Text = Loc.F("Selection.Count", memorySelection.Count);
            NavSelectionBar.Visibility = memorySelection.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void NavDeleteSelected_Click(object sender, RoutedEventArgs e) => memorySelection.DeleteSelected();

        private void NavClearSelection_Click(object sender, RoutedEventArgs e) => memorySelection.ClearSelection();

        // A selected memory: the accent fill in place of the open or hovered look (CreateNavItem's hover leaves it).
        private void PaintNavItem(FrameworkElement element, bool on)
        {
            Border item = (Border)element;
            if (on)
                item.Background = SelectionLook.SidebarFill;
            else
                item.Background = item.Name == "Nav" + selectedKey || item.IsMouseOver ? B("#2A2926") : Brushes.Transparent;
        }

        // One question for all of them (a stronger one: each takes everything in it along), then the same delete as one.
        private bool DeleteMemories(IReadOnlyList<Profile> doomed)
        {
            if (doomed.Count == 1)
            {
                DeleteMemory(doomed[0]); // one selected: the question for one, as from its menu
                return !profiles.Contains(doomed[0]);
            }
            string warning = Loc.T("Bulk.MemoriesWarning")
                + (doomed.Any(p => p.IsSample == true) ? "\n\n" + Loc.T("Bulk.SampleAmong") : "");
            if (!BulkDelete.Confirm(this, "Bulk.Delete.Memories", doomed.Select(p => p.Name).ToList(), warning, serious: true))
                return false;
            return RemoveMemories(doomed);
        }

        private Border CreateMemoryItem(Profile profile)
        {
            Border item = CreateNavItem("📁", profile.Name, selectedKey == KeyOf(profile),
                () => NavigateTo(profile), MemoryMenu(profile), true);
            item.Name = "Nav" + KeyOf(profile);
            memorySelection.Attach(item, profile); // before the drag: a Ctrl or Shift click selects, and neither opens nor drags
            memoryDrag.Attach(item, profile);
            return item;
        }

        // A memory's menu: its right-click in the sidebar and the ⋯ button on its page.
        private ContextMenu MemoryMenu(Profile profile)
        {
            ContextMenu menu = new ContextMenu();

            MenuItem rename = new MenuItem { Header = Loc.T("Common.Rename") };
            rename.Click += (s, e) => RenameMemory(profile);
            MenuItem delete = new MenuItem { Header = Loc.T("Common.Delete") };
            delete.Click += (s, e) => DeleteMemory(profile);
            MenuItem reminder = new MenuItem { Header = Loc.T("Common.AddReminder") };
            reminder.Click += (s, e) => new AddReminderWindow(profile.Name).ShowDialog();
            MenuItem export = new MenuItem { Header = Loc.T("Memory.Menu.Export") };
            export.Click += (s, e) => ExportMemory(profile);

            menu.Items.Add(rename);
            menu.Items.Add(delete);
            menu.Items.Add(reminder);
            menu.Items.Add(export);
            if (profile.Type == ProfileType.Collection)
                menu.Items.Add(TransferUI.MenuEntry(() => TransferUI.ExportCollection(this, profile)));
            if (profile.Type == ProfileType.Collection && System.IO.File.Exists(MemoryFiles.Layout(profile)))
            {
                MenuItem layout = new MenuItem { Header = Loc.T("Custom.Menu.EditLayout") };
                layout.Click += (s, e) => EditLayout(profile);
                menu.Items.Add(layout);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => profile.Name, Loc.T("Common.CopyName")));
            return menu;
        }

        // Drag-and-drop in the sidebar. The whole list is renumbered 0..n; the other group's relative order is untouched.
        private void MoveMemory(Profile dragged, Profile target, bool after)
        {
            using UndoChange change = UndoChange.Begin(UndoService.App, UndoParts.Profiles());
            DragReorder<Profile>.Move(profiles, dragged, target, after);
            for (int i = 0; i < profiles.Count; i++)
                profiles[i].Order = i;

            if (!ProfileManager.TrySaveProfiles(profiles))
                WarnProfilesNotSaved(Loc.T("Memory.NotSaved.Order"));
            change.Commit(Loc.F("Undo.Moved", dragged.Name));
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
                // A selected item keeps its selected fill (PaintNavItem).
                item.MouseEnter += (s, e) => { if (item.Background != SelectionLook.SidebarFill) item.Background = B("#2A2926"); };
                item.MouseLeave += (s, e) => { if (item.Background != SelectionLook.SidebarFill) item.Background = Brushes.Transparent; };
            }

            return item;
        }

        private TextBlock CreateSectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text.ToUpper(Loc.Culture),
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
            bool collection = type == ProfileType.Collection;
            string name = InputDialog.Prompt(this,
                Loc.T(collection ? "Main.NewCollection" : "Main.NewProject"),
                Loc.T(collection ? "Memory.New.CollectionPrompt" : "Memory.New.ProjectPrompt"));
            if (string.IsNullOrWhiteSpace(name)) return;

            if (NameTaken(name, type))
            {
                Msg.Show(NameTakenText(type), Loc.T("Memory.New.Caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // A collection holds the ready-made types, or items laid out the user's own way (a custom collection).
            bool custom = false;
            if (collection)
            {
                CollectionKindDialog kind = new CollectionKindDialog { Owner = this };
                if (kind.ShowDialog() != true)
                    return;
                custom = kind.Custom;
            }

            Profile profile = new Profile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Color = NextColor(),
                Type = type,
                Order = profiles.Count == 0 ? 0 : profiles.Max(p => p.Order) + 1
            };
            // A custom collection's layout is written first: a collection without it would be an ordinary one.
            if (custom && !CustomLayoutStore.TrySave(profile, CustomLayout.Starter()))
            {
                Msg.Show(Loc.F("Custom.Layout.NotCreated", MemoryFiles.Layout(profile)), Loc.T("Common.CouldntSave"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                MemoryFiles.DeleteFolder(profile);
                return;
            }
            profiles.Add(profile);
            if (!ProfileManager.TrySaveProfiles(profiles))
            {
                profiles.Remove(profile);
                if (custom)
                    MemoryFiles.DeleteFolder(profile);
                WarnProfilesNotSaved(Loc.T(collection ? "Memory.NotSaved.CollectionNotAdded" : "Memory.NotSaved.ProjectNotAdded"));
                return;
            }

            NavigateTo(profile); // the view creates the memory's folder and first tab
            if (custom)
                EditLayout(profile); // a new custom collection starts with its layout
        }

        // A custom collection's layout editor, over its page (shown first).
        private void EditLayout(Profile profile)
        {
            if (selectedKey != KeyOf(profile))
                NavigateTo(profile);
            (ContentArea.Content as CollectionView)?.EditLayout();
        }

        private string NextColor() => MemoryColors[profiles.Count % MemoryColors.Length];

        // Only the name changes: the memory's files are in a folder named by its Id, so nothing on disk moves.
        private void RenameMemory(Profile profile)
        {
            string oldName = profile.Name;
            string newName = InputDialog.Prompt(this,
                Loc.T(profile.Type == ProfileType.Collection ? "Memory.Rename.CollectionTitle" : "Memory.Rename.ProjectTitle"),
                Loc.T("Common.RenamePrompt"), oldName, Loc.T("Common.Rename"));

            if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

            if (NameTaken(newName, profile.Type, except: profile))
            {
                Msg.Show(NameTakenText(profile.Type), Loc.T("Memory.Rename.Caption"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            using UndoChange change = UndoChange.Begin(UndoService.App, UndoParts.Profiles());
            profile.Name = newName;
            if (!ProfileManager.TrySaveProfiles(profiles))
            {
                profile.Name = oldName;
                WarnProfilesNotSaved(Loc.T("Memory.NotSaved.NameUnchanged"));
                return;
            }
            change.Commit(Loc.F("Undo.RenamedTo", oldName, newName));
            NavigateTo(profile);
        }

        private void DeleteMemory(Profile profile)
        {
            MessageBoxResult result = Msg.Show(
                Loc.F("Memory.Delete.Confirm", profile.Name)
                + (profile.IsSample == true ? "\n\n" + Loc.T("Examples.DeleteNote") : ""),
                Loc.T("Common.ConfirmDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
                RemoveMemories(new[] { profile });
        }

        // One memory (its menu) or several (a selection). They leave profiles.json first, in one save; only once that
        // worked are their folders and the pictures and attached files they own deleted (best effort: one that can't
        // be deleted is left behind, unused). If the save fails, nothing is deleted (false). The open one among them: Home.
        // The delete is one undo step for the rest of the session: until it ends, the folders and files stay on disk
        // (UndoService holds them, the deletes below skip them) and Ctrl+Z puts the memories back in the list.
        private bool RemoveMemories(IReadOnlyList<Profile> doomed)
        {
            using UndoChange change = UndoChange.Begin(UndoService.App, UndoParts.Profiles(), UndoParts.Reminders());
            HashSet<Guid> known = new HashSet<Guid>();
            foreach (Profile profile in doomed)
            {
                change.Hold(UndoFiles.OfMemory(profile, out List<Guid> reminders));
                change.WatchReminders(reminders);
                known.UnionWith(reminders);
            }
            change.AfterMove = new MemoriesGained(doomed.ToList(), known).Follow;
            List<Profile> before = profiles.ToList();
            profiles.RemoveAll(doomed.Contains);
            if (!ProfileManager.TrySaveProfiles(profiles))
            {
                profiles.Clear();
                profiles.AddRange(before);
                WarnProfilesNotSaved(Loc.T("Memory.NotSaved.NothingDeleted"));
                return false;
            }

            foreach (Profile profile in doomed)
            {
                DeleteOwnedFiles(profile);
                MemoryFiles.DeleteFolder(profile);
            }
            change.Commit(doomed.Count == 1 ? Loc.F("Undo.Deleted", doomed[0].Name) : Loc.Plural("Undo.Deleted.Memories", doomed.Count),
                destructive: true);

            if (doomed.Any(p => selectedKey == KeyOf(p)))
                NavigateHome();
            else
            {
                BuildNav();
                (ContentArea.Content as HomeView)?.Refresh(); // the deleted memories' projects/items were on it
            }
            return true;
        }

        // ---- Undo -----------------------------------------------------------

        // The keys and the toast of this window (for the harness too).
        public UndoHost Undo { get; }

        // After an undo or redo: everything shown is read again. A sidebar step changed profiles.json, so the list of
        // memories is read anew (the same list object, which Home and the Day Plan hold) and the page shown again with
        // the memory as it is now (its undo history stays: the same page); one that is gone leaves Home showing.
        private void AfterUndo(UndoHistory history, UndoStep step)
        {
            if (history == UndoService.App)
            {
                if (ProfileManager.TryLoadProfiles(out List<Profile> stored, out _))
                {
                    profiles.Clear();
                    profiles.AddRange(stored);
                }
                if (CalendarPanel.IsOpen)
                    CalendarPanel.Refresh();
                Profile? shown = profiles.FirstOrDefault(p => KeyOf(p) == selectedKey);
                if (shown != null)
                {
                    NavigateTo(shown);
                    return;
                }
                if (selectedKey != HomeKey && selectedKey != DayPlanKey)
                {
                    NavigateHome();
                    return;
                }
                BuildNav();
            }
            (ContentArea.Content as IUndoPage)?.ReloadAfterUndo();
        }

        // What deleted memories gained between an undo of their delete and its redo (an item's new picture, a new dated
        // document): the parts recorded at the delete don't know it. On each redo its files are held too and the new
        // reminders go (kept here); on the undo after, those reminders come back, if nothing put them back already.
        private sealed class MemoriesGained
        {
            private readonly List<Profile> memories;
            private readonly HashSet<Guid> known;
            private readonly Dictionary<Guid, Reminder> taken = new Dictionary<Guid, Reminder>();

            public MemoriesGained(List<Profile> memories, HashSet<Guid> known)
            {
                this.memories = memories;
                this.known = known;
            }

            public string? Follow(StoredStep step, bool undone)
            {
                try
                {
                    List<Reminder> existing = ReminderManager.LoadReminders();
                    if (undone)
                    {
                        foreach (Reminder reminder in taken.Values.Where(r => existing.All(e => e.Id != r.Id)))
                            ReminderManager.UpdateReminder(reminder);
                        taken.Clear();
                        return null;
                    }
                    foreach (Profile memory in memories)
                    {
                        step.Hold(UndoFiles.OfMemory(memory, out List<Guid> dated));
                        foreach (Reminder reminder in existing.Where(r => dated.Contains(r.Id) && !known.Contains(r.Id)))
                        {
                            taken[reminder.Id] = reminder;
                            ReminderManager.DeleteReminder(reminder.Id);
                        }
                    }
                    return null;
                }
                catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException || e is System.Text.Json.JsonException
                                          || e is System.Runtime.InteropServices.COMException)
                {
                    UnhandledErrors.LogQuietly("undo", e);
                    return Loc.T("Undo.RemindersNote");
                }
            }
        }

        private static void WarnProfilesNotSaved(string consequence)
        {
            Msg.Show(Loc.F("Memory.NotSaved", AppPaths.Data("profiles.json"), consequence), Loc.T("Common.CouldntSave"),
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

        private static string NameTakenText(ProfileType type) =>
            Loc.T(type == ProfileType.Collection ? "Memory.CollectionNameTaken" : "Memory.ProjectNameTaken");

        // The page background, card pictures, attached files, item pictures and document files of a memory being deleted,
        // kept in the app-wide folders, so they aren't left behind there. An unreadable file just means nothing to clean up.
        private static void DeleteOwnedFiles(Profile memory)
        {
            if (memory.BackgroundType == BackgroundFill.Image)
                ImageStore.Backgrounds.Delete(memory.BackgroundImagePath);

            if (memory.Type == ProfileType.Collection)
            {
                // The memory has already left profiles.json, so only files another collection's item still uses stay. Its
                // vehicles' document reminders go too (best effort, like the files).
                List<Entity> items = EntityStore.LoadOrEmpty(memory);
                EntityFiles.DeleteUnreferenced(items);
                foreach (Entity item in items)
                    VehicleReminders.Follow(item, null);
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
            SettingsWindow settings = new SettingsWindow(ExportAll, ImportPackage) { Owner = this };
            settings.ShowDialog();
        }

        // Settings > Import: a .tidymind package, as a new project in a project memory (the open one by default) or a
        // new collection. Then that memory shows, read again from disk.
        private bool ImportPackage(Window owner)
        {
            Profile current = profiles.FirstOrDefault(p => KeyOf(p) == selectedKey);
            Profile shown = TransferUI.Import(owner, profiles, current, NextColor);
            if (shown == null)
                return false;
            NavigateTo(shown);
            return true;
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
            if (profiles.Count == 0 && !ExportService.HasDayPlan())
            {
                Msg.Show(Loc.T("Export.Nothing"), Loc.T("Export.Caption"), MessageBoxButton.OK, MessageBoxImage.Information);
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
            if (result.Kind == SearchResultKind.PlanBlock)
            {
                ShowDayPlan(result.Date, result.ItemId);
                return;
            }
            NavigateTo(result.Profile);
            if (result.Kind == SearchResultKind.Memory) return;
            if (result.Kind == SearchResultKind.Note || result.Kind == SearchResultKind.MemoryNote)
                NotebookEditor.OpenAtPage(result.ItemId, result.Page);

            // After the new view has loaded, so a project opens over its visible card (and can pulse on close).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                // A page of the memory's notebook: the notebook over its page, at that page.
                if (result.Kind == SearchResultKind.MemoryNote)
                    (ContentArea.Content as IMemoryPage)?.OpenNotebook();
                else if (ContentArea.Content is ProjectView projectView)
                    projectView.Reveal(result.ItemId);
                else if (ContentArea.Content is CollectionView collectionView)
                    collectionView.Reveal(result.ItemId);
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ---- Corner utilities ---------------------------------------------

        private void QuickNotesButton_Click(object sender, RoutedEventArgs e) => OverlayManager.Route(Overlay.QuickNotes);

        private void RemindersIconButton_Click(object sender, RoutedEventArgs e) => OverlayManager.Route(Overlay.Reminders);

        private void HelpButton_Click(object sender, RoutedEventArgs e) => HelpPopup.IsOpen = !HelpPopup.IsOpen;

        // ---- Help: tips, the tour, what's new, examples ------------------------

        // The ? menu's last entry only while there are no example memories to add.
        private void HelpPopup_Opened(object sender, EventArgs e) =>
            AddExamplesLink.Visibility = WelcomeMemory.Exists(profiles) ? Visibility.Collapsed : Visibility.Visible;

        private void Tips_Click(object sender, RoutedEventArgs e)
        {
            HelpPopup.IsOpen = false;
            TipsWindow.ShowFor(this);
        }

        private void WhatsNew_Click(object sender, RoutedEventArgs e)
        {
            HelpPopup.IsOpen = false;
            WhatsNew.Show(this);
        }

        // Brings Home's Getting started list back (it stays until hidden again) and shows it.
        private void Checklist_Click(object sender, RoutedEventArgs e)
        {
            HelpPopup.IsOpen = false;
            AppSettings.Update(s => s.Checklist = AppSettings.ChecklistShown);
            NavigateHome();
            Dispatcher.BeginInvoke(new Action(() => (ContentArea.Content as HomeView)?.BringChecklistIntoView()),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void AddExamples_Click(object sender, RoutedEventArgs e)
        {
            HelpPopup.IsOpen = false;
            AddExamples();
        }

        private void TakeTour_Click(object sender, RoutedEventArgs e)
        {
            HelpPopup.IsOpen = false;
            StartTour();
        }

        private void StartTour()
        {
            if (AppSettings.Load().Tour == null)
                AppSettings.Update(s => s.Tour = AppSettings.TourShown);
            GuidedTour.Run(TourSteps.Build(this), TourEnded);
        }

        // Before every tour step (TourStep.Prepare): Home shown, the sidebar at its top, nothing open over the window (a
        // click through the spotlight may have opened something). A step whose element is still not wholly on screen
        // is skipped (GuidedTour.OnScreen).
        public void ReadyForTour()
        {
            OverlayManager.CloseAll();
            foreach (Window note in Application.Current.Windows.OfType<Window>().Where(w => w is TipsWindow || w is WhatsNewWindow).ToList())
                note.Close();
            if (!(ContentArea.Content is HomeView))
                NavigateHome();
            NavScroll.ScrollToTop();
            UpdateLayout();
        }

        private void TourEnded(TourEnd end)
        {
            switch (end)
            {
                case TourEnd.Finished:
                    AppSettings.Update(s => s.Tour = AppSettings.TourCompleted);
                    break;
                case TourEnd.Skipped:
                    AppSettings.Update(s => s.Tour = s.Tour == AppSettings.TourCompleted ? s.Tour : AppSettings.TourDismissed);
                    break;
                case TourEnd.Begin:
                    StartTour();
                    break;
                case TourEnd.Examples:
                    AppSettings.Update(s => s.Tour = AppSettings.TourDismissed);
                    AddExamples();
                    break;
            }
        }

        // The example project memory and collection (WelcomeMemory), added next to the user's own memories, which are
        // never touched; then the project one is shown.
        private void AddExamples()
        {
            if (!WelcomeMemory.Exists(profiles) && !WelcomeMemory.TryCreate(profiles, NextColor))
            {
                BuildNav();
                Msg.Show(this, Loc.F("Examples.WriteFailed", AppPaths.DataFolder), Loc.T("Examples.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ShowSample(ProfileType.Project);
        }

        // Shows that example memory unless it's already the open page.
        public void ShowSample(ProfileType type)
        {
            Profile? sample = WelcomeMemory.Find(profiles, type);
            if (sample != null && selectedKey != KeyOf(sample))
                NavigateTo(sample);
            else
                BuildNav();
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
