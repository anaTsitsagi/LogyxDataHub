namespace DataHub.Oris;

/// <summary>
/// Clarion stores dates as a LONG counting days since 28 December 1800 (day 4 = 1801-01-01).
/// A value of 0 means "no date".
/// </summary>
public static class ClarionDate
{
    private static readonly DateOnly Epoch = new(1800, 12, 28);

    public static DateOnly? ToDateOnly(long days) =>
        days <= 0 || days > int.MaxValue ? null : Epoch.AddDays((int)days);

    public static long FromDateOnly(DateOnly date) => date.DayNumber - Epoch.DayNumber;
}
