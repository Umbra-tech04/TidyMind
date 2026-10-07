using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ShapePath = System.Windows.Shapes.Path;

namespace TidyMind
{
    // What a project card shows, taken from a Project once (so the dashboard can carry it without the project).
    public class ProjectCardModel
    {
        public string Name { get; set; }
        public int Done { get; set; }
        public int Total { get; set; }
        public int Percent { get; set; }
        public string NextTask { get; set; } // the first unfinished task, null if none
        public int AttachmentCount { get; set; }
        public BackgroundFill BackgroundType { get; set; }
        public string BackgroundColor { get; set; }
        public string BackgroundImage { get; set; } // a file name in CardImages/
        public ImageSource BackgroundImagePreview { get; set; } // the Customize dialog's not-yet-copied pick; wins over BackgroundImage

        public static ProjectCardModel From(Project project)
        {
            var tasks = project.Tasks ?? Enumerable.Empty<TaskItem>().ToList();
            return new ProjectCardModel
            {
                Name = project.Name,
                Done = tasks.Count(t => t.IsDone),
                Total = tasks.Count,
                Percent = project.CompletionPercent(),
                NextTask = tasks.FirstOrDefault(t => !t.IsDone && !string.IsNullOrWhiteSpace(t.Title))?.Title.Trim(),
                AttachmentCount = project.Attachments?.Count ?? 0,
                BackgroundType = project.CardBackgroundType,
                BackgroundColor = project.CardBackgroundColor,
                BackgroundImage = project.CardBackgroundImagePath
            };
        }

        public bool Complete => Total > 0 && Done == Total;
    }

    // The built card: the host adds size-independent behaviour (click, menu, drag, hover lift) on top.
    public class ProjectCardVisual
    {
        public Grid Card { get; set; }
        public DropShadowEffect Shadow { get; set; }
        public ShapePath Progress { get; set; } // null until a task is done (nothing to pulse yet)
    }

    // The project card used by the project grid and, smaller, by the Home dashboard. The perimeter ring is a light
    // track that fills clockwise as tasks get done, and closes, with a faint azure wash, when every task is done.
    // The name sits top-left so long names wrap instead of being cut; the full-size card also says what's next.
    public static class ProjectCard
    {
        private static readonly Color Track = Color.FromRgb(0xEC, 0xEC, 0xE9);
        private static readonly Color CompleteWash = Color.FromRgb(0xF2, 0xF7, 0xFC);
        private static readonly Color ImagePlaceholder = Color.FromRgb(0x9A, 0xA0, 0xA6); // a picture card while it decodes

        // On a color-customised card the ring (and its percentage) use a brighter "pop" of that color instead of
        // Clean Azure, so it stands out on both the fill and the bottom scrim. Keyed by the Customize Card palette's
        // background hex; any other color keeps the default accent.
        private static readonly Dictionary<string, Color> RingAccents = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase)
        {
            ["#C1562F"] = Color.FromRgb(0xFF, 0x7A, 0x42), // Rust
            ["#C99A2E"] = Color.FromRgb(0xFF, 0xCC, 0x33), // Ochre
            ["#2F5D43"] = Color.FromRgb(0x5F, 0xD6, 0x8C), // Forest
            ["#1F6B6B"] = Color.FromRgb(0x3D, 0xDB, 0xC4), // Teal
            ["#2C4A6E"] = Color.FromRgb(0x5B, 0x9D, 0xFF), // Ink
            ["#2E7BC4"] = Color.FromRgb(0x6F, 0xC3, 0xFF), // Azure
            ["#6B3B5E"] = Color.FromRgb(0xE8, 0x61, 0x9A), // Plum
            ["#9C3D3D"] = Color.FromRgb(0xFF, 0x5C, 0x5C), // Brick
            ["#51637A"] = Color.FromRgb(0x8F, 0xD1, 0xFF), // Slate
            ["#332F2B"] = Color.FromRgb(0xFF, 0xB6, 0x48)  // Charcoal
        };

        // compact: the Home tile (smaller type, no "Next" line).
        public static ProjectCardVisual Build(ProjectCardModel model, double size, double radius, bool compact)
        {
            double thickness = compact ? 3 : 4;
            double pad = compact ? 13 : 18;
            Brush colorAccent = ColorCardAccent(model); // null unless the card has a palette color
            Brush ringBrush = colorAccent ?? CardEffects.Accent;

            Grid card = new Grid { Width = size, Height = size };

            // A customised card (Customize Card) fills with its color or image; a missing image falls back to plain.
            // A picture not decoded yet this session starts as a neutral placeholder and is swapped in when ready.
            Brush customFill = CustomFill(model, out string pendingImage);

            DropShadowEffect shadow = CardEffects.CreateShadow();
            Border background = new Border
            {
                Background = customFill ?? (model.Complete ? new SolidColorBrush(CompleteWash) : Brushes.White),
                CornerRadius = new CornerRadius(radius), // also clips an image to the card's rounded square
                Effect = shadow
            };
            card.Children.Add(background);
            if (pendingImage != null)
                ShowWhenDecoded(background, ImageStore.Cards.LoadAsync(pendingImage));

            PathGeometry ring = CardEffects.BuildRingGeometry(size, radius, thickness);
            ShapePath track = new ShapePath { Data = ring, Stroke = new SolidColorBrush(Track), StrokeThickness = thickness };

            ShapePath progress = null;
            if (model.Done > 0)
            {
                progress = new ShapePath
                {
                    Data = ring,
                    Stroke = ringBrush,
                    StrokeThickness = thickness,
                    StrokeDashCap = PenLineCap.Round
                };

                // A complete ring is drawn solid: a dash exactly one perimeter long can leave a hairline seam.
                if (!model.Complete)
                {
                    double perimeter = CardEffects.RingPerimeter(size, radius, thickness);
                    // WPF dash lengths are in multiples of StrokeThickness, not pixels.
                    progress.StrokeDashArray = new DoubleCollection
                    {
                        perimeter * model.Done / model.Total / thickness,
                        perimeter / thickness
                    };
                }

            }
            // In a Canvas so the pulse's thicker stroke and glow aren't layout-clipped to the card.
            Canvas progressLayer = progress == null ? null : new Canvas { Children = { progress } };

            if (customFill == null)
            {
                // The plain card, drawn exactly as before.
                card.Children.Add(track);
                if (progressLayer != null)
                    card.Children.Add(progressLayer);
                card.Children.Add(Content(model, ringBrush, pad, compact));
            }
            else
            {
                // Over a color or picture: a dark scrim along the bottom with white text on it, and the ring on top
                // of everything so its progress stays readable whatever is behind it.
                card.Children.Add(Scrim(size, radius));
                card.Children.Add(CustomContent(model, colorAccent, pad, compact));
                card.Children.Add(track);
                if (progressLayer != null)
                    card.Children.Add(progressLayer);
            }

            return new ProjectCardVisual { Card = card, Shadow = shadow, Progress = progress };
        }

        // The ring accent for a card filled with one of the palette colors; null otherwise (plain, image, other color).
        private static Brush ColorCardAccent(ProjectCardModel model)
        {
            if (model.BackgroundType != BackgroundFill.Color || model.BackgroundColor == null)
                return null;
            return RingAccents.TryGetValue(model.BackgroundColor.Trim(), out Color accent) ? new SolidColorBrush(accent) : null;
        }

        // The card's own fill, or null for the plain card. For a picture that exists but hasn't been decoded yet
        // this session, the fill is the placeholder and `pendingImage` names the file to decode off the UI thread.
        private static Brush CustomFill(ProjectCardModel model, out string pendingImage)
        {
            pendingImage = null;
            switch (model.BackgroundType)
            {
                case BackgroundFill.Color:
                    Color? color = ParseColor(model.BackgroundColor);
                    return color.HasValue ? new SolidColorBrush(color.Value) : null;
                case BackgroundFill.Image:
                    ImageSource image = model.BackgroundImagePreview ?? ImageStore.Cards.TryGetLoaded(model.BackgroundImage);
                    if (image != null)
                        return ImageFill(image);
                    if (!ImageStore.Cards.Exists(model.BackgroundImage))
                        return null;
                    pendingImage = model.BackgroundImage;
                    return new SolidColorBrush(ImagePlaceholder);
                default:
                    return null;
            }
        }

        private static Brush ImageFill(ImageSource image) => new ImageBrush(image) { Stretch = Stretch.UniformToFill };

        // Once the worker has decoded the picture, it's put on the card on the card's own UI thread (explicitly, not
        // by relying on whatever context the await happens to resume in). A card rebuilt meanwhile is simply no
        // longer on screen; setting its background is harmless. An unreadable file keeps the placeholder.
        private static async void ShowWhenDecoded(Border background, Task<BitmapImage> decoding)
        {
            BitmapImage image = await decoding.ConfigureAwait(false);
            if (image != null)
                background.Dispatcher.BeginInvoke(new Action(() => background.Background = ImageFill(image)));
        }

        public static Color? ParseColor(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;
            try
            {
                return (Color)ColorConverter.ConvertFromString(hex);
            }
            catch (FormatException)
            {
                return null;
            }
        }

        // The bottom 40% of the card, from clear to 75% black, with the card's bottom corners.
        private static FrameworkElement Scrim(double size, double radius)
        {
            return new Border
            {
                Height = size * 0.4,
                VerticalAlignment = VerticalAlignment.Bottom,
                CornerRadius = new CornerRadius(0, 0, radius, radius),
                IsHitTestVisible = false,
                Background = new LinearGradientBrush(Color.FromArgb(0x00, 0, 0, 0), Color.FromArgb(0xBF, 0, 0, 0), 90)
            };
        }

        // A customised card's text: name (two lines) and footer, together at the bottom, in white over the scrim.
        // The "Next" line is left out so all of it fits in the scrim. A soft shadow keeps the letters legible where
        // the scrim is still faint.
        // percentBrush: the color card's ring accent, so the percentage matches the ring; null keeps it white.
        private static FrameworkElement CustomContent(ProjectCardModel model, Brush percentBrush, double pad, bool compact)
        {
            double nameLine = compact ? 17 : 22;
            StackPanel content = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(pad, pad, pad, pad - 3),
                IsHitTestVisible = false,
                Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 0, BlurRadius = 6, Opacity = 0.5 }
            };

            content.Children.Add(new TextBlock
            {
                Text = model.Name,
                FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont"),
                FontSize = compact ? 13 : 17,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = nameLine,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = nameLine * 2
            });

            Grid footer = new Grid { Margin = new Thickness(0, compact ? 3 : 6, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // attached files
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // percentage / status
            footer.Children.Add(new TextBlock
            {
                Text = TaskSummary(model),
                FontSize = compact ? 10.5 : 11.5,
                Foreground = new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            AddAttachmentBadge(footer, model, new SolidColorBrush(Color.FromArgb(0xE0, 0xFF, 0xFF, 0xFF)), compact);
            string right = PercentText(model);
            if (right != null)
            {
                TextBlock state = new TextBlock
                {
                    Text = right,
                    FontSize = compact ? 10.5 : 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = percentBrush ?? Brushes.White,
                    Margin = new Thickness(8, 0, 0, 0)
                };
                Typography.SetNumeralAlignment(state, FontNumeralAlignment.Tabular);
                Grid.SetColumn(state, 2);
                footer.Children.Add(state);
            }
            content.Children.Add(footer);
            return content;
        }

        // Name at the top; at the bottom, "Next: …" (full size only) and the footer.
        private static FrameworkElement Content(ProjectCardModel model, Brush ringBrush, double pad, bool compact)
        {
            Brush main = (Brush)Application.Current.FindResource("TextMain");
            Brush sub = (Brush)Application.Current.FindResource("TextSub");

            DockPanel content = new DockPanel { Margin = new Thickness(pad, pad + 1, pad, pad - 3), LastChildFill = true };

            // Footer: what the tasks add up to, and the percentage (or the status, when it isn't simply active).
            Grid footer = new Grid { Margin = new Thickness(0, compact ? 4 : 8, 0, 0) };
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // attached files
            footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // percentage / status
            footer.Children.Add(new TextBlock
            {
                Text = TaskSummary(model),
                FontSize = compact ? 10.5 : 11.5,
                Foreground = sub,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Bottom
            });
            AddAttachmentBadge(footer, model, sub, compact);
            string right = PercentText(model);
            if (right != null)
            {
                TextBlock state = new TextBlock
                {
                    Text = right,
                    FontSize = compact ? 10.5 : 11.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = ringBrush,
                    Margin = new Thickness(8, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                Typography.SetNumeralAlignment(state, FontNumeralAlignment.Tabular);
                Grid.SetColumn(state, 2);
                footer.Children.Add(state);
            }
            DockPanel.SetDock(footer, Dock.Bottom);
            content.Children.Add(footer);

            if (!compact && model.NextTask != null)
            {
                TextBlock next = new TextBlock
                {
                    Text = "Next: " + model.NextTask,
                    FontSize = 12,
                    LineHeight = 16,
                    LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                    MaxHeight = 32,
                    Foreground = (Brush)Application.Current.FindResource("TextBody"),
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 6, 0, 0)
                };
                DockPanel.SetDock(next, Dock.Bottom);
                content.Children.Add(next);
            }

            double nameSize = compact ? 13 : 17;
            double nameLine = compact ? 17 : 22;
            int nameLines = compact ? 3 : (model.NextTask != null ? 3 : 4);
            content.Children.Add(new TextBlock
            {
                Text = model.Name,
                FontFamily = (FontFamily)Application.Current.FindResource("DisplayFont"),
                FontSize = nameSize,
                FontWeight = FontWeights.SemiBold,
                Foreground = main,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                LineHeight = nameLine,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                MaxHeight = nameLine * nameLines,
                VerticalAlignment = VerticalAlignment.Top,
            });

            return content;
        }

        // A paperclip and how many files are attached, in the footer beside the percentage (on a customised card
        // that's on the dark scrim, so it reads on any color or picture). Nothing when there are none.
        private static void AddAttachmentBadge(Grid footer, ProjectCardModel model, Brush brush, bool compact)
        {
            if (model.AttachmentCount == 0)
                return;

            double size = compact ? 10.5 : 11.5;
            StackPanel badge = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Bottom
            };
            badge.Children.Add(new TextBlock
            {
                Text = "\uE723", // Segoe MDL2: Attach (a paperclip)
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = size,
                Foreground = brush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 3, 0)
            });
            badge.Children.Add(new TextBlock { Text = model.AttachmentCount.ToString(), FontSize = size, Foreground = brush });
            Grid.SetColumn(badge, 1);
            footer.Children.Add(badge);
        }

        private static string TaskSummary(ProjectCardModel model)
        {
            if (model.Total == 0) return "No tasks yet";
            // Complete: the right side already says "100%", so the left just counts the tasks.
            if (model.Complete) return model.Total == 1 ? "1 task" : model.Total + " tasks";
            return model.Done + " of " + model.Total + (model.Total == 1 ? " task" : " tasks");
        }

        // The percentage once there's something to measure.
        private static string PercentText(ProjectCardModel model)
        {
            return model.Total == 0 ? null : model.Percent + "%";
        }
    }
}
