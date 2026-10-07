using System.Windows;

namespace TidyMind
{
    // Customize Card: a project card's own color or picture, previewed on the real card. Swatches show the palette
    // colors as they are, because that's how the card wears them.
    public static class CardCustomizeDialog
    {
        // The choice, or null if the dialog was cancelled.
        public static BackgroundChoice Choose(DependencyObject owner, Project project)
        {
            BackgroundDialog dialog = new BackgroundDialog("Customize card", project.Name, project.GetCardBackground(),
                ImageStore.Cards, color => color,
                (type, color, pickedImage) =>
                {
                    ProjectCardModel model = ProjectCardModel.From(project);
                    model.BackgroundType = type;
                    model.BackgroundColor = color;
                    model.BackgroundImagePreview = pickedImage;
                    return ProjectCard.Build(model, 150, 13, compact: true).Card;
                });
            return dialog.Ask(owner);
        }
    }
}
