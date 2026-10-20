namespace StupidDict.App.Translation;

public enum TranslationDirection
{
    EnglishToChinese,
    ChineseToEnglish,
}

public enum TranslationFailureKind
{
    NotConfigured,
    InvalidConfig,
    Authentication,
    EndpointOrModel,
    RateLimited,
    Server,
    Timeout,
    Network,
    InvalidResponse,
    Refused,
    Incomplete,
}

/// <summary>User-facing translation state; None means "no translation section on this page".</summary>
public enum TranslationUiKind
{
    None,
    Manual,
    NotConfigured,
    Pending,
    Success,
    Failure,
}

public sealed record TranslationConfig(string ApiUrl, string ApiKey, string Model, bool AutoOnMiss)
{
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(Model);
}

public sealed record TranslationResult(
    TranslationFailureKind? Failure,
    string? Text = null,
    string? Model = null,
    bool FromCache = false,
    bool IsLocal = false,
    bool CanRetry = true,
    string? Detail = null)
{
    public bool IsSuccess => Failure is null && Text is not null;

    public static TranslationResult Success(string text, string model, bool fromCache, bool isLocal) =>
        new(null, text, model, fromCache, isLocal);

    public static TranslationResult Fail(TranslationFailureKind kind, bool canRetry = true, string? detail = null) =>
        new(kind, CanRetry: canRetry, Detail: detail);
}

public sealed record TranslationTestResult(
    bool Success,
    string? Model = null,
    TranslationFailureKind? Failure = null,
    string? Detail = null);

public sealed record TranslationUiState(
    TranslationUiKind Kind,
    string Query,
    string? Text = null,
    string? Model = null,
    bool FromCache = false,
    bool IsLocal = false,
    TranslationFailureKind? Failure = null,
    bool CanRetry = true)
{
    public static TranslationUiState None(string query) => new(TranslationUiKind.None, query);
}
