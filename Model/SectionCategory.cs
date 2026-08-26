namespace CommandCenter.Model
{
    // Which of the three permanent tabs an additional (user-added) build section belongs under -
    // it inherits that category's server catalog and shares the same client executables.
    public enum SectionCategory
    {
        Gms,
        Cms,
        Live
    }
}
