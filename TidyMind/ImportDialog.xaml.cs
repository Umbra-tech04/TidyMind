using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // The preview before a package is imported: what it is, what's in it, when and with which version it was
    // exported, and where it goes. A project goes into a project memory, an item into a collection (the open one by
    // default, or a new collection when there is none); a collection becomes a new collection. Either way it's a new
    // copy; the name it gets there is said if it changes.
    public partial class ImportDialog : Window
    {
        private readonly OpenedPackage package;
        private readonly List<Profile> profiles;
        private readonly bool hasTarget; // a project or an item: it goes into a memory picked here

        public ImportDialog(OpenedPackage package, List<Profile> profiles, Profile? current)
        {
            InitializeComponent();
            this.package = package;
            this.profiles = profiles;
            hasTarget = !package.IsCollection;

            ProfileType kind = package.IsItem ? ProfileType.Collection : ProfileType.Project;
            List<Profile> memories = hasTarget ? profiles.Where(p => p.Type == kind).OrderBy(p => p.Order).ToList() : new List<Profile>();
            // Items of their own (the app's ComboBox look shows an item's content, not DisplayMemberPath), by name.
            foreach (Profile memory in memories)
                TargetBox.Items.Add(new ComboBoxItem { Content = memory.Name, Tag = memory });
            Profile? chosen = current != null && memories.Contains(current) ? current : memories.FirstOrDefault();
            TargetBox.SelectedIndex = chosen == null ? -1 : memories.IndexOf(chosen);
            TargetPanel.Visibility = memories.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            ShowPackage();
            Loc.Track(this, ShowPackage);
        }

        // The memory chosen: a project memory for a project, a collection for an item (null when there's none yet: a
        // new one is made), null for a collection.
        public Profile? Target => hasTarget ? (TargetBox.SelectedItem as ComboBoxItem)?.Tag as Profile : null;

        private void ShowPackage()
        {
            string name = package.Name;
            if (package.IsCollection)
            {
                KindText.Text = Loc.F("Transfer.Import.Collection", name);
                CountsText.Text = package.NotebookPages > 0
                    ? Loc.F("Transfer.Import.CollectionNotebookCounts", Loc.Plural("Transfer.Count.Items", package.Items),
                        Loc.Plural("Transfer.Count.Pictures", package.Pictures), Loc.Plural("Transfer.Count.Pages", package.NotebookPages))
                    : Loc.F("Transfer.Import.CollectionCounts",
                        Loc.Plural("Transfer.Count.Items", package.Items), Loc.Plural("Transfer.Count.Pictures", package.Pictures));
            }
            else if (package.IsItem)
            {
                KindText.Text = Loc.F("Transfer.Import.Item", name);
                CountsText.Text = Loc.Plural("Transfer.Count.Pictures", package.Pictures);
            }
            else
            {
                KindText.Text = Loc.F("Transfer.Import.Project", name);
                CountsText.Text = Loc.F("Transfer.Import.ProjectCounts", Loc.Plural("Transfer.Count.Tasks", package.Tasks),
                    Loc.Plural("Transfer.Count.Pages", package.NotePages), Loc.Plural("Transfer.Count.Files", package.Files));
            }
            ExportedText.Text = Loc.F("Transfer.Import.Exported",
                package.Manifest.Exported.ToLocalTime().ToString(Loc.T("Format.LongDate"), Loc.Culture), package.Manifest.AppVersion);
            NewerText.Text = Loc.F("Transfer.Import.Newer", package.Manifest.AppVersion);
            NewerText.Visibility = package.FromNewerApp ? Visibility.Visible : Visibility.Collapsed;
            ShowWhere();
        }

        private void TargetBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowWhere();

        // Where it goes and under which name. A project needs a project memory to go into; an item with no collection
        // to go into gets a new one.
        private void ShowWhere()
        {
            ImportButton.IsEnabled = true;
            if (package.IsCollection)
            {
                string unique = UniqueCollectionName(package.Name);
                WhereText.Text = unique == package.Name
                    ? Loc.T("Transfer.Import.NewCollection")
                    : Loc.F("Transfer.Import.NewCollectionRenamed", unique);
                return;
            }

            if (!(Target is Profile target))
            {
                if (package.IsItem)
                    WhereText.Text = Loc.F("Transfer.Import.NewItemNewCollection", UniqueCollectionName(Loc.T("Transfer.Import.NewCollectionName")));
                else
                {
                    WhereText.Text = Loc.T("Transfer.Import.NoProjectMemory");
                    ImportButton.IsEnabled = false;
                }
                return;
            }

            // A memory whose file can't be read takes nothing in (it's never saved over): said here, not after unpacking.
            List<string>? names = null;
            string problem;
            if (package.IsItem)
            {
                if (EntityStore.TryLoad(target, out List<Entity> items, out problem))
                    names = items.Select(i => i.Name).ToList();
            }
            else if (ProjectStore.TryLoad(target, out List<Project> projects, out problem))
                names = projects.Select(p => p.Name).ToList();
            if (names == null)
            {
                WhereText.Text = Loc.F("Transfer.Error.TargetUnreadable", target.Name, problem);
                ImportButton.IsEnabled = false;
                return;
            }

            string name = TransferPackage.UniqueName(package.Name, names);
            bool renamed = name != package.Name;
            WhereText.Text = package.IsItem
                ? (renamed ? Loc.F("Transfer.Import.NewItemRenamed", name) : Loc.T("Transfer.Import.NewItem"))
                : (renamed ? Loc.F("Transfer.Import.NewProjectRenamed", name) : Loc.T("Transfer.Import.NewProject"));
        }

        private string UniqueCollectionName(string name) =>
            TransferPackage.UniqueName(name, profiles.Where(p => p.Type == ProfileType.Collection).Select(p => p.Name));

        private void ImportButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
