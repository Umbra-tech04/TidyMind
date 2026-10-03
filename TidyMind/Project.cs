using System;
using System.Collections.Generic;

namespace TidyMind
{
    public class Project
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public ProjectStatus Status { get; set; }
        public List<TaskItem> Tasks { get; set; }
        public string Notes { get; set; }
        public Guid TabId { get; set; }
        public int Order { get; set; }

        // Set whenever the project or its tasks change (DateTime.MinValue for projects from before this existed).
        public DateTime LastModified { get; set; }

        // 0-100 from tasks done; never 100 until every task is done (e.g. 199/200 would otherwise round up).
        public int CompletionPercent()
        {
            int total = Tasks?.Count ?? 0;
            if (total == 0) return 0;

            int done = Tasks.Count(t => t.IsDone);
            return done == total
                ? 100
                : Math.Min(99, (int)Math.Round(done * 100.0 / total, MidpointRounding.AwayFromZero));
        }
    }
}
