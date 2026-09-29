using System.Globalization;

namespace Coglatas.Application.Notifications;

public enum TaskDeadlineDigestCategory
{
    DeadlineInThreeLocalDays = 0,
    DeadlineInOneLocalDay = 1,
    DueToday = 2,
    Overdue = 3
}

public readonly record struct TaskDeadlineDigestSchedule(
    DateOnly LocalDate,
    DateTimeOffset DueAtUtc);

/// <summary>
/// Pure scheduling and classification contract for the Workspace-local Task
/// deadline digest. Persistence, claiming, retries, and authorization remain
/// owned by the digest generation use case and ledger.
/// </summary>
public static class TaskDeadlineDigestPolicy
{
    public const int PolicyVersion = 1;
    public const int MaximumAutomaticAttempts = 3;
    public const string NotificationTitle = "Task deadline digest";
    public const string RelatedEntityType = "TaskDeadlineDigest";

    private static readonly long DigestIntervalTicks = TimeSpan.FromMinutes(15).Ticks;

    public static bool IsValidLocalTime(TimeOnly localTime) =>
        localTime.Ticks % DigestIntervalTicks == 0;

    public static DateOnly ResolveLocalDate(
        DateTimeOffset currentInstant,
        TimeZoneInfo workspaceTimeZone)
    {
        ArgumentNullException.ThrowIfNull(workspaceTimeZone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(currentInstant, workspaceTimeZone).DateTime);
    }

    public static TaskDeadlineDigestSchedule ResolveSchedule(
        DateTimeOffset currentInstant,
        TimeOnly localTime,
        TimeZoneInfo workspaceTimeZone)
    {
        var localDate = ResolveLocalDate(currentInstant, workspaceTimeZone);
        return new TaskDeadlineDigestSchedule(
            localDate,
            ResolveDueAtUtc(localDate, localTime, workspaceTimeZone));
    }

    /// <summary>
    /// Resolves the selected Workspace-local wall time to one UTC instant.
    /// A nonexistent wall time advances to the first valid instant after the
    /// DST gap. An ambiguous wall time selects its first chronological
    /// occurrence.
    /// </summary>
    public static DateTimeOffset ResolveDueAtUtc(
        DateOnly localDate,
        TimeOnly localTime,
        TimeZoneInfo workspaceTimeZone)
    {
        ArgumentNullException.ThrowIfNull(workspaceTimeZone);
        if (!IsValidLocalTime(localTime))
        {
            throw new ArgumentOutOfRangeException(
                nameof(localTime),
                localTime,
                "Digest local time must be an exact 15-minute value from 00:00 through 23:45.");
        }

        var localDateTime = localDate.ToDateTime(localTime, DateTimeKind.Unspecified);
        if (workspaceTimeZone.IsInvalidTime(localDateTime))
        {
            localDateTime = FirstValidLocalDateTimeAfterGap(localDateTime, workspaceTimeZone);
        }

        if (workspaceTimeZone.IsAmbiguousTime(localDateTime))
        {
            return workspaceTimeZone
                .GetAmbiguousTimeOffsets(localDateTime)
                .Select(offset => new DateTimeOffset(localDateTime, offset).ToUniversalTime())
                .Min();
        }

        var utc = TimeZoneInfo.ConvertTimeToUtc(localDateTime, workspaceTimeZone);
        return new DateTimeOffset(utc, TimeSpan.Zero);
    }

    public static TaskDeadlineDigestCategory? Classify(
        DateTimeOffset? deadlineAt,
        DateTimeOffset currentInstant,
        TimeZoneInfo workspaceTimeZone)
    {
        ArgumentNullException.ThrowIfNull(workspaceTimeZone);
        if (!deadlineAt.HasValue)
        {
            return null;
        }

        if (deadlineAt.Value < currentInstant)
        {
            return TaskDeadlineDigestCategory.Overdue;
        }

        var currentLocalDate = ResolveLocalDate(currentInstant, workspaceTimeZone);
        var deadlineLocalDate = ResolveLocalDate(deadlineAt.Value, workspaceTimeZone);
        var localDayDifference = deadlineLocalDate.DayNumber - currentLocalDate.DayNumber;
        return localDayDifference switch
        {
            0 => TaskDeadlineDigestCategory.DueToday,
            1 => TaskDeadlineDigestCategory.DeadlineInOneLocalDay,
            3 => TaskDeadlineDigestCategory.DeadlineInThreeLocalDays,
            _ => null
        };
    }

    public static string BuildNotificationLogicalKey(
        Guid workspaceId,
        DateOnly localDate,
        int policyVersion = PolicyVersion)
    {
        if (workspaceId == Guid.Empty)
        {
            throw new ArgumentException("A Workspace identity is required.", nameof(workspaceId));
        }

        if (policyVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policyVersion), policyVersion, "Policy version must be positive.");
        }

        return $"task-deadline-digest:workspace:{workspaceId:N}:date:{localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}:policy:{policyVersion}";
    }

    private static DateTime FirstValidLocalDateTimeAfterGap(
        DateTime invalidLocalDateTime,
        TimeZoneInfo workspaceTimeZone)
    {
        var firstInvalidTick = invalidLocalDateTime.Ticks;
        var upperBound = invalidLocalDateTime;

        // TimeZoneInfo adjustment rules cannot express an unbounded gap. The
        // loop deliberately locates the actual transition boundary instead of
        // assuming a one-hour DST shift; historical zones can skip other spans.
        while (workspaceTimeZone.IsInvalidTime(upperBound))
        {
            if (upperBound > DateTime.MaxValue.AddHours(-1))
            {
                throw new InvalidOperationException("The first valid local instant after the timezone gap could not be represented.");
            }

            upperBound = upperBound.AddHours(1);
        }

        var lastInvalidTick = firstInvalidTick;
        var firstValidTick = upperBound.Ticks;
        while (firstValidTick - lastInvalidTick > 1)
        {
            var middleTick = lastInvalidTick + ((firstValidTick - lastInvalidTick) / 2);
            var middle = new DateTime(middleTick, DateTimeKind.Unspecified);
            if (workspaceTimeZone.IsInvalidTime(middle))
            {
                lastInvalidTick = middleTick;
            }
            else
            {
                firstValidTick = middleTick;
            }
        }

        return new DateTime(firstValidTick, DateTimeKind.Unspecified);
    }
}
