using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace TidyMind
{
    // Edit Background on a memory's page, the same for the project and the collection view: draws the memory's
    // background into a layer behind the page, and gives the page's empty space a right-click menu to change it.
    // The background is saved on the memory (profiles.json), so it's the same on every tab of that memory.
    public static class MemoryPageBackground
    {
        // page: the view's root grid; layer: a panel spanning it, behind its content.
        // saveProfiles: writes the main window's list of memories (the profile is an item of that list).
        // excluded: parts of the page whose right-clicks aren't "empty space" (e.g. an open detail overlay).
        public static void Attach(Grid page, Panel layer, Profile profile, Func<bool> saveProfiles, params FrameworkElement[] excluded)
        {
            Show(layer, profile);

            MenuItem edit = new MenuItem { Header = "Edit Background" };
            edit.Click += (s, e) => Edit(page, layer, profile, saveProfiles);
            page.ContextMenu = new ContextMenu { Items = { edit } };

            // Cards, tabs and text boxes have their own menus (WPF opens the nearest one); buttons don't, so a
            // right-click on one would otherwise fall through to the page.
            page.ContextMenuOpening += (s, e) =>
            {
                for (DependencyObject d = e.OriginalSource as DependencyObject; d != null && d != page; d = Parent(d))
                {
                    if (d is ButtonBase || excluded.Contains(d))
                    {
                        e.Handled = true;
                        return;
                    }
                }
            };
        }

        private static void Show(Panel layer, Profile profile)
        {
            PageBackground.Fill(layer, profile.BackgroundType, profile.BackgroundColor, storedImage: profile.BackgroundImagePath);
        }

        // A picked picture is copied into BackgroundImages/ and an old one deleted only once the save worked
        // (BackgroundChoice.ApplyTo).
        private static void Edit(Grid page, Panel layer, Profile profile, Func<bool> saveProfiles)
        {
            BackgroundChoice choice = MemoryBackgroundDialog.Choose(page, profile);
            if (choice == null)
                return;

            bool saved = choice.ApplyTo(profile.GetBackground(), profile.SetBackground, () =>
            {
                if (saveProfiles())
                    return true;
                MessageBox.Show("Couldn't save: profiles.json can't be written. The background wasn't changed.",
                    "Couldn't Save", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }, ImageStore.Backgrounds);

            if (saved)
                Show(layer, profile);
        }

        private static DependencyObject Parent(DependencyObject d)
        {
            return d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
    }
}
