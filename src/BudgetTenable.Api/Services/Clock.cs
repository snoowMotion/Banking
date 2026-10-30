namespace BudgetTenable.Api.Services;

public interface IClock
{
    /// <summary>Date du jour en Europe/Paris, jamais en UTC.</summary>
    DateOnly Today { get; }
}

public sealed class ParisClock(TimeProvider time) : IClock
{
    private static readonly TimeZoneInfo Zone = Find();

    private static TimeZoneInfo Find()
    {
        foreach (var id in new[] { "Europe/Paris", "Romance Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        throw new InvalidOperationException("Fuseau Europe/Paris introuvable : installe le paquet tzdata dans l'image.");
    }

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), Zone).DateTime);
}
