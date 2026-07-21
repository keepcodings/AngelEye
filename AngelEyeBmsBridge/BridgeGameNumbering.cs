using System.Globalization;

namespace AngelEyeBmsBridge;

/// <summary>
/// Creates BMS shoe numbers in yyyyMMddNNNN format and advances them across day boundaries.
/// </summary>
public static class BridgeGameNumbering
{
    private const long ShoeSequenceBase = 10000L;

    /// <summary>
    /// Returns the first shoe number for the current local day.
    /// </summary>
    /// <returns>The shoe number ending in 0001 for today.</returns>
    public static long TodayFirstShoe()
    {
        return FirstShoeForDate(DateTime.Now);
    }

    /// <summary>
    /// Returns the first shoe number for a specific date.
    /// </summary>
    /// <param name="date">The local date used for the yyyyMMdd prefix.</param>
    /// <returns>The shoe number ending in 0001 for the given date.</returns>
    public static long FirstShoeForDate(DateTime date)
    {
        return long.Parse($"{date:yyyyMMdd}0001", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Determines whether a BMS shoe number belongs to the specified local date.
    /// </summary>
    /// <param name="shoe">BMS shoe number in yyyyMMddNNNN format.</param>
    /// <param name="date">The local date to compare with the shoe prefix.</param>
    /// <returns><see langword="true"/> when the shoe uses the specified date prefix.</returns>
    public static bool IsShoeForDate(long shoe, DateTime date)
    {
        long datePrefix = long.Parse(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        return shoe > 0 && shoe / ShoeSequenceBase == datePrefix;
    }

    /// <summary>
    /// Advances the current shoe number, resetting to 0001 when the local date changes.
    /// </summary>
    /// <param name="currentShoe">Current BMS shoe number.</param>
    /// <param name="now">Optional local time used by tests or simulations.</param>
    /// <returns>The next BMS shoe number.</returns>
    public static long NextShoe(long currentShoe, DateTime? now = null)
    {
        DateTime date = now ?? DateTime.Now;
        long datePrefix = long.Parse(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
        long todayBase = datePrefix * ShoeSequenceBase;

        if (currentShoe / ShoeSequenceBase == datePrefix)
        {
            long currentSeq = currentShoe % ShoeSequenceBase;
            return todayBase + Math.Max(currentSeq + 1, 1);
        }

        return todayBase + 1;
    }
}
