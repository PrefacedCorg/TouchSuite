using System.Windows;

namespace TouchSuite.Ink;

/// <summary>设置窗口：四个开关，点一下立即生效并写入 settings.json。</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();

        ChkDComp.IsChecked = InkSettings.DComp;
        ChkPrediction.IsChecked = InkSettings.Prediction;
        ChkSmoothing.IsChecked = InkSettings.Smoothing;
        ChkTaper.IsChecked = InkSettings.Taper;
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        InkSettings.DComp = ChkDComp.IsChecked == true;
        InkSettings.Prediction = ChkPrediction.IsChecked == true;
        InkSettings.Smoothing = ChkSmoothing.IsChecked == true;
        InkSettings.Taper = ChkTaper.IsChecked == true;
        InkSettings.Save();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
