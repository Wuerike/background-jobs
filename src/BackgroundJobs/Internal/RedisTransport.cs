using StackExchange.Redis;

namespace BackgroundJobs.Internal;

// Only communication failures permit reconciliation. Server/storage/configuration
// errors must remain explicit, even when another attempt might happen to succeed.
internal static class RedisTransport
{
    internal static bool IsTransient(Exception error)
    {
        // The dependency can wrap authentication/protocol failures in a generic
        // "no connection available" exception. Inspect causes before retrying.
        for (Exception? cause = error; cause is not null; cause = cause.InnerException)
        {
            if (cause is System.Security.Authentication.AuthenticationException or RedisServerException ||
                cause is RedisConnectionException permanent && !CommunicationFailure(permanent.FailureType)) return false;
        }
        return error is RedisTimeoutException || error is RedisConnectionException connection && CommunicationFailure(connection.FailureType);
    }

    private static bool CommunicationFailure(ConnectionFailureType type) => type is
        ConnectionFailureType.UnableToResolvePhysicalConnection or ConnectionFailureType.SocketFailure or
        ConnectionFailureType.SocketClosed or ConnectionFailureType.Loading or ConnectionFailureType.UnableToConnect;

    internal static async Task<T> ReadAsync<T>(Func<Task<T>> read)
    {
        try { return await read().ConfigureAwait(false); }
        catch (Exception error) when (IsTransient(error)) { return await read().ConfigureAwait(false); }
    }
}
