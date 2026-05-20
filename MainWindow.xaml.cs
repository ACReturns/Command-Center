using CommandCenter.Model;
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
        public BuildSelection buildSelection { get; set; }
        SaveDataHandler settings { get; set; }
        public MainWindow()
        {
            InitializeComponent();
            buildSelection = new BuildSelection();
            settings = new SaveDataHandler();
            Setting setting = settings.LoadData();
            this.DataContext = setting;
            DataContext = buildSelection;
        }
    }
}