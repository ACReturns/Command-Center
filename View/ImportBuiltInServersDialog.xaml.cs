using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using CommandCenter.Model;

namespace CommandCenter.View
{
    // Settings -> a BuildSection tab's "+ Import Built-In Servers" (see DraftTabViewModel.
    // ImportBuiltInServersCommand). Shows LaunchServerCatalog's registry for a chosen category
    // (GMS/CMS/Live) as a checklist so the user can pull in whichever entries they can use instead
    // of retyping IP/port pairs by hand through AddEditServerDialog one at a time. Any category can
    // be imported into any tab - the dialog doesn't care whether the destination tab's own Category
    // matches what's being borrowed from, same as a General ("+ Add Tab") tab having no built-ins
    // of its own to begin with (see LaunchServerCatalog.SpecsFor).
    public partial class ImportBuiltInServersDialog : Window
    {
        // Order matches CategoryCombo's XAML items (GMS/CMS/Live Service Builds) - General is never
        // offered here since LaunchServerCatalog has nothing to import for it.
        private static readonly SectionCategory[] ImportableCategories =
        {
            SectionCategory.Gms,
            SectionCategory.Cms,
            SectionCategory.Live
        };

        // One row in the checklist - a fresh TabServerEntry from LaunchServerCatalog.BuiltInEntries
        // plus whether it's already present on the destination tab (matched by Host+Port, same
        // pairing that composes LaunchArgument). AlreadyAdded rows are shown but disabled/unchecked
        // rather than hidden, so re-opening this dialog after a partial import still shows the full
        // catalog instead of a shrinking list that's confusing to reconcile against what's already
        // there.
        private sealed class ImportRow : INotifyPropertyChanged
        {
            private bool _isSelected;

            public ImportRow(TabServerEntry source, bool alreadyAdded)
            {
                Source = source;
                AlreadyAdded = alreadyAdded;
                _isSelected = !alreadyAdded;
            }

            public TabServerEntry Source { get; }
            public bool AlreadyAdded { get; }
            public bool CanSelect => !AlreadyAdded;
            public string DisplayName => Source.DisplayName;
            public string Preview => Source.LaunchArgument;
            public Visibility AlreadyAddedVisibility => AlreadyAdded ? Visibility.Visible : Visibility.Collapsed;

            public bool IsSelected
            {
                get => _isSelected;
                set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // Host+Port pairs already on the destination tab, passed in by the caller (see
        // PromptForImport) - used only to flag ImportRow.AlreadyAdded, never mutated here.
        private readonly HashSet<(string Host, string Port)> _existing;
        private readonly ObservableCollection<ImportRow> _rows = new();

        private ImportBuiltInServersDialog(IEnumerable<TabServerEntry> existingServers)
        {
            InitializeComponent();

            _existing = existingServers
                .Select(s => (Host: s.Host, Port: s.Port))
                .ToHashSet();

            RowsControl.ItemsSource = _rows;
            LoadRows();
        }

        public IReadOnlyList<TabServerEntry> SelectedEntries { get; private set; } = Array.Empty<TabServerEntry>();

        private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => LoadRows();

        // Rebuilds the checklist from LaunchServerCatalog for whatever category is now selected -
        // called once from the constructor and again every time the combo box changes. A fresh
        // BuiltInEntries() call each time (rather than caching all 3 catalogs up front) matches
        // LaunchServerCatalog's own "always allocate new instances" contract, and there are only 3
        // categories to ever re-fetch, so there's no real cost to it.
        private void LoadRows()
        {
            // Guarded because SelectionChanged can fire during InitializeComponent, before
            // RowsControl/_existing are assigned yet - same reasoning AddEditServerDialog.
            // Mode_Checked already documents for its own panels.
            if (RowsControl == null || _existing == null)
            {
                return;
            }

            int index = CategoryCombo.SelectedIndex;

            if (index < 0 || index >= ImportableCategories.Length)
            {
                return;
            }

            _rows.Clear();

            foreach (var entry in LaunchServerCatalog.BuiltInEntries(ImportableCategories[index]))
            {
                bool alreadyAdded = _existing.Contains((entry.Host, entry.Port));
                _rows.Add(new ImportRow(entry, alreadyAdded));
            }
        }

        private void Import_Click(object sender, RoutedEventArgs e)
        {
            var selected = _rows.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Source).ToList();

            if (selected.Count == 0)
            {
                MessageBox.Show(this, "Check at least one server to import.", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelectedEntries = selected;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        // Shows the dialog modally; returns true and the entries the user checked (never
        // AlreadyAdded ones - those can't be selected to begin with) iff they clicked
        // "Import Selected". existingServers is only read to flag duplicates - the caller
        // (DraftTabViewModel.ImportBuiltInServers) is what actually adds the returned entries.
        public static bool PromptForImport(Window? owner, IEnumerable<TabServerEntry> existingServers, out IReadOnlyList<TabServerEntry> imported)
        {
            var dialog = new ImportBuiltInServersDialog(existingServers);

            if (owner != null)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            bool result = dialog.ShowDialog() == true;
            imported = result ? dialog.SelectedEntries : Array.Empty<TabServerEntry>();
            return result;
        }
    }
}
