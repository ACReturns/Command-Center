namespace CommandCenter.Model
{
    // Gms/Cms/Live: the 3 permanent tabs. An extra build section used to have to pick one of
    // these as its parent and inherited that category's server catalog + client executables.
    //
    // General: what "+ Add Tab" creates now that extras are independent top-level tabs rather
    // than sub-rows nested under a GMS/CMS/Live parent - picking a parent category stopped making
    // sense once there was no parent to nest under. A General tab still has its own Title,
    // BuildPath, and VersionNumber like any other build section; it just has no preset QA/staging
    // server list to launch against (LaunchServerCatalog.ServersFor returns none) and doesn't
    // offer "Pushed to Live" (see MainViewModel.CreateBuildSectionViewModel).
    public enum SectionCategory
    {
        Gms,
        Cms,
        Live,
        General
    }
}
