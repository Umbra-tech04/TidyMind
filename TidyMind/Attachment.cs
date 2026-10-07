using System;

namespace TidyMind
{
    // A file attached to a project. The file itself is a copy in Attachments/ under StoredFileName (a GUID plus the
    // original extension), so renaming the project, its memory or the attachment never touches it.
    public class Attachment
    {
        public Guid Id { get; set; }
        public string DisplayName { get; set; }    // the original file name; Rename changes only this
        public string StoredFileName { get; set; }
        public long SizeBytes { get; set; }
        public DateTime AddedDate { get; set; }
    }
}
