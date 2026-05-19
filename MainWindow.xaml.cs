using CommandCenter.ViewModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CommandCenter
{
    public partial class MainWindow : Window
    {
        BuildSelection buildSelection { get; set; }
        public MainWindow()
        {
            InitializeComponent();
            buildSelection = new BuildSelection();
            DataContext = buildSelection;
        }
    }
}