using BenchmarkDotNet.Attributes;
using Piper.Core.Http;
using Piper.Core.Sessions;

namespace Piper.Benchmarks;

internal static class SessionFactory
{
    public const int Count = 100_000;

    /// <summary>Completed sessions across 50 hosts with a mix of statuses and small JSON bodies.</summary>
    public static Session[] Make(int count)
    {
        var sessions = new Session[count];
        for (var i = 0; i < count; i++)
        {
            var request = new HttpRequestData { Method = i % 5 == 0 ? "POST" : "GET", RequestTarget = $"/api/orders/{i}?page={i % 20}" };
            request.Headers.Add("Host", $"api{i % 50}.example.com");
            request.Headers.Add("Accept", "application/json");
            var response = new HttpResponseData { StatusCode = i % 17 == 0 ? 500 : i % 11 == 0 ? 404 : 200, Body = System.Text.Encoding.UTF8.GetBytes($"{{\"id\":{i},\"status\":\"ok\"}}") };
            response.Headers.Add("Content-Type", "application/json");
            sessions[i] = new Session { Request = request, Response = response, State = SessionState.Complete, Completed = DateTimeOffset.Now };
        }
        return sessions;
    }
}

/// <summary>Running a parsed query over 100,000 captured sessions, as the filter box does.</summary>
[MemoryDiagnoser]
public class SearchQueryBenchmarks
{
    private Session[] _sessions = [];
    private SearchQuery _query = SearchQuery.Empty;

    // Hits among the 100,000 sessions (counted, not guessed): 1,111 (1.1%), 5,883 (5.9%), 2,000 (2%),
    // 91,444 (91%, a scan of every body). "orders" matches all and "domain:api7 method:POST" matches none.
    [Params("orders/77", "status:5xx", "domain:api5.example.com method:POST", "body:\"status\":\"ok\" -status:404")]
    public string Query { get; set; } = "orders/77";

    private Session[] _cold = [];

    [GlobalSetup]
    public void Setup()
    {
        _sessions = SessionFactory.Make(SessionFactory.Count);
        _query = SearchQuery.Parse(Query);
        foreach (var session in _sessions) _query.Matches(session); // fills the per-session text caches, as a first search does
    }

    /// <summary>Typing in the filter box again: every session's text is already cached.</summary>
    [Benchmark]
    public int MatchAll() => Count(_sessions);

    /// <summary>The first search after the sessions were captured: nothing is cached yet, so this includes
    /// building each session's search text. New sessions for every iteration (outside the measurement).</summary>
    [IterationSetup(Target = nameof(FirstSearch))]
    public void ColdSessions() => _cold = SessionFactory.Make(SessionFactory.Count);

    [Benchmark]
    public int FirstSearch() => Count(_cold);

    private int Count(Session[] sessions)
    {
        var hits = 0;
        foreach (var session in sessions)
            if (_query.Matches(session)) hits++;
        return hits;
    }
}

/// <summary>Adding 100,000 sessions to a store, and copying them out for the grid to read.</summary>
[MemoryDiagnoser]
public class SessionStoreBenchmarks
{
    private Session[] _sessions = [];
    private SessionStore _full = new();
    private readonly List<Session> _buffer = [];

    [GlobalSetup]
    public void Setup()
    {
        _sessions = SessionFactory.Make(SessionFactory.Count);
        _full = new SessionStore { Capacity = SessionFactory.Count };
        foreach (var session in _sessions) _full.Add(session);
    }

    [Benchmark]
    public int AddAll()
    {
        var store = new SessionStore { Capacity = SessionFactory.Count };
        foreach (var session in _sessions) store.Add(session);
        return store.Count;
    }

    [Benchmark]
    public int CopyTo()
    {
        _full.CopyTo(_buffer);
        return _buffer.Count;
    }
}
