using System;
using System.Collections.Generic;
using System.IO;
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

        public ProjectView(Profile profile)
        {
            InitializeComponent();
            profileName = profile.Name;
            TitleText.Text = profile.Name;
            LoadProjects();
            TodoPanel.Load(profile.Name);

            // Cards sit 24px apart (12px margin each side); the drop line goes in the middle of that gap.
            cardDrag = new DragReorder<Project>(ProjectPanel, Orientation.Horizontal, 24, null, MoveProject);

            tabStrip = new MemoryTabStrip(this, TabStrip, profileName, ProfileType.Project, "project",
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

        private void LoadProjects()
        {
            string fileName = profileName + ".json";
            if (File.Exists(fileName))
                allProjects = JsonSerializer.Deserialize<List<Project>>(File.ReadAllText(fileName)) ?? new List<Project>();
            else
                allProjects = new List<Project>();

            // Stable sort: files from before Order existed (all 0) keep their saved order.
            allProjects = allProjects.OrderBy(p => p.Order).ToList();
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

        private void SaveProjects()
        {
            File.WriteAllText(profileName + ".json", JsonSerializer.Serialize(allProjects));
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
            ProjectPanel.Children.Clear();
            cardDrag.Clear();

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

        private const double RingThickness = 3;

        private FrameworkElement CreateProjectCard(Project project)
        {
            if (project.Tasks == null)
                project.Tasks = new List<TaskItem>();

            int total = project.Tasks.Count;
            int done = project.Tasks.Count(t => t.IsDone);
            double size = CardEffects.CardSize;

            Grid card = new Grid
            {
                Width = size,
                Height = size,
                Margin = new Thickness(12),
                Cursor = Cursors.Hand
            };

            DropShadowEffect shadow = CardEffects.CreateShadow();
            card.Children.Add(new Border
            {
                Background = Brushes.White,
                CornerRadius = new CornerRadius(CardEffects.CardRadius),
                Effect = shadow
            });

            PathGeometry ring = CardEffects.BuildRingGeometry(size, CardEffects.CardRadius, RingThickness);

            card.Children.Add(new ShapePath
            {
                Data = ring,
                Stroke = B("#ECECE9"),
                StrokeThickness = RingThickness
            });

            ShapePath progressRing = null;
            if (done > 0)
            {
                progressRing = new ShapePath
                {
                    Data = ring,
                    Stroke = CardEffects.Accent,
                    StrokeThickness = RingThickness,
                    StrokeDashCap = PenLineCap.Round
                };

                // A complete ring is drawn solid: a dash exactly one perimeter long can leave a hairline seam.
                if (done < total)
                {
                    double perimeter = CardEffects.RingPerimeter(size, CardEffects.CardRadius, RingThickness);
                    double progress = (double)done / total;

                    // WPF dash lengths are in multiples of StrokeThickness, not pixels.
                    progressRing.StrokeDashArray = new DoubleCollection
                    {
                        perimeter * progress / RingThickness,
                        perimeter / RingThickness
                    };
                }

                // In a Canvas so the heartbeat's thicker stroke and glow aren't layout-clipped to the 190px cell.
                card.Children.Add(new Canvas { Children = { progressRing } });
            }

            card.Children.Add(new TextBlock
            {
                Text = project.Name,
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = B("#1C1B19"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                LineHeight = 23,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = 69,
                TextTrimming = TextTrimming.CharacterEllipsis,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20)
            });

            card.Children.Add(CreateProgressLabel(project, done, total));

            CardEffects.AttachHoverLift(card, shadow, progressRing == null ? null : () => PulseRing(progressRing));

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

            ContextMenu menu = new ContextMenu();
            MenuItem rename = new MenuItem { Header = "Rename" };
            rename.Click += (s, e) => RenameProject(project);
            MenuItem delete = new MenuItem { Header = "Delete" };
            delete.Click += (s, e) => DeleteProject(project);
            MenuItem reminder = new MenuItem { Header = "Add Reminder" };
            reminder.Click += (s, e) => new AddReminderWindow(project.Name).ShowDialog();
            menu.Items.Add(rename);
            menu.Items.Add(delete);
            menu.Items.Add(reminder);
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => project.Name, "Copy name"));
            menu.Items.Add(TextCopy.CopyItem(() => ProjectAsText(project), "Copy with tasks"));
            card.ContextMenu = menu;

            return card;
        }

        private UIElement CreateProgressLabel(Project project, int done, int total)
        {
            StackPanel label = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 16, 14)
            };

            if (total == 0)
            {
                label.Children.Add(new TextBlock { Text = "No tasks", FontSize = 11, Foreground = B("#A5A5A1") });
                return label;
            }

            int percent = project.CompletionPercent();

            label.Children.Add(new TextBlock
            {
                Text = done + "/" + total,
                FontSize = 11,
                Foreground = B("#8A8A87"),
                Margin = new Thickness(0, 0, 6, 0)
            });
            label.Children.Add(new TextBlock
            {
                Text = percent + "%",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = CardEffects.Accent
            });

            return label;
        }

        private static void PulseRing(ShapePath ring)
        {
            DropShadowEffect glow = new DropShadowEffect
            {
                Color = CardEffects.Accent.Color,
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
            if (projectIndex < 0 || projectIndex >= allProjects.Count) return;

            Project project = allProjects[projectIndex];
            tabStrip.Select(project.TabId);
            OpenProject(project);
        }

        // Name, description and a checklist of the tasks, as plain text for pasting elsewhere.
        private static string ProjectAsText(Project project)
        {
            List<string> lines = new List<string> { project.Name };

            string description = RichTextHelper.ToPlainText(project.Description).Trim();
            if (description.Length > 0)
                lines.Add(description);

            if (project.Tasks != null && project.Tasks.Count > 0)
            {
                lines.Add("");
                lines.AddRange(project.Tasks.Select(t => (t.IsDone ? "[x] " : "[ ] ") + t.Title));
            }

            return string.Join(Environment.NewLine, lines);
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
            SaveProjects();
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

        private void RenameProject(Project project)
        {
            string newName = InputDialog.Prompt(this, "Rename Project", "Enter a new name.", project.Name, "Rename");
            if (string.IsNullOrWhiteSpace(newName) || newName == project.Name) return;

            project.Name = newName;
            project.LastModified = DateTime.Now;
            SaveProjects();
            RenderProjects();
        }

        private void DeleteProject(Project project)
        {
            var result = MessageBox.Show("Delete '" + project.Name + "'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                allProjects.Remove(project);
                pendingPulse.Remove(project);
                SaveProjects();
                RenderProjects();
            }
        }

        private static SolidColorBrush B(string hex)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
    }
}
