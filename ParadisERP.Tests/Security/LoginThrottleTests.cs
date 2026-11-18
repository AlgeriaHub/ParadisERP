using ParadisERP.Infrastructure.Auth;

namespace ParadisERP.Tests.Security;

/// <summary>
/// The cap on password guessing. It has to hold two things at once: an attacker cannot keep
/// guessing, and the shop cannot be locked out of its own back office for longer than a moment.
/// </summary>
public class LoginThrottleTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset now = start;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    private static (LoginThrottle Throttle, Clock Clock) New()
    {
        var clock = new Clock(new DateTimeOffset(2026, 9, 23, 8, 0, 0, TimeSpan.Zero));
        return (new LoginThrottle(clock), clock);
    }

    private static void Fail(LoginThrottle throttle, int times, string company = "shop", string user = "admin")
    {
        for (var i = 0; i < times; i++) throttle.Failed(company, user);
    }

    [Fact]
    public void A_run_of_wrong_passwords_is_stopped_before_the_server_hashes_another_one()
    {
        var (throttle, _) = New();

        Fail(throttle, LoginThrottle.MaxFailures - 1);
        Assert.Null(throttle.RetryAfter("shop", "admin"));

        Fail(throttle, 1);
        var wait = throttle.RetryAfter("shop", "admin");
        Assert.NotNull(wait);
        Assert.True(wait <= LoginThrottle.Cooldown);
    }

    [Fact]
    public void The_cooldown_is_a_moment_and_never_a_lockout()
    {
        var (throttle, clock) = New();
        Fail(throttle, LoginThrottle.MaxFailures);
        Assert.NotNull(throttle.RetryAfter("shop", "admin"));

        // The administrator whose username an attacker is guessing at waits a minute, not a
        // day, and the till on the counter was never involved: it sells on the token it holds
        // and unlocks offline on a PIN.
        clock.Advance(LoginThrottle.Cooldown + TimeSpan.FromSeconds(1));
        Assert.Null(throttle.RetryAfter("shop", "admin"));
    }

    [Fact]
    public void A_quiet_window_clears_the_count_and_the_right_password_clears_it_at_once()
    {
        var (throttle, clock) = New();

        Fail(throttle, LoginThrottle.MaxFailures - 1);
        clock.Advance(LoginThrottle.Window + TimeSpan.FromSeconds(1));
        Fail(throttle, 1);
        // The old failures are the account's own evidence that the run stopped, so they are not
        // held against a user who mistypes once a fortnight.
        Assert.Null(throttle.RetryAfter("shop", "admin"));

        Fail(throttle, LoginThrottle.MaxFailures);
        Assert.NotNull(throttle.RetryAfter("shop", "admin"));
        throttle.Succeeded("shop", "admin");
        Assert.Null(throttle.RetryAfter("shop", "admin"));
    }

    [Fact]
    public void One_account_being_guessed_at_never_stops_anybody_else_signing_in()
    {
        var (throttle, _) = New();
        Fail(throttle, LoginThrottle.MaxFailures * 3);

        // Per account, and per company: this is a multi-tenant deployment and one company's
        // traffic must not reach another's, even when the two happen to name a user the same.
        Assert.Null(throttle.RetryAfter("shop", "cashier"));
        Assert.Null(throttle.RetryAfter("other-shop", "admin"));
        Assert.NotNull(throttle.RetryAfter("shop", "admin"));
    }

    [Fact]
    public void Case_and_surrounding_space_do_not_buy_a_fresh_count()
    {
        var (throttle, _) = New();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            throttle.Failed("shop", i % 2 == 0 ? " Admin " : "ADMIN");

        // Usernames are compared case-insensitively when one is created, so varying the
        // spelling has to be the same account here or the cap is one line of script away.
        Assert.NotNull(throttle.RetryAfter("shop", "admin"));
    }

    [Fact]
    public void Two_hundred_guesses_fired_at_once_get_exactly_the_allowance_and_no_more()
    {
        var (throttle, _) = New();
        var allowed = 0;
        Parallel.For(0, 200, _ =>
        {
            if (throttle.TryBegin("shop", "admin", "203.0.113.7") is null) Interlocked.Increment(ref allowed);
        });

        // Reserved under one lock before any password is checked (security review, 2026-10-10):
        // the race that let a burst all read the same old count is gone.
        Assert.Equal(LoginThrottle.MaxFailures, allowed);
    }

    [Fact]
    public void Someone_guessing_from_outside_does_not_lock_the_real_user_out_from_their_own_desk()
    {
        var (throttle, _) = New();
        for (var i = 0; i < LoginThrottle.MaxFailures; i++) throttle.TryBegin("shop", "admin", "198.51.100.9");

        Assert.NotNull(throttle.TryBegin("shop", "admin", "198.51.100.9"));
        Assert.Null(throttle.TryBegin("shop", "admin", "192.0.2.10"));
    }

    [Fact]
    public void Guessing_spread_over_many_addresses_still_meets_the_account_ceiling()
    {
        var (throttle, _) = New();
        for (var i = 0; i < LoginThrottle.MaxAccountFailures; i++) throttle.TryBegin("shop", "admin", $"10.0.{i / 250}.{i % 250}");

        Assert.NotNull(throttle.TryBegin("shop", "admin", "192.0.2.10"));
    }

    [Fact]
    public void The_right_password_gives_the_reserved_attempt_back()
    {
        var (throttle, _) = New();
        for (var i = 0; i < LoginThrottle.MaxFailures - 1; i++) throttle.TryBegin("shop", "admin", "192.0.2.10");
        Assert.Null(throttle.TryBegin("shop", "admin", "192.0.2.10"));
        throttle.Succeeded("shop", "admin", "192.0.2.10");

        Assert.Null(throttle.RetryAfter("shop", "admin", "192.0.2.10"));
    }

    [Fact]
    public void Full_width_letters_are_the_same_account_as_they_are_to_the_database()
    {
        var (throttle, _) = New();
        // "ａｄｍｉｎ" in full-width letters: SQL Server's default collations ignore width, so it
        // reaches the same row, and here it is the same count.
        for (var i = 0; i < LoginThrottle.MaxFailures; i++)
            throttle.Failed("shop", i % 2 == 0 ? "ａｄｍｉｎ" : "admin");

        Assert.NotNull(throttle.RetryAfter("shop", "ADMIN"));
    }
}
