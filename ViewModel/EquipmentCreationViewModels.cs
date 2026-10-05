using System;
using System.Collections.Generic;
using System.Linq;
using CommandCenter.Model;

namespace CommandCenter.ViewModel
{
    // One checkbox row in the Launch panel's "Existing Equipment Creation" list (see
    // BuildSectionView.xaml / Model/EquipmentCreationCatalog). Ticking it asks the owning
    // BuildSectionViewModel to add this item's create command to the tab's debug command list;
    // unticking removes it again. IsSelected is also kept in sync the other way - whenever the
    // debug command list changes for any reason (Add/Import/Edit/Delete, another tick), every row
    // re-checks whether its command is currently saved (see BuildSectionViewModel.
    // SyncEquipmentSelection) via SyncSelected, which deliberately does NOT call back into the
    // owner so that re-sync can't itself look like a user click. Both paths raise PropertyChanged
    // for IsSelected, which is what keeps the "Select All" checkboxes below up to date.
    public class EquipmentItemViewModel : ViewModelBase
    {
        private readonly Action<EquipmentItemViewModel, bool> _onToggled;
        private bool _isSelected;

        public EquipmentItemViewModel(EquipmentItemDefinition definition, Action<EquipmentItemViewModel, bool> onToggled)
        {
            Definition = definition;
            _onToggled = onToggled;
            Command = definition.Command;
        }

        public EquipmentItemDefinition Definition { get; }
        public string Name => Definition.Name;
        public string? Detail => Definition.Detail;
        public bool HasDetail => !string.IsNullOrWhiteSpace(Definition.Detail);
        public string ItemIdDisplay => Definition.ItemId;

        // The exact line this row adds to / removes from the debug command list.
        public string Command { get; }

        // Bound two-way to the row's CheckBox - only ever set by the user (or by the binding on
        // the user's behalf); the owner's re-sync goes through SyncSelected instead.
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
                _onToggled(this, value);
            }
        }

        // Updates the checkbox to match the saved debug command list without notifying the owner.
        public void SyncSelected(bool value)
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }
    }

    // Shared by EquipmentGroupViewModel and EquipmentTabViewModel: a "Select All" checkbox over a set
    // of rows. AllSelected is true when every row is ticked, false when none are, and null (shown as
    // the checkbox's mixed state) when only some are - recomputed whenever any row's IsSelected
    // changes, however it changed. Clicking it runs ToggleAllCommand rather than going through a
    // two-way binding on purpose: from the mixed state a click should always mean "select everything",
    // and a setter-based binding would hand us false there. ToggleAllCommand ticks every row if
    // anything is still unticked, otherwise unticks them all, and does it as ONE batch through the
    // owner (BuildSectionViewModel.OnEquipmentItemsBulkToggled) so a whole category is saved with a
    // single write of cmd_uidebug.txt instead of one per row.
    public abstract class EquipmentSelectionScope : ViewModelBase
    {
        private readonly IReadOnlyList<EquipmentItemViewModel> _items;

        protected EquipmentSelectionScope(
            string label,
            IReadOnlyList<EquipmentItemViewModel> items,
            Action<string, IReadOnlyList<EquipmentItemViewModel>, bool> onBulkToggled)
        {
            _items = items;

            foreach (EquipmentItemViewModel item in items)
            {
                item.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(EquipmentItemViewModel.IsSelected))
                    {
                        OnPropertyChanged(nameof(AllSelected));
                    }
                };
            }

            ToggleAllCommand = new RelayCommand(_ => onBulkToggled(label, _items, AllSelected != true), _ => _items.Count > 0);
        }

        public bool? AllSelected
        {
            get
            {
                int selected = _items.Count(item => item.IsSelected);
                if (selected == 0)
                {
                    return false;
                }

                return selected == _items.Count ? true : null;
            }
        }

        public RelayCommand ToggleAllCommand { get; }
    }

    // A run of items under one category header (e.g. Armor's "Warrior"/"Magician"...). Items with
    // no category at all land in a single group with no header - HasHeader = false hides it (and
    // its own "Select All", since the tab's one already covers exactly the same rows).
    public class EquipmentGroupViewModel : EquipmentSelectionScope
    {
        public EquipmentGroupViewModel(
            string? header,
            IReadOnlyList<EquipmentItemViewModel> items,
            Action<string, IReadOnlyList<EquipmentItemViewModel>, bool> onBulkToggled)
            : base(header ?? string.Empty, items, onBulkToggled)
        {
            Header = header;
            Items = items;
        }

        public string? Header { get; }
        public bool HasHeader => !string.IsNullOrWhiteSpace(Header);
        public IReadOnlyList<EquipmentItemViewModel> Items { get; }
    }

    // One tab of the equipment list (Primary Weapon, Armor, ...). Its "Select All" covers every row
    // on the tab, across all of its groups.
    public class EquipmentTabViewModel : EquipmentSelectionScope
    {
        public EquipmentTabViewModel(
            string title,
            IReadOnlyList<EquipmentGroupViewModel> groups,
            Action<string, IReadOnlyList<EquipmentItemViewModel>, bool> onBulkToggled)
            : base(title, groups.SelectMany(g => g.Items).ToList(), onBulkToggled)
        {
            Title = title;
            Groups = groups;
            AllItems = groups.SelectMany(g => g.Items).ToList();
        }

        public string Title { get; }
        public IReadOnlyList<EquipmentGroupViewModel> Groups { get; }
        public IReadOnlyList<EquipmentItemViewModel> AllItems { get; }
    }
}
