using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace TidyMind
{
    // Home's "Getting started" list: four first things, each ticked when the user's own data shows it done (the example
    // memories don't count), so nothing is recorded but whether the list is hidden or brought back
    // (AppSettings.Checklist). Each step's texts are "Checklist.<Step>.Title" and "Checklist.<Step>.Text".
    public static class GettingStarted
    {
        public static readonly string[] Steps = { "Project", "Task", "Reminder", "Item" };

        // In the order of Steps.
        public static bool[] Done(IEnumerable<Profile> profiles)
        {
            List<Profile> own = profiles.Where(p => p.IsSample != true).ToList();
            List<Project> projects = own.Where(p => p.Type == ProfileType.Project).SelectMany(ProjectStore.LoadOrEmpty).ToList();
            bool item = own.Where(p => p.Type == ProfileType.Collection).Any(p => EntityStore.LoadOrEmpty(p).Count > 0);
            return new[] { projects.Count > 0, projects.Any(p => p.Tasks?.Count > 0), AnyReminder(), item };
        }

        // Shown while something is left to do, unless hidden; brought back from ? it shows even with everything done.
        public static bool Shown(bool[] done)
        {
            string? state = AppSettings.Load().Checklist;
            return state == AppSettings.ChecklistShown || (state != AppSettings.ChecklistHidden && done.Contains(false));
        }

        // Read without the task check that showing the reminders list starts; an unreadable file counts as none.
        private static bool AnyReminder()
        {
            try
            {
                return ReminderManager.LoadReminders().Count > 0;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is JsonException)
            {
                return false;
            }
        }
    }
}
