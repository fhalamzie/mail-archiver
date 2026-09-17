using MailArchiver.Services.Shared;

namespace MailArchiver.Services
{
    /// <summary>
    /// A run of consecutive failed syncs of one account.
    /// </summary>
    /// <param name="Kind">The kind of the run; a failure of the other kind starts a new run.</param>
    /// <param name="ConsecutiveFailures">Failures in this run, starting at 1.</param>
    /// <param name="NotBeforeUtc">The scheduler may not start the account before this moment.</param>
    public sealed record SyncBackoffState(SyncFailureKind Kind, int ConsecutiveFailures, DateTime NotBeforeUtc);

    /// <summary>
    /// Holds back accounts whose syncs keep failing, so a short sync interval cannot turn a broken
    /// password into a stream of failed logins. See <see cref="SyncBackoffTracker"/>.
    /// </summary>
    public interface ISyncBackoffTracker
    {
        /// <summary>Ends the account's failure run. Returns the run that ended, or null if there was none.</summary>
        SyncBackoffState? RecordSuccess(int accountId);

        /// <summary>Extends (or starts) the account's failure run and returns its new state.</summary>
        SyncBackoffState RecordFailure(int accountId, SyncFailureKind kind, DateTime nowUtc);

        /// <summary>Forgets the account's failure run, e.g. after its credentials were edited.</summary>
        void Reset(int accountId);

        /// <summary>True while the account's failure run forbids starting it.</summary>
        bool IsBlocked(int accountId, DateTime nowUtc);

        /// <summary>Accounts whose failure run is long enough that a human should look at it.</summary>
        IReadOnlyList<int> AlarmingAccounts();

        /// <summary>Drops the state of every account not in <paramref name="activeAccountIds"/>.</summary>
        void Prune(IReadOnlySet<int> activeAccountIds);
    }
}
