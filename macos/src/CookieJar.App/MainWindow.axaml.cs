// Builds with the .NET 8 SDK (Windows); not yet run on a Mac. Bindings go to MainViewModel.

using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using CookieJar.Core;

namespace CookieJar.App
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            // XAML cannot hand an int constant to a double property, so the slider range is set here (0 = Auto, then RamScale.Steps capped positions).
            RamSlider.Maximum = RamScale.Steps;
        }
    }
}
