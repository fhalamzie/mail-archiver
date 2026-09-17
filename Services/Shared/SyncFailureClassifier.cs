using Azure.Identity;
using MailKit.Security;
using Microsoft.Kiota.Abstractions;

namespace MailArchiver.Services.Shared
{
    /// <summary>
    /// How a failed sync should slow down the next attempts of its account.
    /// </summary>
    public enum SyncFailureKind
    {
        /// <summary>Anything that may clear up on its own: network, timeouts, throttling, server errors.</summary>
        Soft,

        /// <summary>The provider refused the credentials. Retrying without a change only repeats the refusal.</summary>
        Hard
    }

    /// <summary>
    /// Sorts the exception that aborted an account sync into <see cref="SyncFailureKind"/>.
    ///
    /// The distinction exists because of what a retry costs. A rejected login retried every minute is
    /// sixty failed logins an hour, and mail providers answer such series by blocking the client IP -
    /// which takes every other account on the same provider down with it. A network hiccup retried
    /// every minute costs nothing and should come back quickly.
    ///
    /// Anything not recognised is Soft. A misclassification should cost an account minutes of delay,
    /// never a day.
    ///
    /// Pure (no I/O, no static state) so it can be unit-tested in isolation.
    /// </summary>
    public static class SyncFailureClassifier
    {
        public static SyncFailureKind Classify(Exception exception)
        {
            return Enumerate(exception).Any(IsCredentialRejection)
                ? SyncFailureKind.Hard
                : SyncFailureKind.Soft;
        }

        private static bool IsCredentialRejection(Exception e) => e switch
        {
            // IMAP LOGIN / AUTHENTICATE refused.
            AuthenticationException => true,
            // Graph client-credentials token request refused (bad secret, tenant or client id).
            AuthenticationFailedException => true,
            // Graph accepted a token but refuses the mailbox (revoked consent, missing permission).
            ApiException api when api.ResponseStatusCode is 401 or 403 => true,
            _ => false
        };

        // The exception chain, including every branch of an AggregateException.
        private static IEnumerable<Exception> Enumerate(Exception? exception)
        {
            if (exception == null)
                yield break;

            yield return exception;

            if (exception is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    foreach (var nested in Enumerate(inner))
                        yield return nested;
            }
            else
            {
                foreach (var nested in Enumerate(exception.InnerException))
                    yield return nested;
            }
        }
    }
}
