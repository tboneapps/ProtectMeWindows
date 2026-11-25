using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ProtectMe.App;

public partial class ToggleToastDialog : Window
{
    public ToggleToastDialog(string title, string body)
    {
        InitializeComponent();
        TitleText.Text = title;
        BodyText.Text = body;
        Loaded += ToggleToastDialog_Loaded;
    }

    private async void ToggleToastDialog_Loaded(object sender, RoutedEventArgs e)
    {
        var dur = TimeSpan.FromMilliseconds(220);
        CardRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, dur));
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.9, 1.0, dur));
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.9, 1.0, dur));

        await Task.Delay(2500);
        if (IsLoaded)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fadeOut.Completed += (_, _) => Close();
            CardRoot.BeginAnimation(OpacityProperty, fadeOut);
        }
    }

    private void Window_Click(object sender, MouseButtonEventArgs e) => Close();
}
