namespace Mindweave.Application.Tools;

/// <summary>One search result: its title, its address and the service's short excerpt of it.</summary>
public sealed record SearchHit(string Title, Uri Url, string Snippet);

/// <summary>Why a search did not return results; each reads as a reason a person (and the model) can act on.</summary>
public enum SearchFailureKind
{
    /// <summary>No key is set for the search service.</summary>
    MissingKey,

    /// <summary>The service refused the key (HTTP 401 or 403).</summary>
    KeyRejected,

    /// <summary>The plan's or the account's credits are used up (Tavily 432, 433).</summary>
    QuotaExhausted,

    /// <summary>Too many requests (HTTP 429).</summary>
    RateLimited,

    /// <summary>The query was refused (HTTP 400, 422).</summary>
    BadQuery,

    /// <summary>The service failed or answered with something unreadable.</summary>
    ServiceError,

    /// <summary>No answer in time.</summary>
    Timeout,

    /// <summary>The service could not be reached.</summary>
    Network,
}

/// <summary>A search failed; <see cref="Exception.Message"/> says why in words for step details and the model. Never contains the key.</summary>
public sealed class SearchProviderException : Exception
{
    public SearchProviderException()
    {
    }

    public SearchProviderException(string message)
        : base(message)
    {
    }

    public SearchProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public SearchProviderException(SearchFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public SearchFailureKind Kind { get; } = SearchFailureKind.ServiceError;
}

/// <summary>
/// A search service (requirements §10.4.5, architecture §15.13; Q12: Tavily first). One adapter per service; the tools and the
/// run never name a service. The key is read from the credential store by the caller and goes only into the request.
/// </summary>
public interface ISearchProvider
{
    /// <summary>The stored name of the service (<c>websearch.provider</c>), for example <c>tavily</c>.</summary>
    string Id { get; }

    /// <summary>The name a person sees, for example Tavily.</summary>
    string DisplayName { get; }

    /// <summary>The host requests go to, recorded as the step's target.</summary>
    string Host { get; }

    /// <summary>Searches <paramref name="query"/>; at most <paramref name="maxResults"/> hits. Throws <see cref="SearchProviderException"/>.</summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int maxResults, string apiKey, CancellationToken cancellationToken);
}

/// <summary>
/// A page as read (architecture §15.8): the final address after redirects, its title, its readable text, the media type it
/// came as, the http(s) links it contains (absolute), and its size in bytes.
/// </summary>
public sealed record FetchedPage(Uri Url, string Title, string Text, string MediaType, IReadOnlyList<Uri> Links, long Bytes);

/// <summary>Why a page was not read.</summary>
public enum FetchFailureKind
{
    /// <summary>Not http or https (file://, ftp://, data:…).</summary>
    UnsupportedScheme,

    /// <summary>The address, or one it resolves to or redirects to, is on this computer or a private network.</summary>
    BlockedAddress,

    /// <summary>More than the allowed redirects.</summary>
    TooManyRedirects,

    /// <summary>Larger than the size limit.</summary>
    TooLarge,

    /// <summary>Not finished within the time limit.</summary>
    Timeout,

    /// <summary>Not text, HTML or PDF.</summary>
    UnsupportedType,

    /// <summary>The site answered with an error status.</summary>
    HttpStatus,

    /// <summary>Read, but no readable text in it (for example a page drawn only by scripts).</summary>
    NoText,

    /// <summary>The host could not be found or reached.</summary>
    Network,
}

/// <summary>A page could not be read; the message says why in words for step details and the model.</summary>
public sealed class PageFetchException : Exception
{
    public PageFetchException()
    {
    }

    public PageFetchException(string message)
        : base(message)
    {
    }

    public PageFetchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public PageFetchException(FetchFailureKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public FetchFailureKind Kind { get; } = FetchFailureKind.Network;
}

/// <summary>
/// Reads one web page (architecture §15.8): http(s) only, every address checked (resolved, and after every redirect), no
/// cookies or credentials, within size and time limits, turned into readable text.
/// </summary>
public interface IWebPageFetcher
{
    /// <summary>Reads <paramref name="url"/>. Throws <see cref="PageFetchException"/>.</summary>
    Task<FetchedPage> FetchAsync(Uri url, CancellationToken cancellationToken);
}
