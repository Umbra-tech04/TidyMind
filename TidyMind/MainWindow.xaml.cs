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

            NavigateHome();

            // A fresh install (no memories, the tour never offered): the welcome card, once the window is up.
            if (profiles.Count == 0 && AppSettings.Load().Tour == null)
                Loaded += (s, e) => GuidedTour.Run(new[] { TourSteps.Welcome }, TourEnded);
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

        private void NavigateHome()
        {
            selectedKey = HomeKey;
            ContentArea.Content = new HomeView(profiles, GoToSearchResult, () => ShowDayPlan(DateTime.Today)); // rebuilt (and re-read from disk) on every visit
            BuildNav();
        }

        // That day's plan; `select`: a block to bring into view (a search result). The calendar closes, so the
        // plan isn't left behind it.
        public void ShowDayPlan(DateTime date, Guid? select = null)
        {
            CloseCalendar();
            selectedKey = DayPlanKey;
            ContentArea.Content = new DayPlanView(profiles, date, select);
            BuildNav();
            TourSignals.Raise(TourSignals.DayPlanOpened);
        }

        private void NavigateTo(Profile profile)
        {
            selectedKey = KeyOf(profile);

            if (profile.Type == ProfileType.Collection)
                ContentArea.Content = new CollectionView(profile, () => ProfileManager.TrySaveProfiles(profiles));
            else
                ContentArea.Content = new ProjectView(profile, () => ProfileManager.TrySaveProfiles(profiles));

            BuildNav();
            if (profile.Type == ProfileType.Collection)
                TourSignals.Raise(TourSignals.CollectionOpened);
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
            dayPlan.Name = "NavDayPlan"; // the guided tour points at it
            NavPanel.Children.Add(dayPlan);

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
                menu.Items.Add(TransferUI.ExportItem(() => TransferUI.ExportCollection(this, profile)));
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => profile.Name, Loc.T("Common.CopyName")));

            Border item = CreateNavItem("📁", profile.Name, selectedKey == KeyOf(profile),
                () => NavigateTo(profile), menu, true);
            item.Name = "Nav" + KeyOf(profile); // the guided tour points at it (SampleNavItem)
            memorySelection.Attach(item, profile); // before the drag: a Ctrl or Shift click selects, and neither opens nor drags
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
                WarnProfilesNotSaved(Loc.T("Memory.NotSaved.Order"));
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

            Profile profile = new Profile
            {
                Id = Guid.NewGuid(),
                Name = name,
                Color = NextColor(),
                Type = type,
                Order = profiles.Count == 0 ? 0 : profiles.Max(p => p.Order) + 1
            };
            profiles.Add(profile);
            if (!ProfileManager.TrySaveProfiles(profiles))
            {
                profiles.Remove(profile);
                WarnProfilesNotSaved(Loc.T(collection ? "Memory.NotSaved.CollectionNotAdded" : "Memory.NotSaved.ProjectNotAdded"));
                return;
            }

            NavigateTo(profile); // the view creates the memory's folder and first tab
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

            profile.Name = newName;
            if (!ProfileManager.TrySaveProfiles(profiles))
            {
                profile.Name = oldName;
                WarnProfilesNotSaved(Loc.T("Memory.NotSaved.NameUnchanged"));
                return;
            }
            NavigateTo(profile);
        }

        private void DeleteMemory(Profile profile)
        {
            MessageBoxResult result = Msg.Show(
                Loc.F("Memory.Delete.Confirm", profile.Name)
                + (profile.IsSample == true ? "\n\n" + Loc.T("Tour.DeleteNote") : ""),
                Loc.T("Common.ConfirmDelete"), MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
                RemoveMemories(new[] { profile });
        }

        // One memory (its menu) or several (a selection). They leave profiles.json first, in one save; only once that
        // worked are their folders and the pictures and attached files they own deleted (best effort: one that can't
        // be deleted is left behind, unused). If the save fails, nothing is deleted (false). The open one among them: Home.
        private bool RemoveMemories(IReadOnlyList<Profile> doomed)
        {
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

            if (doomed.Any(p => selectedKey == KeyOf(p)))
                NavigateHome();
            else
            {
                BuildNav();
                (ContentArea.Content as HomeView)?.Refresh(); // the deleted memories' projects/items were on it
            }
            return true;
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
            if (result.Kind == SearchResultKind.Note)
                ProjectWindow.OpenAtPage(result.ItemId, result.Page);

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

        // ---- Guided tour --------------------------------------------------

        private void TakeTour_Click(object sender, RoutedEventArgs e)
        {
            HelpPopup.IsOpen = false;
            StartTour();
        }

        // Makes sure the example memories exist (asking first, unless the welcome card's Start already did), then runs
        // the tour. The user's own memories are never touched.
        private void StartTour(bool confirmed = false)
        {
            if (!WelcomeMemory.Exists(profiles))
            {
                if (!confirmed && Msg.Show(this, Loc.T("Tour.Confirm.Text"), Loc.T("Tour.Confirm.Title"),
                        MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                    return;
                bool added = WelcomeMemory.TryCreate(profiles, NextColor);
                BuildNav();
                if (!added)
                {
                    Msg.Show(this, Loc.F("Tour.Confirm.WriteFailed", AppPaths.DataFolder), Loc.T("Tour.Confirm.Title"),
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            if (AppSettings.Load().Tour == null)
                AppSettings.Update(s => s.Tour = AppSettings.TourShown);
            GuidedTour.Run(TourSteps.Build(this), TourEnded);
        }

        private void TourEnded(TourEnd end)
        {
            // Every end but Begin (which only swaps the welcome card for the tour): nothing is left open.
            if (end != TourEnd.Begin)
                TourSteps.CloseOpened();

            switch (end)
            {
                case TourEnd.Finished:
                    AppSettings.Update(s => s.Tour = AppSettings.TourCompleted);
                    break;
                case TourEnd.Skipped:
                    AppSettings.Update(s => s.Tour = s.Tour == AppSettings.TourCompleted ? s.Tour : AppSettings.TourDismissed);
                    break;
                case TourEnd.Begin:
                    StartTour(confirmed: true);
                    break;
            }
        }

        // For TourSteps: shows that example memory unless it's already the open page.
        public void ShowSample(ProfileType type)
        {
            Profile? sample = WelcomeMemory.Find(profiles, type);
            if (sample != null && selectedKey != KeyOf(sample))
                NavigateTo(sample);
        }

        public bool ShowsSample => profiles.Any(p => p.IsSample == true && KeyOf(p) == selectedKey);

        // Opens the example project with notes on two pages, the way a search result opens a project.
        public void OpenSampleNotebook()
        {
            Profile? sample = WelcomeMemory.Find(profiles, ProfileType.Project);
            Guid? notebook = sample == null ? null : WelcomeMemory.NotebookProject(sample);
            if (notebook != null)
                GoToSearchResult(new SearchResult { Kind = SearchResultKind.Project, Profile = sample, ItemId = notebook.Value });
        }

        // A word the tour's search step suggests: the last word of the example collection's last item ("bike" from
        // "City bike"), so it finds something in whichever language the examples were written.
        public string SampleSearchWord()
        {
            Profile? sample = WelcomeMemory.Find(profiles, ProfileType.Collection);
            string? name = sample == null ? null : EntityStore.LoadOrEmpty(sample).OrderBy(e => e.Order).LastOrDefault()?.Name;
            string? word = name?.Split(' ', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
            return word?.ToLower(Loc.Culture) ?? Loc.T("Tour.Search.Example");
        }

        public FrameworkElement? SampleNavItem(ProfileType type)
        {
            Profile? sample = WelcomeMemory.Find(profiles, type);
            return sample == null ? null : GuidedTour.Find(this, "Nav" + KeyOf(sample));
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
