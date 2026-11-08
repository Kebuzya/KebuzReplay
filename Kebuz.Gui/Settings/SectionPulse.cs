using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Kebuz.Gui.Settings;

internal static class SectionPulse
{
    private static readonly Duration FadeDuration = new(TimeSpan.FromSeconds(1.8));
    private static readonly Duration StaticDuration = new(TimeSpan.FromSeconds(0.7));

    public static void Show(Border section)
    {
        if (section.TryFindResource("SecondColor") is not Color color)
        {
            return;
        }

        var brush = new SolidColorBrush(color) { Opacity = 0 };
        var animation = SystemParameters.ClientAreaAnimation ? Fade() : Static();

        animation.Completed += (_, _) =>
        {
            if (ReferenceEquals(section.BorderBrush, brush))
            {
                section.BorderBrush = Brushes.Transparent;
            }
        };

        section.BorderBrush = brush;
        brush.BeginAnimation(Brush.OpacityProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static DoubleAnimationUsingKeyFrames Fade()
    {
        return new DoubleAnimationUsingKeyFrames
        {
            Duration = FadeDuration,
            FillBehavior = FillBehavior.Stop,
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                },
                new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.8)))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                }
            }
        };
    }

    private static DoubleAnimationUsingKeyFrames Static()
    {
        return new DoubleAnimationUsingKeyFrames
        {
            Duration = StaticDuration,
            FillBehavior = FillBehavior.Stop,
            KeyFrames =
            {
                new DiscreteDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.Zero)),
                new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.7)))
            }
        };
    }
}
