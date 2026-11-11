using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using BackgroundJobs;
using StackExchange.Redis;

namespace BackgroundJobs.HttpTests;

// Owns every process, port and directory it may stop or remove. Never uses REDIS_CONNECTION.
internal sealed class ProcessHost : IAsyncDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "background-jobs-process-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> children = [];
    private readonly StringBuilder logs = new();
    private readonly int redisPort = FreePort();
    private Process redis = null!;
    public Process Demo { get; private set; } = null!;
    public HttpClient Http { get; private set; } = null!;
    public ConnectionMultiplexer Connection { get; private set; } = null!;
    public RedisJobManager Manager => new(Connection);
    public string DataDirectory => Path.Combine(root, "data with spaces");
    public string Log { get { lock (logs) return logs.ToString(); } }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task Start(int workers = 1)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Process lifecycle tests require Linux signals and redis-server. Run the other HTTP tests with Category!=ProcessLifecycle.");
        Directory.CreateDirectory(DataDirectory);
        await StartRedis(DataDirectory);
        Connection = await ConnectionMultiplexer.ConnectAsync($"127.0.0.1:{redisPort},abortConnect=false,allowAdmin=true");
        await StartDemo(workers);
    }

    private Process Launch(string executable, IEnumerable<string> arguments, Dictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        if (environment is not null) foreach (var (key, value) in environment) info.Environment[key] = value;
        var process = new Process { StartInfo = info };
        var pid = 0;
        void Record(string? line) { if (line is not null) lock (logs) logs.AppendLine($"[{pid}] {line}"); }
        process.OutputDataReceived += (_, e) => Record(e.Data);
        process.ErrorDataReceived += (_, e) => Record(e.Data);
        try
        {
            if (!process.Start()) throw new InvalidOperationException("Could not start " + executable);
            pid = process.Id;
            children.Add(process); process.BeginOutputReadLine(); process.BeginErrorReadLine();
        }
        catch (Exception error)
        { process.Dispose(); throw new InvalidOperationException($"Cannot run {executable}. Configure REDIS_SERVER_EXECUTABLE for a dedicated Redis process. {error.Message}", error); }
        return process;
    }

    public async Task StartRedis(string directory)
    {
        redis = Launch(Environment.GetEnvironmentVariable("REDIS_SERVER_EXECUTABLE") ?? "redis-server",
            ["--bind", "127.0.0.1", "--port", redisPort.ToString(), "--dir", directory, "--appendonly", "yes", "--appendfsync", "always", "--save", "", "--maxmemory-policy", "noeviction"]);
        await Until(async () =>
        {
            if (redis.HasExited) throw new InvalidOperationException("Redis exited: " + Log);
            using var socket = new TcpClient();
            try { await socket.ConnectAsync(IPAddress.Loopback, redisPort); return true; }
            catch (SocketException) { return false; }
        });
    }

    public async Task StartDemo(int workers = 1)
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = output.Name;
        var dll = Path.Combine(output.Parent!.Parent!.FullName, "BackgroundJobs.Demo", configuration, "BackgroundJobs.Demo.dll");
        if (!File.Exists(dll)) throw new FileNotFoundException("Build the demo before process tests.", dll);
        var port = FreePort();
        Demo = Launch(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet", [dll], new()
        {
            ["PORT"] = port.ToString(), ["WORKER_COUNT"] = workers.ToString(),
            ["REDIS_CONNECTION"] = $"127.0.0.1:{redisPort}", ["ASPNETCORE_ENVIRONMENT"] = "Production"
        });
        Http?.Dispose(); Http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(3) };
        await Until(async () =>
        {
            if (Demo.HasExited) throw new InvalidOperationException("Demo exited: " + Log);
            try { using var response = await Http.GetAsync("/health"); return response.IsSuccessStatusCode; }
            catch (HttpRequestException) { return false; }
        });
    }

    public async Task<string> Create(string name, object args, object? options = null)
    {
        using var response = await Http.PostAsJsonAsync("/demo/jobs", new { job = name, @params = args, options = options ?? new { } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("jobId").GetString()!;
    }
    public async Task<JsonElement[]> Events()
    {
        var body = await Http.GetFromJsonAsync<JsonElement>("/demo/events");
        return body.GetProperty("items").EnumerateArray().Select(e => e.Clone()).ToArray();
    }
    public async Task<JobDetails> State(string id, JobState state, TimeSpan? timeout = null)
    {
        JobDetails? result = null;
        await Until(async () => (result = await Manager.GetAsync(id))?.State == state, timeout);
        return result!;
    }
    public static async Task Until(Func<Task<bool>> predicate, TimeSpan? duration = null)
    {
        using var timeout = new CancellationTokenSource(duration ?? TimeSpan.FromSeconds(20));
        while (!await predicate()) await Task.Delay(20, timeout.Token);
    }
    public async Task SignalDemo(string signal)
    {
        using var sender = Launch("/bin/kill", ["-" + signal, Demo.Id.ToString()]);
        await sender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, sender.ExitCode);
        // Launch tracks it; the disposal path tolerates an already disposed sender.
        children.Remove(sender);
    }
    public async Task KillDemo()
    {
        await SignalDemo("KILL"); await Demo.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0, Demo.ExitCode);
    }
    public async Task StopRedis(bool force)
    {
        if (force) redis.Kill();
        else
        {
            using var sender = Process.Start(new ProcessStartInfo("/bin/kill") { ArgumentList = { "-TERM", redis.Id.ToString() }, UseShellExecute = false })!;
            await sender.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0, sender.ExitCode);
        }
        await redis.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (!force) Assert.Equal(0, redis.ExitCode);
        else Assert.NotEqual(0, redis.ExitCode);
    }
    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        foreach (var child in children.AsEnumerable().Reverse())
        {
            try { if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); } }
            finally { child.Dispose(); }
        }
        if (Connection is not null) await Connection.DisposeAsync();
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
