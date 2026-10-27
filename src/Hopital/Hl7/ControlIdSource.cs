namespace Hopital.Hl7;

/// <summary>
/// Makes MSH-10 control IDs: prefix + run start time + counter, e.g. HOP26102514300500001 (20 characters).
/// Including the run start keeps IDs unique across runs, so a receiver's duplicate check
/// only fires when a scenario sends the same ID on purpose.
/// </summary>
public sealed class ControlIdSource(string prefix, DateTime runStart)
{
    private int _counter;

    public ControlIdSource(TimeProvider clock) : this("HOP", clock.GetLocalNow().DateTime)
    {
    }

    public string Next() => $"{prefix}{runStart:yyMMddHHmmss}{Interlocked.Increment(ref _counter):D5}";
}
