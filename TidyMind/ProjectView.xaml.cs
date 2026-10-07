using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using ShapePath = System.Windows.Shapes.Path;

namespace TidyMind
{
    public partial class ProjectView : UserControl
    {
        private readonly string profileName;
        private List<Project> allProjects;
        private readonly MemoryTabStrip tabStrip;

        // Projects that just crossed to 100% while hidden (e.g. behind ProjectWindow); they pulse once when their card renders.
        private readonly HashSet<Project> pendingPulse = new HashSet<Project>();

        private readonly DragReorder<Project> cardDrag;
        private readonly CardSelectionController<Project> selection;

        // Its projects or tabs file couldn't be read: shown read-only (ShowUnreadable), and never saved over.
        private bool unreadable;

        // saveProfiles: writes the main window's list of memories, which `profile` belongs to (Edit Background).
        public ProjectView(Profile profile, Func<bool> saveProfiles)
        {
            InitializeComponent();
            profileName = profile.Name;
            MemoryPageBackground.Attach(Page, BackgroundLayer, profile, saveProfiles);
            TitleText.Text = profile.Name;
            TodoPanel.Load(profile.Name);

            // An unreadable file is left alone: the page says why, and nothing on it can change or save projects.
            if (!ProjectStore.TryLoad(profileName, out allProjects, out string problem))
            {
                ShowUnreadable(profileName + ".json", problem);
                return;
            }
            if (!TabStore.TryLoadOrCreate(profileName, ProfileType.Project, allProjects.Select(p => p.TabId),
                    out List<MemoryTab> tabs, out problem))
            {
                ShowUnreadable(TabStore.FileName(profileName, ProfileType.Project), problem);
                return;
            }

            selection = new CardSelectionController<Project>(this, CardScroll, CardArea, MarqueeCanvas,
                CardEffects.CardRadius, DeleteProjects);
            selection.SelectionChanged += UpdateSelectionBar;
            SelectionBar.DeleteClicked += () => selection.DeleteSelected();
            SelectionBar.CancelClicked += selection.ClearSelection;

            // Cards sit 24px apart (12px margin each side); the drop line goes in the middle of that gap.
            cardDrag = new DragReorder<Project>(ProjectPanel, Orientation.Horizontal, 24, null, MoveProject);

            tabStrip = new MemoryTabStrip(this, TabStrip, profileName, ProfileType.Project, tabs, "project",
                tabId => allProjects.Count(p => p.TabId == tabId),
                (fromTab, toTab) =>
                {
                    foreach (Project project in allProjects.Where(p => p.TabId == fromTab))
                        project.TabId = toTab;
                    SaveProjects();
                });
            tabStrip.TabsChanged += RenderProjects;

            AssignOrphansToFirstTab();
            tabStrip.Render();
            RenderProjects();
        }

        // Reads the list back from disk, e.g. after a delete whose save failed, so the grid shows what's really stored.
        private void ReloadProjects()
        {
            if (ProjectStore.TryLoad(profileName, out List<Project> projects, out string problem))
                allProjects = projects;
            else
                ShowUnreadable(profileName + ".json", problem);
        }

        // The page without its projects: the reason in place of the cards, and no toolbar or tabs to change anything
        // with. The memory's to-do list and page background have their own files and keep working.
        private void ShowUnreadable(string fileName, string problem)
        {
            unreadable = true;
            selection?.ClearSelection();
            ProjectPanel.Children.Clear();
            Toolbar.Visibility = Visibility.Collapsed;
            SelectionBar.Visibility = Visibility.Collapsed;
            TabStrip.Visibility = Visibility.Collapsed;
            SubtitleText.Text = "PROJECTS";
            EmptyState.Text = "Couldn't read " + fileName + ": " + problem + "\n\nThe file was left as it is. "
                + "Fix it or restore it from a backup, then open this memory again.";
            EmptyState.TextWrapping = TextWrapping.Wrap;
            EmptyState.TextAlignment = TextAlignment.Center;
            EmptyState.MaxWidth = 520;
            EmptyState.Visibility = Visibility.Visible;
        }

        // Drag-and-drop on the cards. Other tabs' projects keep their relative order; everything is renumbered 0..n.
        private void MoveProject(Project dragged, Project target, bool after)
        {
            DragReorder<Project>.Move(allProjects, dragged, target, after);
            for (int i = 0; i < allProjects.Count; i++)
                allProjects[i].Order = i;

            SaveProjects();
            RenderProjects();
        }

        private bool SaveProjects()
        {
            return !unreadable && ProjectStore.SaveOrWarn(profileName, allProjects);
        }

        // Projects from before tabs existed (TabId empty) or pointing at a deleted tab go to the first tab.
        private void AssignOrphansToFirstTab()
        {
            bool migrated = false;
            foreach (Project project in allProjects.Where(p => !tabStrip.Contains(p.TabId)))
            {
                project.TabId = tabStrip.FirstTabId;
                migrated = true;
            }

            if (migrated)
                SaveProjects();
        }

        private void RenderProjects()
        {
            if (unreadable) return;

            ProjectPanel.Children.Clear();
            cardDrag.Clear();
            selection.BeginRender();

            string filter = SearchBox.Text == "Search..." ? "" : SearchBox.Text.Trim();
            List<Project> inTab = allProjects.Where(p => p.TabId == tabStrip.ActiveTabId).ToList();

            int shown = 0;
            foreach (Project project in inTab)
            {
                if (!string.IsNullOrEmpty(filter) &&
                    (project.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                ProjectPanel.Children.Add(CreateProjectCard(project));
                shown++;
            }

            SubtitleText.Text = "PROJECTS  ·  " + inTab.Count + (inTab.Count == 1 ? " item" : " items");
            EmptyState.Visibility = shown == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Text = inTab.Count == 0
                ? "No projects in this tab yet. Click “+ Add Project” to start."
                : "No projects match your search.";
            selection.EndRender(); // what's no longer on screen (other tab, filtered out) drops out of the selection
        }

        // ---- Multi-selection ----------------------------------------------

        // While anything is selected, the selection bar stands in for the search box and Add buttons.
        private void UpdateSelectionBar()
        {
            bool any = selection.Count > 0;
            SelectionBar.Show(selection.Count);
            SelectionBar.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
            Toolbar.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        }

        // One confirmation for the lot, then the same delete as a single project's menu (card pictures included),
        // and the grid read back from disk.
        private bool DeleteProjects(IReadOnlyCollection<Project> projects)
        {
            string what = projects.Count == 1 ? "1 project" : projects.Count + " projects";
            if (MessageBox.Show("Delete " + what + "? This can't be undone.", "Confirm Delete",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return false;

            ProjectMenu.Delete(projects, p =>
            {
                allProjects.Remove(p);
                pendingPulse.Remove(p);
            }, SaveProjects);
            ReloadProjects();
            RenderProjects();
            return true;
        }

        // ---- Search -------------------------------------------------------

        private void SearchBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (SearchBox.Text == "Search...")
            {
                SearchBox.Text = "";
                SearchBox.Foreground = B("#1C1B19");
            }
        }

        private void SearchBox_LostFocus(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                SearchBox.Text = "Search...";
                SearchBox.Foreground = B("#8A8A87");
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchBox.Text == "Search...") return;
            RenderProjects();
        }

        // ---- Card ---------------------------------------------------------

        // The look lives in ProjectCard (shared with the Home dashboard); this adds the grid's behaviour.
        private FrameworkElement CreateProjectCard(Project project)
        {
            if (project.Tasks == null)
                project.Tasks = new List<TaskItem>();

            ProjectCardVisual visual = ProjectCard.Build(ProjectCardModel.From(project),
                CardEffects.CardSize, CardEffects.CardRadius, compact: false);
            Grid card = visual.Card;
            ShapePath progressRing = visual.Progress;
            card.Margin = new Thickness(12);
            card.Cursor = Cursors.Hand;
            selection.AttachCard(card, project); // first: a Ctrl+click selects, and neither opens nor drags

            CardEffects.AttachHoverLift(card, visual.Shadow, progressRing == null ? null : () => PulseRing(progressRing));

            if (pendingPulse.Contains(project) && progressRing != null)
            {
                RoutedEventHandler onLoaded = null;
                onLoaded = (s, e) =>
                {
                    card.Loaded -= onLoaded;
                    pendingPulse.Remove(project);
                    CardEffects.RingHeartbeat(progressRing);
                };
                card.Loaded += onLoaded;
            }

            CardEffects.AttachClick(card, () => OpenProject(project));
            cardDrag.Attach(card, project);

            card.ContextMenu = ProjectMenu.Build(this, project,
                save: SaveProjects,
                redraw: RenderProjects,
                remove: () =>
                {
                    allProjects.Remove(project);
                    pendingPulse.Remove(project);
                },
                reload: ReloadProjects);

            return card;
        }

        private static void PulseRing(ShapePath ring)
        {
            DropShadowEffect glow = new DropShadowEffect
            {
                Color = CardEffects.GlowColor(ring),
                ShadowDepth = 0,
                BlurRadius = 0,
                Opacity = 0.6
            };
            ring.Effect = glow;

            DoubleAnimation pulse = new DoubleAnimation(0, 10, TimeSpan.FromMilliseconds(220))
            {
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            pulse.Completed += (s, e) =>
            {
                if (ring.Effect == glow)
                    ring.Effect = null;
            };
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, pulse);
        }

        // ---- CRUD ---------------------------------------------------------

        private void AddProjectButton_Click(object sender, RoutedEventArgs e)
        {
            string name = InputDialog.Prompt(this, "Add Project", "What's the project called?");
            if (string.IsNullOrWhiteSpace(name)) return;

            allProjects.Add(new Project
            {
                Name = name,
                Tasks = new List<TaskItem>(),
                TabId = tabStrip.ActiveTabId,
                Order = allProjects.Count == 0 ? 0 : allProjects.Max(p => p.Order) + 1,
                LastModified = DateTime.Now
            });
            SaveProjects();
            RenderProjects();
        }

        private void AddTodoListButton_Click(object sender, RoutedEventArgs e) => TodoPanel.Reveal();

        // From global search / the dashboard: switch to the project's tab, then open it.
        public void Reveal(int projectIndex)
        {
            if (unreadable || projectIndex < 0 || projectIndex >= allProjects.Count) return;

            Project project = allProjects[projectIndex];
            tabStrip.Select(project.TabId);
            OpenProject(project);
        }

        private void OpenProject(Project project)
        {
            bool wasComplete = IsComplete(project);
            string before = JsonSerializer.Serialize(project);

            ProjectWindow window = new ProjectWindow(project);
            window.ShowDialog();

            // Anything changed in the window (name, description, status, tasks, note) counts as an edit.
            if (JsonSerializer.Serialize(project) != before)
                project.LastModified = DateTime.Now;

            MarkPulseIfJustCompleted(project, wasComplete);
            // Removed files' copies go only once the project is saved without them; if the save failed they stay.
            if (SaveProjects())
                AttachmentStore.Delete(window.RemovedAttachments);
            RenderProjects();
        }

        private void MarkPulseIfJustCompleted(Project project, bool wasComplete)
        {
            if (!wasComplete && IsComplete(project))
                pendingPulse.Add(project);
        }

        private static bool IsComplete(Project project)
        {
            return project.Tasks != null && project.Tasks.Count > 0 && project.Tasks.All(t => t.IsDone);
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
