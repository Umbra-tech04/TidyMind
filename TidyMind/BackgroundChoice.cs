using System;
using System.Windows;

namespace TidyMind
{
    // What a background dialog (Customize Card, Edit Background) hands back; null from the dialog means cancelled.
    public class BackgroundChoice
    {
        public BackgroundFill Type { get; set; }
        public string Color { get; set; }     // when Type is Color
        public string ImageFile { get; set; } // when Type is Image and a new file was picked; null keeps the current one

        // Applies the choice to a stored background, the same way for a card and a page. A picked image is copied
        // into `store` only now; the old copy is deleted only once `save` has written the owner without it. If
        // saving fails, the old setting is put back and the new copy removed again, so no file is left without an
        // owner. Returns whether the background changed.
        public bool ApplyTo(BackgroundSetting current, Action<BackgroundSetting> set, Func<bool> save, ImageStore store)
        {
            string newImage = null;
            if (Type == BackgroundFill.Image && ImageFile != null)
            {
                newImage = store.Import(ImageFile);
                if (newImage == null)
                {
                    MessageBox.Show("Couldn't copy that image into " + store.Folder + ".", "Couldn't Save",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }
            }

            BackgroundSetting next = new BackgroundSetting(Type,
                Type == BackgroundFill.Color ? Color : null,
                Type == BackgroundFill.Image ? newImage ?? current.Image : null);
            set(next);

            if (save())
            {
                if (current.Image != null && current.Image != next.Image)
                    store.Delete(current.Image);
                return true;
            }

            set(current);
            store.Delete(newImage);
            return false;
        }
    }
}
