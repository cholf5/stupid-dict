namespace StupidDict.App.Translation;

public interface ITranslationService : IDisposable
{
    TranslationConfig GetConfig();
    bool AutoPaused { get; }
    void ResetCircuit();
    TranslationResult? TryGetCached(string text);
    Task<TranslationResult> TranslateAsync(string text, bool bypassCache, CancellationToken cancellationToken);
    Task<TranslationTestResult> TestAsync(TranslationConfig config, CancellationToken cancellationToken);
    void ClearCache();
    long GetCacheSizeBytes();
}
