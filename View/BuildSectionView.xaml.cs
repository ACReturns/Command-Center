using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CommandCenter.Model;
using CommandCenter.ViewModel;

namespace CommandCenter.View
{
    public partial class BuildSectionView : UserControl
    {
        public BuildSectionView()
        {
            InitializeComponent();
        }

        // Drag-and-drop onto the Documents box: accepts files/folders dropped from Explorer and
        // imports them the same way the "Add File..." button does (BuildSectionViewModel.
        // ImportDocumentPaths handles both individually-dropped files and whole folders).
        private void DocumentsDropZone_DragEnter(object sender, DragEventArgs e)
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }

        private void DocumentsDropZone_Drop(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                return;
            }

            if (DataContext is BuildSectionViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            {
                vm.ImportDocumentPaths(paths);
            }
        }

        // Double-click a document/folder to open it with its default associated app (a folder
        // just opens in Explorer, since both go through ShellExecute the same way).
        private void DocumentsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is ListBox { SelectedItem: DocumentEntry entry } && DataContext is BuildSectionViewModel vm)
            {
                vm.OpenDocumentCommand.Execute(entry);
            }
        }
    }
}
