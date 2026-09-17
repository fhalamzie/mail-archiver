using System.Net.Sockets;
using Azure.Identity;
using MailArchiver.Services.Shared;
using MailKit.Net.Imap;
using MailKit.Security;
using Microsoft.Graph.Models.ODataErrors;

namespace MailArchiver.Tests.Shared;

/// <summary>
/// Hard failures put an account on a ladder that reaches a day, soft ones stop at fifteen minutes.
/// Calling a network problem hard would silence a working mailbox for hours; calling a rejected
/// password soft would keep hammering the provider until it blocks the IP. Both directions matter.
/// </summary>
public class SyncFailureClassifierTests
{
    private static SyncFailureKind Classify(Exception ex) => SyncFailureClassifier.Classify(ex);

    // ---- hard ---------------------------------------------------------------------------------

    [Fact]
    public void Imap_login_rejected_is_hard()
    {
        Assert.Equal(SyncFailureKind.Hard, Classify(new AuthenticationException("Authentication failed.")));
    }

    [Fact]
    public void Graph_token_request_rejected_is_hard()
    {
        Assert.Equal(SyncFailureKind.Hard,
            Classify(new AuthenticationFailedException("AADSTS7000215: Invalid client secret provided.")));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public void Graph_unauthorized_or_forbidden_is_hard(int status)
    {
        Assert.Equal(SyncFailureKind.Hard, Classify(new ODataError { ResponseStatusCode = status }));
    }

    [Fact]
    public void Hard_failure_wrapped_in_another_exception_is_hard()
    {
        var wrapped = new InvalidOperationException("sync failed",
            new ImapProtocolException("outer", new AuthenticationException("Authentication failed.")));

        Assert.Equal(SyncFailureKind.Hard, Classify(wrapped));
    }

    [Fact]
    public void Hard_failure_inside_an_aggregate_is_hard()
    {
        var aggregate = new AggregateException(
            new TimeoutException(),
            new AuthenticationFailedException("rejected"));

        Assert.Equal(SyncFailureKind.Hard, Classify(aggregate));
    }

    // ---- soft ---------------------------------------------------------------------------------

    [Fact]
    public void Connection_refused_is_soft()
    {
        Assert.Equal(SyncFailureKind.Soft, Classify(new SocketException((int)SocketError.ConnectionRefused)));
    }

    [Fact]
    public void Timeout_is_soft()
    {
        Assert.Equal(SyncFailureKind.Soft, Classify(new TimeoutException()));
    }

    [Fact]
    public void Imap_protocol_error_is_soft()
    {
        Assert.Equal(SyncFailureKind.Soft, Classify(new ImapProtocolException("The IMAP server has unexpectedly disconnected.")));
    }

    [Theory]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    [InlineData(404)]
    public void Graph_throttling_server_error_or_other_status_is_soft(int status)
    {
        Assert.Equal(SyncFailureKind.Soft, Classify(new ODataError { ResponseStatusCode = status }));
    }

    [Fact]
    public void Unknown_exception_is_soft()
    {
        Assert.Equal(SyncFailureKind.Soft, Classify(new InvalidOperationException("something else")));
    }
}
