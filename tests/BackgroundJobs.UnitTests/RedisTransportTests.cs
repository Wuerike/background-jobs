using BackgroundJobs.Internal;
using StackExchange.Redis;

namespace BackgroundJobs.UnitTests;

public sealed class RedisTransportTests
{
    [Theory]
    [InlineData(ConnectionFailureType.AuthenticationFailure)]
    [InlineData(ConnectionFailureType.ProtocolFailure)]
    [InlineData(ConnectionFailureType.ConnectionDisposed)]
    [InlineData(ConnectionFailureType.InternalFailure)]
    [InlineData(ConnectionFailureType.ResponseIntegrityFailure)]
    public void Permanent_connection_errors_never_authorize_retransmission(ConnectionFailureType type) =>
        Assert.False(RedisTransport.IsTransient(new RedisConnectionException(type, "permanent")));

    [Theory]
    [InlineData(ConnectionFailureType.SocketClosed)]
    [InlineData(ConnectionFailureType.SocketFailure)]
    [InlineData(ConnectionFailureType.UnableToConnect)]
    [InlineData(ConnectionFailureType.UnableToResolvePhysicalConnection)]
    [InlineData(ConnectionFailureType.Loading)]
    public void Communication_failures_require_reconciliation(ConnectionFailureType type) =>
        Assert.True(RedisTransport.IsTransient(new RedisConnectionException(type, "uncertain")));

    [Fact]
    public void Wrapped_authentication_failure_is_permanent_even_when_outer_failure_is_transport()
    {
        var authentication = new RedisConnectionException(ConnectionFailureType.AuthenticationFailure, "bad credentials");
        Assert.False(RedisTransport.IsTransient(new RedisConnectionException(ConnectionFailureType.UnableToResolvePhysicalConnection, "unavailable", authentication)));
        Assert.False(RedisTransport.IsTransient(new RedisConnectionException(ConnectionFailureType.SocketFailure, "TLS socket", new System.Security.Authentication.AuthenticationException("TLS"))));
    }

    [Fact]
    public async Task Read_policy_is_bounded_and_does_not_hide_server_or_generic_errors()
    {
        foreach (var error in new Exception[] { new RedisServerException("MISCONF persistence"), new IOException("generic"), new InvalidDataException("corrupt") })
        {
            var calls = 0;
            var result = await Assert.ThrowsAnyAsync<Exception>(() => RedisTransport.ReadAsync<int>(() => { calls++; return Task.FromException<int>(error); }));
            Assert.Same(error, result); Assert.Equal(1, calls);
        }
        var attempts = 0;
        await Assert.ThrowsAsync<RedisTimeoutException>(() => RedisTransport.ReadAsync<int>(() => { attempts++; return Task.FromException<int>(new RedisTimeoutException("timeout", CommandStatus.Unknown)); }));
        Assert.Equal(2, attempts);
    }
}
