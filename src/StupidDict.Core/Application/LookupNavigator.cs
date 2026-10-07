using StupidDict.Core.Dictionary;

namespace StupidDict.Core.Application;

/// <summary>
/// Browser-style back/forward history for lookups. Every rendered result is
/// pushed, so not-found pages stay navigable too. Results are cached whole:
/// navigating back and forward re-renders from the cache and therefore never
/// re-queries the dictionary or rewrites the recent-search list.
/// </summary>
public sealed class LookupNavigator
{
    private const int MaxEntries = 100;

    private readonly List<LookupResult> _entries = [];
    private int _index = -1;

    public bool CanGoBack => _index > 0;

    public bool CanGoForward => _index >= 0 && _index < _entries.Count - 1;

    /// <summary>
    /// Records a rendered result as the newest history entry. Re-pushing the
    /// page already on top (e.g. double-clicking the headword you are reading)
    /// never grows the stack, and pushing past a back-step drops the forward
    /// branch, matching browser semantics.
    /// </summary>
    public void Push(LookupResult result)
    {
        if (_index >= 0 && IsSamePage(_entries[_index], result))
        {
            _entries[_index] = result;
            return;
        }

        if (_index < _entries.Count - 1)
            _entries.RemoveRange(_index + 1, _entries.Count - _index - 1);

        _entries.Add(result);
        if (_entries.Count > MaxEntries)
            _entries.RemoveAt(0);
        _index = _entries.Count - 1;
    }

    public LookupResult? GoBack() => CanGoBack ? _entries[--_index] : null;

    public LookupResult? GoForward() => CanGoForward ? _entries[++_index] : null;

    private static bool IsSamePage(LookupResult a, LookupResult b) =>
        string.Equals(a.Query.Trim(), b.Query.Trim(), StringComparison.OrdinalIgnoreCase);
}
