using System.Collections.Concurrent;
using MailArchiver.Services.Shared;

namespace MailArchiver.Services
{
    /// <summary>
    /// In-memory backoff per account.
    ///
    /// Every failed sync pushes the account's next permitted start further out along a fixed ladder,
    /// every successful sync clears it. The scheduler keeps computing its own due times exactly as
    /// before; this only vetoes accounts that are due but still blocked. So an account waits
    /// max(ladder step, its own interval) - a failure never makes an account sync more often.
    ///
    /// Deliberately not persisted. A restart starts a broken account's run over, which costs one extra
    /// failed attempt before the ladder is back at five minutes; persisting would cost a schema
    /// migration.
    ///
    /// The ladders are constants, not settings: a threshold that can be turned tends to be turned up
    /// at the first false alarm instead of being investigated.
    /// </summary>
    public sealed class SyncBackoffTracker : ISyncBackoffTracker
    {
        // Wait after the n-th consecutive failure (index n-1); the last step repeats.
        internal static readonly TimeSpan[] HardSteps =
        {
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(60), TimeSpan.FromHours(4), TimeSpan.FromHours(24)
        };

        internal static readonly TimeSpan[] SoftSteps =
        {
            TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)
        };

        // A hard run alarms once it reaches the hour step. A soft run alarms after six failures,
        // about 67 minutes without a successful login: a provider blocking the client IP shows up as
        // refused connections and timeouts - soft - and would otherwise never alarm at all.
        internal const int HardAlarmThreshold = 2;
        internal const int SoftAlarmThreshold = 6;

        private readonly ConcurrentDictionary<int, SyncBackoffState> _states = new();

        public SyncBackoffState? RecordSuccess(int accountId)
        {
            return _states.TryRemove(accountId, out var ended) ? ended : null;
        }

        public SyncBackoffState RecordFailure(int accountId, SyncFailureKind kind, DateTime nowUtc)
        {
            return _states.AddOrUpdate(
                accountId,
                _ => Next(kind, 1, nowUtc),
                (_, current) => Next(
                    kind,
                    current.Kind == kind ? current.ConsecutiveFailures + 1 : 1,
                    nowUtc));
        }

        public void Reset(int accountId) => _states.TryRemove(accountId, out _);

        public bool IsBlocked(int accountId, DateTime nowUtc)
        {
            return _states.TryGetValue(accountId, out var state) && nowUtc < state.NotBeforeUtc;
        }

        public IReadOnlyList<int> AlarmingAccounts()
        {
            return _states
                .Where(s => s.Value.ConsecutiveFailures >= (s.Value.Kind == SyncFailureKind.Hard
                    ? HardAlarmThreshold
                    : SoftAlarmThreshold))
                .Select(s => s.Key)
                .OrderBy(id => id)
                .ToList();
        }

        public void Prune(IReadOnlySet<int> activeAccountIds)
        {
            foreach (var id in _states.Keys.Where(id => !activeAccountIds.Contains(id)).ToList())
                _states.TryRemove(id, out _);
        }

        internal static TimeSpan StepFor(SyncFailureKind kind, int consecutiveFailures)
        {
            var steps = kind == SyncFailureKind.Hard ? HardSteps : SoftSteps;
            return steps[Math.Min(consecutiveFailures, steps.Length) - 1];
        }

        private static SyncBackoffState Next(SyncFailureKind kind, int consecutiveFailures, DateTime nowUtc)
        {
            return new SyncBackoffState(kind, consecutiveFailures, nowUtc + StepFor(kind, consecutiveFailures));
        }
    }
}
