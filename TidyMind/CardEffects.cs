using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace TidyMind
{
    internal static class CardEffects
    {
        public const double CardSize = 190;
        public const double CardRadius = 16;

        public static SolidColorBrush Accent => (SolidColorBrush)Application.Current.FindResource("Accent");

        public static DropShadowEffect CreateShadow()
        {
            return new DropShadowEffect
            {
                Color = Colors.Black,
                Direction = 270,
                ShadowDepth = 2,
                BlurRadius = 12,
                Opacity = 0.08
            };
        }

        // RenderTransform (not Margin) so the lift doesn't reflow neighbouring cards.
        public static void AttachHoverLift(FrameworkElement card, DropShadowEffect shadow, Action onEnter = null)
        {
            TranslateTransform lift = new TranslateTransform();
            card.RenderTransform = lift;

            card.MouseEnter += (s, e) =>
            {
                Animate(lift, TranslateTransform.YProperty, -4);
                Animate(shadow, DropShadowEffect.ShadowDepthProperty, 6);
                Animate(shadow, DropShadowEffect.BlurRadiusProperty, 20);
                Animate(shadow, DropShadowEffect.OpacityProperty, 0.14);
                onEnter?.Invoke();
            };

            card.MouseLeave += (s, e) =>
            {
                Animate(lift, TranslateTransform.YProperty, 0);
                Animate(shadow, DropShadowEffect.ShadowDepthProperty, 2);
                Animate(shadow, DropShadowEffect.BlurRadiusProperty, 12);
                Animate(shadow, DropShadowEffect.OpacityProperty, 0.08);
            };
        }

        // Clockwise from top-centre, inset by half the stroke so its outer edge matches the card corner.
        public static PathGeometry BuildRingGeometry(double size, double radius, double thickness)
        {
            double inset = thickness / 2;
            double left = inset, top = inset, right = size - inset, bottom = size - inset;
            double r = radius - inset;
            Size arc = new Size(r, r);

            PathSegment[] segments =
            {
                new LineSegment(new Point(right - r, top), true),
                new ArcSegment(new Point(right, top + r), arc, 0, false, SweepDirection.Clockwise, true),
                new LineSegment(new Point(right, bottom - r), true),
                new ArcSegment(new Point(right - r, bottom), arc, 0, false, SweepDirection.Clockwise, true),
                new LineSegment(new Point(left + r, bottom), true),
                new ArcSegment(new Point(left, bottom - r), arc, 0, false, SweepDirection.Clockwise, true),
                new LineSegment(new Point(left, top + r), true),
                new ArcSegment(new Point(left + r, top), arc, 0, false, SweepDirection.Clockwise, true),
                new LineSegment(new Point(size / 2, top), true)
            };

            PathGeometry geometry = new PathGeometry(new[] { new PathFigure(new Point(size / 2, top), segments, true) });
            geometry.Freeze();
            return geometry;
        }

        public static double RingPerimeter(double size, double radius, double thickness)
        {
            double side = size - thickness;
            double r = radius - thickness / 2;
            return 4 * (side - 2 * r) + 2 * Math.PI * r;
        }

        // Fires on release only if the press also began here — a real click, not a drag-release.
        public static void AttachClick(UIElement element, Action onClick)
        {
            bool pressed = false;
            element.MouseLeftButtonDown += (s, e) => pressed = true;
            element.MouseLeave += (s, e) => pressed = false;
            element.MouseLeftButtonUp += (s, e) =>
            {
                if (!pressed) return;
                pressed = false;
                onClick();
            };
        }

        // Throbs the ring only (thicker stroke + blue glow). Solid rings only: dash lengths scale with StrokeThickness.
        public static void RingHeartbeat(Shape ring)
        {
            double rest = ring.StrokeThickness;
            DropShadowEffect glow = new DropShadowEffect { Color = GlowColor(ring), ShadowDepth = 0, BlurRadius = 0, Opacity = 0.85 };
            ring.Effect = glow;

            // Glow must stay inside the 12px card margin, or the scroll area clips it into a hard square edge.
            DoubleAnimationUsingKeyFrames glowBeat = Beat(0, 8, 5.5);
            glowBeat.Completed += (s, e) =>
            {
                if (ring.Effect == glow)
                    ring.Effect = null;
            };

            ring.BeginAnimation(Shape.StrokeThicknessProperty, Beat(rest, rest + 2.5, rest + 1.7));
            glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, glowBeat);
        }

        // A ring glows in its own color (a color-customised card's ring isn't azure).
        public static Color GlowColor(Shape ring)
        {
            return ring.Stroke is SolidColorBrush stroke ? stroke.Color : Accent.Color;
        }

        // "Lub-dub": a strong first beat, a short gap, a softer second beat, then rest.
        private static DoubleAnimationUsingKeyFrames Beat(double rest, double firstPeak, double secondPeak)
        {
            DoubleAnimationUsingKeyFrames beat = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(120),
                FillBehavior = FillBehavior.Stop
            };
            beat.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            beat.KeyFrames.Add(Key(firstPeak, 110, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            beat.KeyFrames.Add(Key(rest, 240, new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            beat.KeyFrames.Add(Key(secondPeak, 330, new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            beat.KeyFrames.Add(Key(rest, 560, new CubicEase { EasingMode = EasingMode.EaseInOut }));
            return beat;
        }

        private static EasingDoubleKeyFrame Key(double value, int ms, IEasingFunction easing)
        {
            return new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms)), easing);
        }

        private static void Animate(IAnimatable target, DependencyProperty property, double to)
        {
            DoubleAnimation animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            target.BeginAnimation(property, animation);
        }
    }
}
