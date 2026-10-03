using System;

namespace TidyMind
{
    public class QuickTodo
    {
        public Guid Id { get; set; }
        public string Text { get; set; }
        public DateTime DueDate { get; set; } // date only
        public bool IsDone { get; set; }
        public DateTime CreatedDate { get; set; }

        // When it was ticked: old finished todos are cleared by this, not by their due date.
        public DateTime? CompletedDate { get; set; }
    }
}
