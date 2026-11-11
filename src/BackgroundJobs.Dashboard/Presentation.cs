using System.Globalization;

namespace BackgroundJobs.Dashboard;

public static class Presentation
{
    public static string State(JobState state) => state switch
    {
        JobState.Scheduled => "Agendados", JobState.Enqueued => "Na fila", JobState.Processing => "Em execução",
        JobState.Succeeded => "Concluídos", JobState.Failed => "Falharam", _ => "Deletados"
    };
    public static string Key(JobState state) => state.ToString().ToLowerInvariant();
    public static string Date(DateTimeOffset? at) => at?.ToUniversalTime().ToString("dd/MM/yyyy HH:mm:ss.fff 'UTC'", CultureInfo.InvariantCulture) ?? "—";
    public static string Duration(TimeSpan value)
    {
        value = value < TimeSpan.Zero ? TimeSpan.Zero : value;
        return value.TotalDays >= 1 ? $"{value.TotalDays:F1} d" : value.TotalHours >= 1 ? $"{value.TotalHours:F1} h" :
            value.TotalMinutes >= 1 ? $"{value.TotalMinutes:F1} min" : value.TotalSeconds >= 1 ? $"{value.TotalSeconds:F1} s" : $"{value.TotalMilliseconds:F0} ms";
    }
    public static string Outcome(JobAttemptOutcome? outcome) => outcome switch
    {
        JobAttemptOutcome.Succeeded => "Sucesso", JobAttemptOutcome.Failed => "Falha",
        JobAttemptOutcome.Interrupted => "Interrompida", _ => "Em execução"
    };
    public static bool CanRequeue(JobState state) => state is JobState.Succeeded or JobState.Failed or JobState.Deleted;
    public static bool CanDelete(JobState state) => state is JobState.Scheduled or JobState.Enqueued;

    public static DateTimeOffset CurrentCycleStartedAt(JobDetails job)
    {
        var start = job.CreatedAt;
        JobState? previous = null;
        foreach (var entry in job.History)
        {
            if (entry.State == JobState.Enqueued && previous is JobState.Succeeded or JobState.Failed or JobState.Deleted)
                start = entry.At;
            previous = entry.State;
        }
        return start;
    }

    /// <summary>Match attempts to chronological processing entries, not timestamps or reset attempt numbers.</summary>
    public static IReadOnlyList<int> AttemptCycles(JobDetails job)
    {
        var cycles = new List<int>();
        var cycle = 1;
        JobState? previous = null;
        foreach (var entry in job.History)
        {
            if (entry.State == JobState.Enqueued && previous is JobState.Succeeded or JobState.Failed or JobState.Deleted) cycle++;
            if (entry.State == JobState.Processing) cycles.Add(cycle);
            previous = entry.State;
        }
        return cycles;
    }
}
