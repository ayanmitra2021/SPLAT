using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Splat.Runtime;

/// <summary>An open file. Closed automatically when its owner leaves scope (or on <c>drop</c>).</summary>
public sealed class FileObj(string path, string mode, StreamReader? reader, StreamWriter? writer) : HeapObj
{
    public string Path { get; } = path;
    public string Mode { get; } = mode;
    public StreamReader? Reader { get; } = reader;
    public StreamWriter? Writer { get; } = writer;
    public override string TypeName => "File";
    public override string Display() => $"<File {Path}>";
    internal override void Close()
    {
        Reader?.Dispose();
        Writer?.Dispose();
    }
}

/// <summary>A lazy, one-shot stream of values, such as the lines of a file. Never materialised.</summary>
public sealed class IterObj(IEnumerable<Value> source) : HeapObj
{
    private IEnumerable<Value>? _source = source;
    public override string TypeName => "iter";
    public override string Display() => "<iter>";
    public IEnumerable<Value> Consume()
    {
        var s = _source ?? [];
        _source = null; // one-shot
        return s;
    }
}

/// <summary>A dynamically typed JSON value (<c>null</c> is a null node).</summary>
public sealed class JsonObj(JsonNode? node) : HeapObj
{
    public JsonNode? Node { get; } = node;
    public override string TypeName => "Json";
    public override string Display() => Node?.ToJsonString() ?? "null";
}

public sealed class SocketObj : HeapObj
{
    public TcpClient? Client { get; init; }
    public TcpListener? Listener { get; init; }
    public UdpClient? Udp { get; init; }
    public override string TypeName => "Socket";
    public override string Display() => Listener is not null ? "<Socket listening>" : Udp is not null ? "<Socket udp>" : "<Socket tcp>";

    public int Port => Listener is not null ? ((IPEndPoint)Listener.LocalEndpoint).Port
        : Udp is not null ? ((IPEndPoint)Udp.Client.LocalEndPoint!).Port
        : Client is not null ? ((IPEndPoint)Client.Client.LocalEndPoint!).Port : 0;

    internal override void Close()
    {
        Client?.Dispose();
        Listener?.Stop();
        Udp?.Dispose();
    }
}

/// <summary>A running HTTP server. It keeps the program alive (see <see cref="ServerRegistry"/>) until stopped or Ctrl+C.</summary>
public sealed class ServerObj : HeapObj
{
    public HttpListener Listener { get; }
    public int Port { get; }
    public Thread? Thread { get; set; }
    public volatile bool Stopped;

    internal ServerObj(HttpListener listener, int port)
    {
        Listener = listener;
        Port = port;
        ServerRegistry.Add(this);
    }

    public override string TypeName => "Server";
    public override string Display() => $"<Server on port {Port}>";

    internal override void Close()
    {
        Stopped = true;
        try { Listener.Stop(); Listener.Close(); } catch (ObjectDisposedException) { }
    }
}

/// <summary>Every server the program started, so the CLI can wait for them and Ctrl+C can stop them.</summary>
public static class ServerRegistry
{
    private static readonly List<ServerObj> Servers = [];
    private static readonly object Lock = new();

    internal static void Add(ServerObj s) { lock (Lock) Servers.Add(s); }

    public static bool AnyRunning { get { lock (Lock) return Servers.Any(s => !s.Stopped); } }

    public static IReadOnlyList<int> RunningPorts { get { lock (Lock) return Servers.Where(s => !s.Stopped).Select(s => s.Port).ToList(); } }

    public static void StopAll()
    {
        List<ServerObj> copy;
        lock (Lock) copy = [.. Servers];
        foreach (var s in copy) if (!s.Dead) { s.Dead = true; s.Close(); }
    }

    /// <summary>Blocks until every server has stopped (they stop on Ctrl+C, <c>stop()</c>, or scope exit).</summary>
    public static void WaitForAll()
    {
        while (AnyRunning) Thread.Sleep(50);
    }
}
