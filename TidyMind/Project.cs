using System;
using System.Collections.Generic;
using System.Text;

namespace TidyMind
{
    public class Project
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public string Status { get; set; }
        public List<TaskItem> Tasks { get; set; }
        public List<string> Notes { get; set; }

    }
}
