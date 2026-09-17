using MailArchiver.Services;
using MailArchiver.Services.Shared;

namespace MailArchiver.Tests.Services;

/// <summary>
/// The tracker is what stands between a one-minute sync interval and sixty failed logins an hour.
/// The ladders, the reset on success and the alarm thresholds are each easy to get subtly wrong -
/// an off-by-one here means an account silenced for a day after its first hiccup, or an IP block
/// nobody hears about.
/// </summary>
public class SyncBackoffTrackerTests
{
    private static readonly DateTime Now = new(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
    private const int Account = 7;

    private static SyncBackoffState FailTimes(SyncBackoffTracker tracker, SyncFailureKind kind, int times)
    {
        SyncBackoffState state = null!;
        for (var i = 0; i < times; i++)
            state = tracker.RecordFailure(Account, kind, Now);
        return state;
    }

    // ---- ladders ------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 60)]
    [InlineData(3, 240)]
    [InlineData(4, 1440)]
    [InlineData(9, 1440)]
    public void Hard_ladder(int failures, int expectedMinutes)
    {
        var state = FailTimes(new SyncBackoffTracker(), SyncFailureKind.Hard, failures);

        Assert.Equal(failures, state.ConsecutiveFailures);
        Assert.Equal(Now.AddMinutes(expectedMinutes), state.NotBeforeUtc);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(2, 5)]
    [InlineData(3, 15)]
    [InlineData(12, 15)]
    public void Soft_ladder(int failures, int expectedMinutes)
    {
        var state = FailTimes(new SyncBackoffTracker(), SyncFailureKind.Soft, failures);

        Assert.Equal(Now.AddMinutes(expectedMinutes), state.NotBeforeUtc);
    }

    [Fact]
    public void A_failure_of_the_other_kind_starts_a_new_run()
    {
        var tracker = new SyncBackoffTracker();
        FailTimes(tracker, SyncFailureKind.Soft, 4);

        var state = tracker.RecordFailure(Account, SyncFailureKind.Hard, Now);

        Assert.Equal(SyncFailureKind.Hard, state.Kind);
        Assert.Equal(1, state.ConsecutiveFailures);
        Assert.Equal(Now.AddMinutes(5), state.NotBeforeUtc);
    }

    // ---- blocking -----------------------------------------------------------------------------

    [Fact]
    public void An_account_without_failures_is_not_blocked()
    {
        Assert.False(new SyncBackoffTracker().IsBlocked(Account, Now));
    }

    [Fact]
    public void A_failed_account_is_blocked_until_its_step_has_passed()
    {
        var tracker = new SyncBackoffTracker();
        tracker.RecordFailure(Account, SyncFailureKind.Hard, Now);

        Assert.True(tracker.IsBlocked(Account, Now.AddMinutes(4).AddSeconds(59)));
        Assert.False(tracker.IsBlocked(Account, Now.AddMinutes(5)));
    }

    [Fact]
    public void Blocking_one_account_leaves_others_alone()
    {
        var tracker = new SyncBackoffTracker();
        tracker.RecordFailure(Account, SyncFailureKind.Hard, Now);

        Assert.False(tracker.IsBlocked(Account + 1, Now));
    }

    // ---- ending a run -------------------------------------------------------------------------

    [Fact]
    public void Success_ends_the_run_and_reports_it()
    {
        var tracker = new SyncBackoffTracker();
        FailTimes(tracker, SyncFailureKind.Hard, 3);

        var ended = tracker.RecordSuccess(Account);

        Assert.NotNull(ended);
        Assert.Equal(3, ended!.ConsecutiveFailures);
        Assert.False(tracker.IsBlocked(Account, Now));
        Assert.Equal(1, tracker.RecordFailure(Account, SyncFailureKind.Hard, Now).ConsecutiveFailures);
    }

    [Fact]
    public void Success_without_a_run_reports_nothing()
    {
        Assert.Null(new SyncBackoffTracker().RecordSuccess(Account));
    }

    [Fact]
    public void Reset_lifts_a_day_long_block_immediately()
    {
        var tracker = new SyncBackoffTracker();
        FailTimes(tracker, SyncFailureKind.Hard, 5);

        tracker.Reset(Account);

        Assert.False(tracker.IsBlocked(Account, Now));
        Assert.Empty(tracker.AlarmingAccounts());
    }

    [Fact]
    public void Prune_drops_accounts_that_are_no_longer_active()
    {
        var tracker = new SyncBackoffTracker();
        tracker.RecordFailure(1, SyncFailureKind.Hard, Now);
        tracker.RecordFailure(2, SyncFailureKind.Hard, Now);

        tracker.Prune(new HashSet<int> { 2 });

        Assert.False(tracker.IsBlocked(1, Now));
        Assert.True(tracker.IsBlocked(2, Now));
    }

    // ---- alarm --------------------------------------------------------------------------------

    [Fact]
    public void A_single_hard_failure_does_not_alarm_the_second_does()
    {
        var tracker = new SyncBackoffTracker();

        FailTimes(tracker, SyncFailureKind.Hard, 1);
        Assert.Empty(tracker.AlarmingAccounts());

        FailTimes(tracker, SyncFailureKind.Hard, 1);
        Assert.Equal(new[] { Account }, tracker.AlarmingAccounts());
    }

    [Fact]
    public void Soft_failures_alarm_only_from_the_sixth_in_a_row()
    {
        var tracker = new SyncBackoffTracker();

        FailTimes(tracker, SyncFailureKind.Soft, 5);
        Assert.Empty(tracker.AlarmingAccounts());

        FailTimes(tracker, SyncFailureKind.Soft, 1);
        Assert.Equal(new[] { Account }, tracker.AlarmingAccounts());
    }

    [Fact]
    public void Soft_alarm_threshold_is_reached_after_about_an_hour()
    {
        // The threshold is meant as "an hour without a login"; keep the ladder and the threshold
        // from drifting apart unnoticed.
        var waited = Enumerable.Range(1, SyncBackoffTracker.SoftAlarmThreshold)
            .Select(n => SyncBackoffTracker.StepFor(SyncFailureKind.Soft, n))
            .Aggregate(TimeSpan.Zero, (a, b) => a + b);

        Assert.InRange(waited.TotalMinutes, 60, 90);
    }

    // ---- concurrency --------------------------------------------------------------------------

    [Fact]
    public void Parallel_failures_on_different_accounts_are_all_counted()
    {
        var tracker = new SyncBackoffTracker();

        Parallel.For(0, 200, i => tracker.RecordFailure(i % 4, SyncFailureKind.Hard, Now));

        Assert.Equal(new[] { 0, 1, 2, 3 }, tracker.AlarmingAccounts());
        foreach (var id in Enumerable.Range(0, 4))
            Assert.Equal(50, tracker.RecordFailure(id, SyncFailureKind.Hard, Now).ConsecutiveFailures - 1);
    }
}
