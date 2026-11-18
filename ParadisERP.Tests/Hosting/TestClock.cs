namespace ParadisERP.Tests.Hosting;

/// <summary>
/// A clock a test sets by hand: the instant a customer or an employee is created at, which decides
/// the YYMM in their code. Given to a host through <see cref="PosHost"/>'s <c>services</c>, or to a
/// harness directly.
/// </summary>
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;
}
