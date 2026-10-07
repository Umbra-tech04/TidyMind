namespace TidyMind
{
    // What fills a customisable background: a project's card (Customize Card) or a memory's page
    // (Edit Background). None is the default look; anything saved before these existed loads as None.
    public enum BackgroundFill { None, Color, Image }

    // One background as stored on a Project or a Profile: only the field matching Type is set, the other is null.
    public readonly struct BackgroundSetting
    {
        public BackgroundSetting(BackgroundFill type, string color, string image)
        {
            Type = type;
            Color = color;
            Image = image;
        }

        public BackgroundFill Type { get; }
        public string Color { get; }  // "#RRGGBB", when Type is Color
        public string Image { get; }  // a file name inside the store's folder, when Type is Image
    }
}
