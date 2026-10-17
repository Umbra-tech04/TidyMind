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
        // plate: drawn behind the page's header and tabs while a picture is the background, so their text stays
        // readable on it. excluded: parts of the page whose right-clicks aren't "empty space" (e.g. an open detail
        // overlay).
        public static void Attach(Grid page, Panel layer, Profile profile, Func<bool> saveProfiles, FrameworkElement plate,
            params FrameworkElement[] excluded)
        {
            Show(layer, profile, plate);

            MenuItem edit = new MenuItem { Header = Loc.T("Background.Edit") };
            edit.Click += (s, e) => Edit(page, layer, profile, saveProfiles, plate);
            page.ContextMenu = new ContextMenu { Items = { edit } };
            page.ContextMenu.Opened += (s, e) => edit.Header = Loc.T("Background.Edit"); // in the language of the moment

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

        private static void Show(Panel layer, Profile profile, FrameworkElement plate)
        {
            PageBackground.Fill(layer, profile.BackgroundType, profile.BackgroundColor, storedImage: profile.BackgroundImagePath);
            plate.Visibility = profile.BackgroundType == BackgroundFill.Image ? Visibility.Visible : Visibility.Collapsed;
        }

        // A picked picture is copied into BackgroundImages/ and an old one deleted only once the save worked
        // (BackgroundChoice.ApplyTo).
        private static void Edit(Grid page, Panel layer, Profile profile, Func<bool> saveProfiles, FrameworkElement plate)
        {
            BackgroundChoice choice = MemoryBackgroundDialog.Choose(page, profile);
            if (choice == null)
                return;

            bool saved = choice.ApplyTo(profile.GetBackground(), profile.SetBackground, () =>
            {
                if (saveProfiles())
                    return true;
                Msg.Show(Loc.T("Background.ProfilesNotSaved"), Loc.T("Common.CouldntSave"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }, ImageStore.Backgrounds);

            if (!saved) return;
            Show(layer, profile, plate);
            TourSignals.Raise(TourSignals.PageBackgroundChanged);
        }

        private static DependencyObject Parent(DependencyObject d)
        {
            return d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
    }
}
