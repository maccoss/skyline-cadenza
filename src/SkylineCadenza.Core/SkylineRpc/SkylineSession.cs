#nullable enable

using System.IO.Pipes;
using System.Text.Json;
using SkylineTool;
using static SkylineTool.JsonToolConstants;

namespace SkylineCadenza.Core.SkylineRpc;

/// <summary>
/// Connection factory for Skyline's JSON-RPC pipe. Skyline closes the pipe
/// after each request/response (the upstream <c>SkylineMcpServer.Invoke</c>
/// pattern is connect-per-call), so this class does NOT hold a pipe open -
/// it remembers how to connect and opens a fresh pipe inside each
/// <see cref="Execute"/> call.
/// </summary>
/// <remarks>
/// Discovery is a near-verbatim port of
/// <c>pwiz_tools/Skyline/Executables/Tools/SkylineMcp/SkylineMcpServer/SkylineConnection.cs</c>.
/// </remarks>
public sealed class SkylineSession
{
    public string PipeName { get; }
    public int? SkylineProcessId { get; }
    public TimeSpan ConnectTimeout { get; }

    private SkylineSession(string pipeName, int? processId, TimeSpan timeout)
    {
        PipeName = pipeName;
        SkylineProcessId = processId;
        ConnectTimeout = timeout;
    }

    /// <summary>
    /// Construct a session from <c>args[0]</c> (the
    /// <c>$(SkylineConnection)</c> pipe name Skyline passes external
    /// tools). Falls back to discovering any running instance via
    /// <c>~/.skyline-mcp/</c> when no argument is supplied.
    /// </summary>
    public static SkylineSession FromArguments(string[] args, TimeSpan? timeout = null)
    {
        var to = timeout ?? TimeSpan.FromSeconds(5);
        if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
        {
            // Skyline expands $(SkylineConnection) in tool-inf to the LEGACY
            // ToolService pipe name (used by the old binary SkylineToolClient).
            // The JSON-RPC server listens on a derived name; transform via
            // GetJsonPipeName so we actually connect to the JSON endpoint.
            // The legacy pipe responds in a binary length-prefixed format
            // which is why misrouted requests look like JSON starting with 0x00.
            string raw = args[0];
            string jsonName = raw.StartsWith(JsonToolConstants.JSON_PIPE_PREFIX, StringComparison.Ordinal)
                ? raw
                : JsonToolConstants.GetJsonPipeName(raw);
            return new SkylineSession(jsonName, null, to);
        }

        var info = DiscoverMostRecent()
            ?? throw new InvalidOperationException(
                "No Skyline pipe-name argument was passed and no running Skyline instance was found in ~/.skyline-mcp/.");
        return new SkylineSession(info.PipeName, info.ProcessId, to);
    }

    /// <summary>
    /// Open a fresh pipe, hand the client to <paramref name="action"/>, and
    /// dispose immediately. This is the canonical Skyline JSON-RPC usage
    /// pattern - each call is a stand-alone exchange.
    /// </summary>
    public T Execute<T>(Func<SkylineJsonToolClient, T> action)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
        pipe.ReadMode = PipeTransmissionMode.Message;
        var client = new SkylineJsonToolClient(pipe);
        return action(client);
    }

    /// <summary>
    /// Void overload of <see cref="Execute{T}"/>.
    /// </summary>
    public void Execute(Action<SkylineJsonToolClient> action)
    {
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect((int)ConnectTimeout.TotalMilliseconds);
        pipe.ReadMode = PipeTransmissionMode.Message;
        var client = new SkylineJsonToolClient(pipe);
        action(client);
    }

    /// <summary>
    /// Walk <c>~/.skyline-mcp/connection-*.json</c> and return all entries
    /// whose JSON parses cleanly.
    /// </summary>
    public static List<ConnectionInfo> DiscoverAll()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".skyline-mcp");
        if (!Directory.Exists(dir)) return new List<ConnectionInfo>();

        var result = new List<ConnectionInfo>();
        foreach (var file in Directory.EnumerateFiles(dir, "connection-*.json"))
        {
            try
            {
                using var fs = File.OpenRead(file);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                if (!root.TryGetProperty("pipe_name", out var pipeProp)) continue;
                string? pipeName = pipeProp.GetString();
                if (string.IsNullOrWhiteSpace(pipeName)) continue;
                int? pid = root.TryGetProperty("process_id", out var pidProp) ? pidProp.GetInt32() : null;
                DateTime? connectedAt = null;
                if (root.TryGetProperty("connected_at", out var atProp) &&
                    DateTime.TryParse(atProp.GetString(), out var parsed))
                {
                    connectedAt = parsed;
                }
                result.Add(new ConnectionInfo
                {
                    PipeName = pipeName!,
                    ProcessId = pid,
                    ConnectedAt = connectedAt,
                    SkylineVersion = root.TryGetProperty("skyline_version", out var v) ? v.GetString() : null,
                });
            }
            catch
            {
                // Ignore unreadable / stale connection files.
            }
        }
        return result;
    }

    public static ConnectionInfo? DiscoverMostRecent()
    {
        return DiscoverAll()
            .OrderByDescending(c => c.ConnectedAt ?? DateTime.MinValue)
            .FirstOrDefault();
    }

    public sealed class ConnectionInfo
    {
        public required string PipeName { get; init; }
        public int? ProcessId { get; init; }
        public DateTime? ConnectedAt { get; init; }
        public string? SkylineVersion { get; init; }
    }
}
