using OmniSharp.Extensions.LanguageServer.Protocol;

namespace UitkxLanguageServer;

/// <summary>
/// Thread-safe document text cache. Updated by <see cref="TextSyncHandler"/>.
/// </summary>
public sealed class DocumentStore
{
    private readonly Dictionary<string, string> _docs = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Set(DocumentUri uri, string text)
    {
        lock (_lock)
            _docs[uri.ToString()] = text;
    }

    public void Remove(DocumentUri uri)
    {
        lock (_lock)
            _docs.Remove(uri.ToString());
    }

    public bool TryGet(DocumentUri uri, out string text)
    {
        lock (_lock)
            return _docs.TryGetValue(uri.ToString(), out text!);
    }

    /// <summary>
    /// Looks up editor content by local file path (case-insensitive).
    /// Unlike <see cref="TryGet(DocumentUri, out string)"/> which uses the
    /// URI string as key, this method converts each stored URI to a local path
    /// and compares against <paramref name="localPath"/>.  Returns <c>true</c>
    /// if the file is open in the editor and its text is available.
    /// </summary>
    public bool TryGetByPath(string localPath, out string text)
    {
        // Both sides are canonicalised. A caller that built its path by joining
        // strings hands over forward slashes, while Uri.LocalPath gives
        // backslashes on Windows, so the raw comparison never matched for them -
        // which silently made every open document invisible to anything that
        // resolves a path itself rather than holding a DocumentUri.
        string wanted = Canon(localPath);
        lock (_lock)
        {
            foreach (var kvp in _docs)
            {
                try
                {
                    var uri = new System.Uri(kvp.Key);
                    if (uri.IsFile && string.Equals(
                        Canon(uri.LocalPath), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        text = kvp.Value;
                        return true;
                    }
                }
                catch { }
            }
        }
        text = null!;
        return false;
    }

    private static string Canon(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch
        {
            return path;
        }
    }

    /// <summary>Returns a snapshot of all currently open documents as (uriString, text) pairs.</summary>
    public IReadOnlyList<(string UriString, string Text)> GetAll()
    {
        lock (_lock)
        {
            var list = new List<(string, string)>(_docs.Count);
            foreach (var kvp in _docs)
                list.Add((kvp.Key, kvp.Value));
            return list;
        }
    }
}
