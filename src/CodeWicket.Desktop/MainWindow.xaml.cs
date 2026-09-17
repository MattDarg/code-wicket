using System.Windows;
using CodeWicket.Core;

namespace CodeWicket.Desktop
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            Title = Branding.ProductName + " — Desktop host";
        }
    }
}
