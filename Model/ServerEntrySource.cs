namespace CommandCenter.Model
{
    // Where a TabServerEntry came from - see TabServerEntry.Source. BuiltIn entries are seeded
    // from LaunchServerCatalog (the "known good" registry) the first time a tab of that category
    // is created or migrated; Custom entries are anything the user added themselves via Settings'
    // "+ Add Custom Server" form. Both live side by side in the same TabSettings.Servers list -
    // this is only tracked so Settings can tell them apart: built-ins can be connected/disconnected
    // but not deleted (they're the registry - see DraftServerViewModel.CanDelete), while custom
    // entries can be freely edited or removed.
    public enum ServerEntrySource
    {
        BuiltIn,
        Custom
    }
}
