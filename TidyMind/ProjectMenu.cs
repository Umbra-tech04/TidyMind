using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace TidyMind
{
    // The right-click menu of a project card, built here for both the project grid and the Home dashboard.
    // The menu does the asking (rename prompt, delete confirmation, card customisation); the view that owns the
    // project list provides how to save it, redraw, and take a project out of it.
    public static class ProjectMenu
    {
        // save: writes the owning list to disk, false if it couldn't (the view has already said so).
        // remove: takes the project out of the owning list (saving follows through `save`).
        public static ContextMenu Build(DependencyObject owner, Project project, Func<bool> save, Action redraw, Action remove)
        {
            ContextMenu menu = new ContextMenu();

            MenuItem rename = new MenuItem { Header = "Rename" };
            rename.Click += (s, e) =>
            {
                if (!Rename(owner, project)) return;
                save();
                redraw();
            };
            MenuItem customize = new MenuItem { Header = "Customize Card" };
            customize.Click += (s, e) =>
            {
                if (Customize(owner, project, save))
                    redraw();
            };
            MenuItem delete = new MenuItem { Header = "Delete" };
            delete.Click += (s, e) =>
            {
                if (!ConfirmDelete(project)) return;
                Delete(new[] { project }, _ => remove(), save);
                redraw();
            };
            MenuItem reminder = new MenuItem { Header = "Add Reminder" };
            reminder.Click += (s, e) => new AddReminderWindow(project.Name).ShowDialog();

            menu.Items.Add(rename);
            menu.Items.Add(customize);
            menu.Items.Add(delete);
            menu.Items.Add(reminder);
            menu.Items.Add(ExportMenu.Submenu(() => project.Name, (path, format) => ExportService.ExportProject(project, path, format)));
            menu.Items.Add(new Separator());
            menu.Items.Add(TextCopy.CopyItem(() => project.Name, "Copy name"));
            menu.Items.Add(TextCopy.CopyItem(() => AsText(project), "Copy with tasks"));
            return menu;
        }

        private static bool Rename(DependencyObject owner, Project project)
        {
            string newName = InputDialog.Prompt(owner, "Rename Project", "Enter a new name.", project.Name, "Rename");
            if (string.IsNullOrWhiteSpace(newName) || newName == project.Name) return false;

            project.Name = newName;
            project.LastModified = DateTime.Now;
            return true;
        }

        // Opens the card dialog and applies the choice (copying a picked image in, cleaning up the old one: see
        // BackgroundChoice.ApplyTo). Returns whether the card changed.
        private static bool Customize(DependencyObject owner, Project project, Func<bool> save)
        {
            BackgroundChoice choice = CardCustomizeDialog.Choose(owner, project);
            return choice != null && choice.ApplyTo(project.GetCardBackground(), project.SetCardBackground, save, ImageStore.Cards);
        }

        // Deleting one project (its menu) or several (a multi-selection's Delete): take them out of the owning list,
        // save, and once that worked delete their card pictures and attached files. Returns whether the save worked.
        public static bool Delete(IEnumerable<Project> projects, Action<Project> remove, Func<bool> save)
        {
            List<Project> doomed = projects.ToList();
            List<string> images = doomed.Where(p => p.CardBackgroundType == BackgroundFill.Image)
                .Select(p => p.CardBackgroundImagePath).ToList();
            List<Attachment> files = doomed.SelectMany(p => p.Attachments ?? new List<Attachment>()).ToList();

            foreach (Project project in doomed)
                remove(project);
            if (!save())
                return false;

            // Not needed any more
            foreach (string image in images)
                ImageStore.Cards.Delete(image);
            AttachmentStore.Delete(files);
            return true;
        }

        private static bool ConfirmDelete(Project project)
        {
            return MessageBox.Show("Delete '" + project.Name + "'?", "Confirm Delete",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        // Name, description and a checklist of the tasks, as plain text for pasting elsewhere.
        private static string AsText(Project project)
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
    }
}
