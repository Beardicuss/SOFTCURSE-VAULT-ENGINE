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
            Loaded += (s, e) => 
            {
                var sb = (System.Windows.Media.Animation.Storyboard)Resources["RotateRing"];
                sb.Begin();
                UpdateVisual();
            };
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
            // Ensure we have a valid size for the path
            // The Path is fixed size 150x150 in XAML, so we use that radius
            double radius = 150 / 2 - 6; // 75 - half stroke thickness (6) = 69
            double centerX = 150 / 2;
            double centerY = 150 / 2;

            double value = Math.Max(0, Math.Min(100, Value));
            double angle = value * 3.6; // 0-100 → 0-360 degrees

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
                // Animate text number
                // Note: We can't easily animate the string text, so we just set it
                // But we could animate a double and bind to it if needed.
                // For now, direct update is fine for the text.
                PercentageText.Text = $"{(int)value}%";
                _oldValue = value;
            }
            else
            {
                PercentageText.Text = $"{(int)value}%";
            }
        }
    }
}
