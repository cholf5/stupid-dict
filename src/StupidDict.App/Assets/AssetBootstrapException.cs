namespace StupidDict.App.Assets;

/// <summary>
/// A bootstrap failure whose message is final and user-facing (download or
/// checksum stage): the stage that failed is already in the text, so the UI
/// shows <see cref="Exception.Message"/> verbatim instead of re-wrapping it.
/// </summary>
public sealed class AssetBootstrapException(string message, Exception? inner = null) : Exception(message, inner);
