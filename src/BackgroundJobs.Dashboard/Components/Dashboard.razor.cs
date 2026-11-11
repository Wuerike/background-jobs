using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.JSInterop;
using Microsoft.Extensions.Logging;

namespace BackgroundJobs.Dashboard.Components;

public partial class Dashboard
{
    private static readonly JobState[] States = [JobState.Enqueued, JobState.Processing, JobState.Succeeded,
        JobState.Failed, JobState.Scheduled, JobState.Deleted];
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private readonly CancellationTokenSource lifetime = new();
    private JobObservation? observation;
    private JobMonitorPage? tablePage;
    private JobDetails? job;
    private string? selectedId, feedback, error, filterError, confirmationId;
    private string search = "", stateInput = "", sortInput = "Newest";
    private DateTime? fromDate, untilDate;
    private JobState? stateFilter;
    private JobMonitorQuery query = new();
    private bool loading, mutating, requeue, menuOpen, forbidden, disposed;
    private int refreshSeconds = 10;
    private DateTimeOffset? lastSuccess;
    private DateTimeOffset lastRequest, now = DateTimeOffset.UtcNow;
    private IJSObjectReference? module;
    private ElementReference confirmation;
    private Task? polling;
    private string Arguments => job is null ? "" : JsonSerializer.Serialize(job.Args, new JsonSerializerOptions { WriteIndented = true });

    protected override async Task OnInitializedAsync()
    {
        Navigation.LocationChanged += LocationChanged;
        ParseLocation();
        await Refresh();
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender) return;
        module = await JS.InvokeAsync<IJSObjectReference>("import", "./_content/BackgroundJobs.Dashboard/dashboard.js");
        polling = Poll();
        await InvokeAsync(StateHasChanged);
    }

    private async Task Poll()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(lifetime.Token))
                await InvokeAsync(async () =>
                {
                    if (!Connection.IsConnected) return;
                    now = DateTimeOffset.UtcNow;
                    if (refreshSeconds > 0 && now - lastRequest >= TimeSpan.FromSeconds(refreshSeconds)) await Refresh();
                    StateHasChanged();
                });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async void LocationChanged(object? sender, LocationChangedEventArgs args)
    {
        try
        {
            await InvokeAsync(async () =>
            {
                var previous = selectedId;
                ParseLocation();
                menuOpen = false;
                if (previous != selectedId) { job = null; feedback = null; await CloseDialog(); }
                await Refresh();
                StateHasChanged();
            });
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception) { Logger.LogError(exception, "Dashboard navigation failed."); }
    }

    private void ParseLocation()
    {
        var values = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query);
        string Get(string name) => values.TryGetValue(name, out var value) ? value.ToString() : "";
        selectedId = string.IsNullOrEmpty(Get("job")) ? null : Get("job");
        search = Get("q"); stateInput = Get("state"); sortInput = Get("sort");
        if (sortInput.Length == 0) sortInput = "Newest";
        stateFilter = null;
        filterError = null;
        if (stateInput.Length > 0)
        {
            if (Enum.TryParse<JobState>(stateInput, true, out var state) && Enum.IsDefined(state)) stateFilter = state;
            else filterError = "Estado de filtro inválido.";
        }
        DateTime? ParseDate(string key)
        {
            if (Get(key).Length == 0) return null;
            if (DateTime.TryParseExact(Get(key), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
            filterError = "Data de filtro inválida."; return null;
        }
        fromDate = ParseDate("from"); untilDate = ParseDate("until");
        if (!Enum.TryParse<JobMonitorOrder>(sortInput, out var order) || !Enum.IsDefined(order)) filterError = "Ordenação inválida.";
        int Number(string key, int fallback)
        {
            if (Get(key).Length == 0) return fallback;
            if (int.TryParse(Get(key), out var number)) return number;
            filterError = "Paginação inválida."; return fallback;
        }
        query = new(stateFilter, search, Utc(fromDate), Utc(untilDate), order, Number("page", 1), Number("size", 25));
        BuildPage();
    }

    private static DateTimeOffset? Utc(DateTime? value) => value is { } date ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)) : null;

    private void BuildPage()
    {
        tablePage = null;
        if (filterError is not null || observation is null) return;
        try { tablePage = observation.Query(query); }
        catch (ArgumentException) { filterError = "Confira os filtros: intervalo crescente, busca de até 200 caracteres, página positiva e tamanho de 1 a 100."; }
    }

    private async Task<bool> HasAccess()
    {
        if (Options.AuthorizationPolicy is null) return true;
        var state = await Authentication.GetAuthenticationStateAsync();
        return (await Authorization.AuthorizeAsync(state.User, null, Options.AuthorizationPolicy)).Succeeded;
    }

    private async Task Refresh()
    {
        if (loading || disposed) return;
        loading = true;
        lastRequest = DateTimeOffset.UtcNow;
        var requestedId = selectedId;
        try
        {
            if (!await HasAccess())
            {
                forbidden = true; observation = null; tablePage = null; job = null; return;
            }
            forbidden = false;
            var snapshot = await Cache.ReadAsync(lifetime.Token);
            var detail = requestedId is null ? null : await Manager.GetAsync(requestedId);
            if (disposed) return;
            observation = snapshot;
            if (requestedId == selectedId) job = detail;
            lastSuccess = DateTimeOffset.UtcNow;
            now = lastSuccess.Value;
            error = null;
            BuildPage();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Dashboard storage read failed.");
            error = exception is InvalidOperationException && exception.Message.StartsWith("Monitoring limit", StringComparison.Ordinal)
                ? $"O limite de {Options.MaxJobs} jobs foi excedido. Ajuste MaxJobs no host ou use um armazenamento menor."
                : "Verifique a conexão e a integridade do armazenamento no host.";
        }
        finally { loading = false; }
        if (requestedId != selectedId && !disposed) await Refresh();
    }

    private string ListUrl() => new Uri(Navigation.Uri).GetLeftPart(UriPartial.Path);
    private string StateUrl(JobState state) => QueryHelpers.AddQueryString(ListUrl(), "state", Presentation.Key(state));
    private string JobUrl(string id) => ChangeQuery("job", id);
    private string WithoutJob() => ChangeQuery("job", null);
    private string ChangeQuery(string key, string? value)
    {
        var values = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query).ToDictionary(item => item.Key, item => (string?)item.Value.ToString());
        values[key] = value;
        return QueryHelpers.AddQueryString(ListUrl(), values);
    }
    private void GoPage(int number) => Navigation.NavigateTo(ChangeQuery("page", number.ToString(CultureInfo.InvariantCulture)));
    private void ChangePageSize(ChangeEventArgs args)
    {
        var values = QueryHelpers.ParseQuery(new Uri(Navigation.Uri).Query)
            .ToDictionary(item => item.Key, item => (string?)item.Value.ToString());
        values["page"] = "1";
        values["size"] = args.Value?.ToString();
        Navigation.NavigateTo(QueryHelpers.AddQueryString(ListUrl(), values));
    }
    private async Task ManualRefresh()
    {
        Cache.Invalidate();
        await Refresh();
    }
    private void ApplyFilters()
    {
        var values = new Dictionary<string, string?> { ["q"] = search, ["state"] = stateInput, ["sort"] = sortInput,
            ["from"] = fromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["until"] = untilDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ["size"] = query.PageSize.ToString(CultureInfo.InvariantCulture) };
        Navigation.NavigateTo(QueryHelpers.AddQueryString(ListUrl(), values));
    }

    private static string StateDescription(JobState state) => state switch
    {
        JobState.Enqueued => "Prontos para executar", JobState.Processing => "Tentativas em andamento",
        JobState.Succeeded => "Finalizados com sucesso", JobState.Failed => "Tentativas esgotadas",
        JobState.Scheduled => "Agendamentos e retries", _ => "Pendências removidas"
    };
    private string ItemTime(JobMonitorItem item) => item.State switch
    {
        JobState.Enqueued => Presentation.Duration(now - item.UpdatedAt),
        JobState.Processing => Presentation.Duration(now - item.LastAttempt!.StartedAt),
        JobState.Scheduled => item.ScheduledAt <= now ? "Elegível" : Presentation.Duration(item.ScheduledAt!.Value - now),
        _ => item.LastAttempt?.FinishedAt is { } end ? Presentation.Duration(end - item.LastAttempt.StartedAt) : "—"
    };
    private static string TimeLabel(JobMonitorItem item) => item.State switch
    {
        JobState.Enqueued => "Aguardando na fila", JobState.Processing => "Execução em andamento",
        JobState.Scheduled => "Até elegibilidade", _ => item.LastAttempt?.Outcome == JobAttemptOutcome.Interrupted
            ? "Intervalo até recuperação" : "Última execução concluída"
    };
    private async Task Copy(string value)
    {
        if (module is null) return;
        try { await module.InvokeVoidAsync("copy", value); feedback = "Copiado para a área de transferência."; }
        catch (JSException) { feedback = "A cópia não está disponível neste navegador. Selecione e copie o texto."; }
    }
    private async Task OpenConfirmation(bool isRequeue)
    {
        if (mutating || module is null || job is null) return;
        requeue = isRequeue;
        confirmationId = job.Id;
        await module.InvokeVoidAsync("showDialog", confirmation);
    }
    private async Task CancelConfirmation()
    {
        if (!mutating) await CloseDialog();
    }
    private async Task CloseDialog()
    {
        confirmationId = null;
        if (module is not null) await module.InvokeVoidAsync("closeDialog", confirmation);
    }
    private async Task Confirm()
    {
        if (mutating || confirmationId is null) return;
        mutating = true;
        var id = confirmationId;
        try
        {
            if (!await HasAccess()) { feedback = "A política do host não permite esta operação."; return; }
            var result = requeue ? await Manager.RequeueAsync(id) : await Manager.DeleteAsync(id);
            feedback = result switch
            {
                JobOperationResult.Applied => requeue ? "Reenfileiramento confirmado. O contador foi reiniciado; o executor pode já ter avançado o job." : "Deleção confirmada. A pendência foi removida.",
                JobOperationResult.NotFound => "Job não encontrado. A operação não foi aplicada.",
                _ => "Conflito: o estado mudou ou não permite a ação. Os dados serão atualizados; confira o estado atual."
            };
        }
        catch (Exception exception)
        {
            Logger.LogError(exception, "Dashboard management result not confirmed.");
            feedback = "Resultado não confirmado. A resposta da operação não foi recebida; ela pode ter sido aplicada. Consulte o histórico antes de decidir enviar outra ação. Não houve reenvio automático pelo painel.";
        }
        finally
        {
            Cache.Invalidate();
            mutating = false;
            await CloseDialog();
            await Refresh();
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        Navigation.LocationChanged -= LocationChanged;
        await lifetime.CancelAsync();
        if (polling is not null) await polling;
        if (module is not null)
            try { await module.DisposeAsync(); } catch (JSDisconnectedException) { }
        lifetime.Dispose();
    }
}
