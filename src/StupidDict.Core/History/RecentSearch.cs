namespace StupidDict.Core.History;

/// <summary>A previously entered query, as the user typed it.</summary>
public sealed record RecentSearch(string Query, DateTimeOffset QueriedAt);
