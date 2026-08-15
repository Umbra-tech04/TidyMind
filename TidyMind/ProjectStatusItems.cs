using System;
using System.Collections.Generic;
using System.Text;

namespace TidyMind
{
    public static class ProjectStatusItems
    {
        public static List<ProjectStatus> All { get; } = new List<ProjectStatus>
        {
            ProjectStatus.Active,
            ProjectStatus.Paused,
            ProjectStatus.Planned,
            ProjectStatus.Cancelled,
            ProjectStatus.Done
        };
    }
}
