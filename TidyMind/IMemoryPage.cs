namespace TidyMind
{
    // A memory's page (ProjectView, CollectionView): what the main window asks of it whichever kind it is.
    public interface IMemoryPage
    {
        // The memory's notebook over the page (a search hit on one of its pages).
        void OpenNotebook();
    }
}
