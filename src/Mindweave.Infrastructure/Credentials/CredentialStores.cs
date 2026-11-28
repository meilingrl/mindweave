using Mindweave.Application.Credentials;

namespace Mindweave.Infrastructure.Credentials;

public static class CredentialStores
{
    /// <summary>
    /// How long a call to the system's key store may take (D2 §2.3, gap-native-interop-1). A locked store can wait for the
    /// user without end; the request then fails and gives its slot up instead of holding it.
    /// </summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The platform store, namespaced by channel so separate tracks and builds never share secrets, with every call limited
    /// to <see cref="AnswerWait"/>.
    /// </summary>
    public static ICredentialStore ForPlatform(string channel, TimeProvider time)
    {
        var name = $"Mindweave.{channel}";
        if (OperatingSystem.IsMacOS())
        {
            return Limited(new MacKeychainCredentialStore(name), AnswerWait, time);
        }

        if (OperatingSystem.IsWindows())
        {
            return Limited(new WindowsCredentialStore(name), AnswerWait, time);
        }

        return new UnsupportedCredentialStore();
    }

    /// <summary><paramref name="inner"/>, with every call failing as unavailable once it has taken <paramref name="wait"/>.</summary>
    internal static ICredentialStore Limited(ICredentialStore inner, TimeSpan wait, TimeProvider time) => new LimitedCredentialStore(inner, wait, time);

    /// <summary>
    /// A call the system does not answer in time fails as <see cref="CredentialUnavailableException"/>, which every caller
    /// already treats as a locked store. The native call cannot be taken back: it goes on in the background and its result is
    /// dropped. A write that lands after that leaves an item nothing refers to; a delete is retried, since the reference stays
    /// retired.
    /// </summary>
    private sealed class LimitedCredentialStore(ICredentialStore inner, TimeSpan wait, TimeProvider time) : ICredentialStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) =>
            LimitAsync(inner.ReadAsync(reference, cancellationToken), cancellationToken);

        public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken) =>
            LimitAsync(inner.WriteAsync(reference, secret, cancellationToken), cancellationToken);

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) =>
            LimitAsync(inner.DeleteAsync(reference, cancellationToken), cancellationToken);

        private async Task LimitAsync(Task call, CancellationToken cancellationToken)
        {
            try
            {
                await call.WaitAsync(wait, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                Observe(call);
                throw NoAnswer(ex);
            }
        }

        private async Task<T> LimitAsync<T>(Task<T> call, CancellationToken cancellationToken)
        {
            try
            {
                return await call.WaitAsync(wait, time, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                Observe(call);
                throw NoAnswer(ex);
            }
        }

        private CredentialUnavailableException NoAnswer(TimeoutException ex) =>
            new($"The system's key store did not answer within {wait.TotalSeconds:0} seconds; unlock it and retry.", ex);

        /// <summary>The abandoned call's own failure, if it ever fails, is not left unobserved.</summary>
        private static void Observe(Task call) =>
            _ = call.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Linux is a later target: say so clearly instead of storing secrets somewhere weaker.</summary>
    private sealed class UnsupportedCredentialStore : ICredentialStore
    {
        public Task<string?> ReadAsync(string reference, CancellationToken cancellationToken) => throw Unsupported();

        public Task WriteAsync(string reference, string secret, CancellationToken cancellationToken) => throw Unsupported();

        public Task DeleteAsync(string reference, CancellationToken cancellationToken) => throw Unsupported();

        private static CredentialUnavailableException Unsupported() =>
            new("No supported credential store on this platform yet.");
    }
}
