using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // The preview before a package is imported: what it is, what's in it, when and with which version it was
    // exported, and where it goes. A project goes into a project memory (the open one by default); a collection
    // becomes a new collection. Either way it's a new copy; the name it gets there is said if it changes.
    public partial class ImportDialog : Window
    {
        private readonly OpenedPackage package;
        private readonly List<Profile> profiles;

        public ImportDialog(OpenedPackage package, List<Profile> profiles, Profile? current)
        {
            InitializeComponent();
            this.package = package;
            this.profiles = profiles;

            List<Profile> projectMemories = profiles.Where(p => p.Type == ProfileType.Project).OrderBy(p => p.Order).ToList();
            // Items of their own (the app's ComboBox look shows an item's content, not DisplayMemberPath), by name.
            foreach (Profile memory in projectMemories)
                TargetBox.Items.Add(new ComboBoxItem { Content = memory.Name, Tag = memory });
            Profile? chosen = current != null && projectMemories.Contains(current) ? current : projectMemories.FirstOrDefault();
            TargetBox.SelectedIndex = chosen == null ? -1 : projectMemories.IndexOf(chosen);
            TargetPanel.Visibility = package.IsCollection || projectMemories.Count == 0 ? Visibility.Collapsed : Visibility.Visible;

            ShowPackage();
            Loc.Track(this, ShowPackage);
        }

        // The project memory chosen (null for a collection).
        public Profile? Target => package.IsCollection ? null : (TargetBox.SelectedItem as ComboBoxItem)?.Tag as Profile;

        private void ShowPackage()
        {
            string name = package.Name;
            if (package.IsCollection)
            {
                KindText.Text = Loc.F("Transfer.Import.Collection", name);
                CountsText.Text = Loc.F("Transfer.Import.CollectionCounts",
                    Loc.Plural("Transfer.Count.Items", package.Items), Loc.Plural("Transfer.Count.Pictures", package.Pictures));
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

        // Where it goes and under which name; with no project memory to put a project in, it can't be imported yet.
        private void ShowWhere()
        {
            if (package.IsCollection)
            {
                string unique = TransferPackage.UniqueName(package.Name,
                    profiles.Where(p => p.Type == ProfileType.Collection).Select(p => p.Name));
                WhereText.Text = unique == package.Name
                    ? Loc.T("Transfer.Import.NewCollection")
                    : Loc.F("Transfer.Import.NewCollectionRenamed", unique);
                ImportButton.IsEnabled = true;
                return;
            }

            if (!(Target is Profile target))
            {
                WhereText.Text = Loc.T("Transfer.Import.NoProjectMemory");
                ImportButton.IsEnabled = false;
                return;
            }
            // A memory whose projects can't be read takes nothing in (it's never saved over): said here, not after unpacking.
            if (!ProjectStore.TryLoad(target, out List<Project> projects, out string problem))
            {
                WhereText.Text = Loc.F("Transfer.Error.TargetUnreadable", target.Name, problem);
                ImportButton.IsEnabled = false;
                return;
            }
            string name = TransferPackage.UniqueName(package.Name, projects.Select(p => p.Name));
            WhereText.Text = name == package.Name
                ? Loc.T("Transfer.Import.NewProject")
                : Loc.F("Transfer.Import.NewProjectRenamed", name);
            ImportButton.IsEnabled = true;
        }

        private void ImportButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
