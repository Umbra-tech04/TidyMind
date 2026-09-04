namespace TidyMind
{
    public enum ProfileType
    {
        Project,
        Collection
    }

    public class Profile
    {
        public string Name { get; set; }
        public string Color { get; set; }
        public ProfileType Type { get; set; } = ProfileType.Project;
    }
}