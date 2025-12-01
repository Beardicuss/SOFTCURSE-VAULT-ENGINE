using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace BorderlandsStorageCleaner.WinStat.Controls
{
    /// <summary>
    /// Borderlands-themed circular progress indicator with smooth animations.
    /// </summary>
    public partial class CircularProgress : UserControl
    {
        private double _oldValue = 0;

        public CircularProgress()
        {
            InitializeComponent();
            SizeChanged += (s, e) => UpdateVisual();
        }

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        public static readonly DependencyProperty ValueProperty =
            DependencyProperty.Register(
                nameof(Value),
                typeof(double),
                typeof(CircularProgress),
                new PropertyMetadata(0.0, OnValueChanged));

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CircularProgress progress)
                progress.UpdateVisual();
        }

        private void UpdateVisual()
        {
            if (ActualWidth == 0 || ActualHeight == 0)
                return;

            double value = Math.Max(0, Math.Min(100, Value));
            double angle = value * 3.6; // 0-100 → 0-360 degrees
            double thickness = 12;
            double radius = Math.Min(ActualWidth, ActualHeight) / 2 - thickness;

            double centerX = ActualWidth / 2;
            double centerY = ActualHeight / 2;

            // Calculate end point on the arc
            double angleRad = (angle - 90) * Math.PI / 180;
            double endX = centerX + radius * Math.Cos(angleRad);
            double endY = centerY + radius * Math.Sin(angleRad);

            bool isLargeArc = angle > 180;

            var figure = new PathFigure
            {
                StartPoint = new Point(centerX, centerY - radius)
            };

            var arc = new ArcSegment
            {
                Point = new Point(endX, endY),
                Size = new Size(radius, radius),
                IsLargeArc = isLargeArc,
                SweepDirection = SweepDirection.Clockwise
            };

            figure.Segments.Clear();
            figure.Segments.Add(arc);

            var geometry = new PathGeometry();
            geometry.Figures.Clear();
            geometry.Figures.Add(figure);

            ProgressPath.Data = geometry;

            // Update text with smooth animation
            if (Math.Abs(value - _oldValue) > 0.1)
            {
                var animation = new DoubleAnimation
                {
                    From = _oldValue,
                    To = value,
                    Duration = TimeSpan.FromMilliseconds(150),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                animation.Completed += (s, e) =>
                {
                    PercentageText.Text = $"{(int)value}%";
                };

                _oldValue = value;
            }
            else
            {
                PercentageText.Text = $"{(int)value}%";
            }
        }
    }
}
