using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Splat.Core;
using Splat.Semantics;
using Splat.Syntax;

namespace Splat.Runtime;

// Phases 10-11: files, JSON, CSV, dates and times, HTTP and sockets.
public sealed partial class Interpreter
{
    private readonly List<ServerObj> _servers = [];

    /// <summary>Stops the servers this interpreter started (a session ending must not touch other programs' servers).</summary>
    public void StopServers()
    {
        List<ServerObj> copy;
        lock (_servers) copy = [.. _servers];
        foreach (var s in copy) if (!s.Dead) { s.Dead = true; s.Close(); }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Runs a blocking operation with the interpreter lock released, so a local server can answer while we wait.</summary>
    private T Blocking<T>(Func<T> work)
    {
        bool held = Monitor.IsEntered(_gil);
        if (held) Monitor.Exit(_gil);
        try { return work(); }
        finally { if (held) Monitor.Enter(_gil); }
    }

    private static long NowStamp() => (DateTime.UtcNow.Ticks - DateTime.UnixEpoch.Ticks) * 100;

    // ---- prelude functions ----

    private bool TryNativeBuiltin(string name, Value[] a, SourceSpan span, out Value result)
    {
        result = Value.Unit;
        switch (name)
        {
            case "read":
            {
                var path = a[0].AsString;
                result = Value.Str(Io(span, path, () => File.ReadAllText(path)));
                return true;
            }
            case "write":
            {
                var path = a[0].AsString;
                Io(span, path, () => { File.WriteAllText(path, a[1].AsString); return 0; });
                return true;
            }
            case "append":
            {
                var path = a[0].AsString;
                Io(span, path, () => { File.AppendAllText(path, a[1].AsString); return 0; });
                return true;
            }
            case "exists": result = Value.Bool(File.Exists(a[0].AsString) || Directory.Exists(a[0].AsString)); return true;
            case "delete":
            {
                var path = a[0].AsString;
                if (!File.Exists(path)) throw Fail(Templates.FileNotFound(span, path));
                Io(span, path, () => { File.Delete(path); return 0; });
                return true;
            }
            case "open": result = Value.Obj(OpenFile(a[0].AsString, a[1].AsString, span)); return true;
            case "load_json":
                try { result = Value.Obj(new JsonObj(JsonNode.Parse(a[0].AsString))); }
                catch (JsonException e) { throw Fail(Templates.BadJson(span, $"That is not valid JSON: {e.Message}")); }
                return true;
            case "to_json": result = Value.Str(ToJsonNode(a[0])?.ToJsonString() ?? "null"); return true;

            case "now": result = Value.Temporal(VKind.Stamp, NowStamp()); return true;
            case "today": result = Value.Temporal(VKind.Date, Temporal.FromDateOnly(DateOnly.FromDateTime(DateTime.Now))); return true;
            case "clock": result = Value.Temporal(VKind.Time, DateTime.Now.TimeOfDay.Ticks * 100); return true;
            case "date":
                result = Temporal.TryParseDateLocale(a[0].AsString, Temporal.Locale.Default, out long d) ? Got(Value.Temporal(VKind.Date, d)) : Nada();
                return true;
            case "time":
                result = Temporal.TryParseTime(a[0].AsString.Trim(), out long t, out _) ? Got(Value.Temporal(VKind.Time, t)) : Nada();
                return true;
            case "stamp":
                result = Temporal.TryParseStampLocale(a[0].AsString, Temporal.Locale.Default, out long s) ? Got(Value.Temporal(VKind.Stamp, s)) : Nada();
                return true;
            case "wrap_time":
                result = Value.Temporal(VKind.Time, Temporal.FloorMod(a[0].Bits + a[1].Bits % Temporal.NsPerDay, Temporal.NsPerDay));
                return true;
            case "sleep":
            {
                int ms = (int)Math.Clamp(a[0].Bits / Temporal.NsPerMs, 0, int.MaxValue);
                Blocking(() => { Thread.Sleep(ms); return 0; });
                return true;
            }

            case "fetch": result = Value.Str(HttpText(HttpMethod.Get, a[0].AsString, null, span)); return true;
            case "post": result = Value.Str(HttpText(HttpMethod.Post, a[0].AsString, a[1].AsString, span)); return true;
            case "download":
            {
                var url = a[0].AsString;
                var bytes = HttpBytes(url, span);
                Io(span, a[1].AsString, () => { File.WriteAllBytes(a[1].AsString, bytes); return 0; });
                return true;
            }
            case "serve": result = Serve((int)a[0].Bits, (Closure)a[1].Ref!, span); return true;
        }
        return false;
    }

    private T Io<T>(SourceSpan span, string path, Func<T> work)
    {
        try { return work(); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { throw Fail(Templates.FileNotFound(span, path)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw Fail(Templates.IoError(span, $"'{path}'", e.Message)); }
    }

    private FileObj OpenFile(string path, string mode, SourceSpan span)
    {
        switch (mode)
        {
            case "r":
                if (!File.Exists(path)) throw Fail(Templates.FileNotFound(span, path));
                return Io(span, path, () => new FileObj(path, mode, new StreamReader(path, Encoding.UTF8), null));
            case "w":
            case "a":
                return Io(span, path, () => new FileObj(path, mode, null, new StreamWriter(path, mode == "a", new UTF8Encoding(false))));
            default:
                throw Fail(Templates.IoError(span, $"open('{path}', '{mode}')", "the mode must be \"r\" (read), \"w\" (write) or \"a\" (append)"));
        }
    }

    // ---- File, Json, Socket and Server methods ----

    private Value CallNative(HeapObj o, string name, Value[] a, SourceSpan span)
    {
        switch (o)
        {
            case FileObj f: return FileMethod(f, name, a, span);
            case JsonObj j: return JsonMethod(j, name, a, span);
            case SocketObj s: return SocketMethod(s, name, a, span);
            case ServerObj sv:
                switch (name)
                {
                    case "stop": Destroy(sv, span); return Value.Unit;
                    case "port": return Value.Int(VKind.I32, sv.Port);
                    case "wait": Blocking(() => { while (!sv.Stopped) Thread.Sleep(25); return 0; }); return Value.Unit;
                }
                break;
        }
        throw Fail(Templates.Internal(span, $"method '{name}' on {o.TypeName}"));
    }

    private Value FileMethod(FileObj f, string name, Value[] a, SourceSpan span)
    {
        void Open(bool read)
        {
            if (f.Dead) throw Fail(Templates.IoError(span, $"'{f.Path}'", "the file is already closed"));
            if (read && f.Reader is null) throw Fail(Templates.IoError(span, $"'{f.Path}'", "the file was opened for writing; open it with mode \"r\" to read"));
            if (!read && f.Writer is null) throw Fail(Templates.IoError(span, $"'{f.Path}'", "the file was opened for reading; open it with mode \"w\" or \"a\" to write"));
        }
        switch (name)
        {
            case "lines": Open(true); return Value.Obj(new IterObj(LinesOf(f)));
            case "read_all": Open(true); return Value.Str(Io(span, f.Path, () => f.Reader!.ReadToEnd()));
            case "write": Open(false); Io(span, f.Path, () => { f.Writer!.Write(a[0].AsString); return 0; }); return Value.Unit;
            case "write_line": Open(false); Io(span, f.Path, () => { f.Writer!.Write(a[0].AsString + "\n"); return 0; }); return Value.Unit;
            case "close": Destroy(f, span); return Value.Unit;
        }
        throw Fail(Templates.Internal(span, $"File.{name}"));
    }

    /// <summary>Reads one line at a time. A file nobody owns (<c>open(p).lines()</c>) closes when the loop ends or breaks.</summary>
    private IEnumerable<Value> LinesOf(FileObj f)
    {
        try
        {
            string? line;
            while ((line = f.Reader!.ReadLine()) is not null) yield return Value.Str(line);
        }
        finally
        {
            if (f.Owner is null) { f.Close(); f.Dead = true; }
        }
    }

    // ---- JSON ----

    private Value JsonMethod(JsonObj j, string name, Value[] a, SourceSpan span)
    {
        var n = j.Node;
        Value Wrap(JsonNode? x) => Value.Obj(new JsonObj(x));
        switch (name)
        {
            case "kind":
                return Value.Str(n switch
                {
                    null => "null",
                    JsonArray => "list",
                    JsonObject => "object",
                    JsonValue v when v.TryGetValue<bool>(out _) => "bool",
                    JsonValue v when v.TryGetValue<string>(out _) => "text",
                    _ => "number",
                });
            case "len":
                return Value.Int(VKind.Usize, n switch { JsonArray ar => ar.Count, JsonObject ob => ob.Count, JsonValue v when v.TryGetValue<string>(out var s) => s.Length, _ => 0 });
            case "keys":
            {
                var l = new SplatList();
                if (n is JsonObject ob) foreach (var kv in ob) l.Items.Add(Value.Str(kv.Key));
                return Value.List(l);
            }
            case "items":
            {
                var l = new SplatList();
                if (n is JsonArray ar) foreach (var x in ar) l.Items.Add(Wrap(x));
                else if (n is JsonObject ob) foreach (var kv in ob) l.Items.Add(Wrap(kv.Value));
                return Value.List(l);
            }
            case "get":
                return n is JsonObject o2 && o2.TryGetPropertyValue(a[0].AsString, out var child) ? Got(Wrap(child)) : Nada();
            case "at":
                return n is JsonArray ar2 && a[0].AsInt128 >= 0 && a[0].AsInt128 < ar2.Count ? Got(Wrap(ar2[(int)a[0].AsInt128])) : Nada();
            case "as_txt":
                return n is JsonValue sv && sv.TryGetValue<string>(out var str) ? Got(Value.Str(str)) : Nada();
            case "as_f64":
                return n is JsonValue fv && !fv.TryGetValue<string>(out _) && !fv.TryGetValue<bool>(out _) && fv.TryGetValue<double>(out var dbl)
                    ? Got(Value.Float(VKind.F64, dbl)) : Nada();
            case "as_i64":
                return n is JsonValue iv && !iv.TryGetValue<string>(out _) && iv.TryGetValue<long>(out var lng) ? Got(Value.Int(VKind.I64, lng)) : Nada();
            case "as_bool":
                return n is JsonValue bv && bv.TryGetValue<bool>(out var bl) ? Got(Value.Bool(bl)) : Nada();
        }
        throw Fail(Templates.Internal(span, $"Json.{name}"));
    }

    private Value IndexJson(JsonObj j, Value idx, SourceSpan span)
    {
        if (idx.Kind == VKind.Txt)
        {
            if (j.Node is JsonObject o && o.TryGetPropertyValue(idx.AsString, out var c)) return Value.Obj(new JsonObj(c));
            throw Fail(Templates.KeyNotFound(span, KeyText(idx)));
        }
        var i = idx.AsInt128;
        if (j.Node is JsonArray a && i >= 0 && i < a.Count) return Value.Obj(new JsonObj(a[(int)i]));
        throw Fail(Templates.IndexOutOfRange(span, (long)Int128.Clamp(i, long.MinValue, long.MaxValue), (j.Node as JsonArray)?.Count ?? 0));
    }

    private JsonNode? ToJsonNode(Value v)
    {
        switch (v.Kind)
        {
            case VKind.Bool: return JsonValue.Create(v.AsBool);
            case VKind.Txt: return JsonValue.Create(v.AsString);
            case VKind.Char: return JsonValue.Create(Formatter.Display(v));
            case VKind.Unit: return null;
            case VKind.F16 or VKind.F32 or VKind.F64:
                return double.IsFinite(v.AsDouble) ? JsonValue.Create(v.AsDouble) : null;
            case VKind.List: return new JsonArray(v.AsList.Items.Select(ToJsonNode).ToArray());
            case VKind.Set: return new JsonArray(v.AsSet.Items.Select(ToJsonNode).ToArray());
            case VKind.Tuple: return new JsonArray(v.AsTuple.Select(ToJsonNode).ToArray());
            case VKind.Map:
            {
                var o = new JsonObject();
                foreach (var (k, x) in v.AsMap.Items) o[Formatter.Display(k)] = ToJsonNode(x);
                return o;
            }
            case VKind.Enum:
            {
                var e = v.AsEnum;
                if (e.EnumName == "Maybe") return e.VariantName == "Got" ? ToJsonNode(e.Payloads[0]) : null;
                if (e.PayloadCount == 0) return JsonValue.Create(e.VariantName);
                return new JsonObject { [e.VariantName] = new JsonArray(e.Payloads.Select(ToJsonNode).ToArray()) };
            }
            case VKind.Date: return JsonValue.Create(Temporal.IsoDate(v.Bits));
            case VKind.Time: return JsonValue.Create(Temporal.IsoTime(v.Bits));
            case VKind.Stamp: return JsonValue.Create(Temporal.IsoStamp(v.Bits));
            case VKind.Dur: return JsonValue.Create(Temporal.DurText(v.Bits));
            case VKind.Cal: return JsonValue.Create(Temporal.CalText(v.Bits));
            case VKind.Object:
                if (v.AsObj is JsonObj jo) return jo.Node?.DeepClone();
                if (v.AsObj is ClassInst ci)
                {
                    var o = new JsonObject();
                    foreach (var f in ci.Class.AllFields())
                        if (!f.IsSecret && ci.Fields.TryGetValue(f.Name, out var fv)) o[f.Name] = ToJsonNode(fv);
                    return o;
                }
                return JsonValue.Create(v.AsObj.Display());
            default:
                if (v.IsInt) return v.IsUnsigned ? JsonValue.Create((ulong)v.Bits) : JsonValue.Create(v.Bits);
                return JsonValue.Create(Formatter.Display(v));
        }
    }

    // ---- CSV (rows of f32; the tensor-returning version arrives with tensors) ----

    // ---- dates and times ----

    private Value TemporalMethod(Value recv, string name, SourceSpan span)
    {
        long days = recv.Kind == VKind.Date ? recv.Bits : recv.Kind == VKind.Stamp ? Temporal.FloorDiv(recv.Bits, Temporal.NsPerDay) : 0;
        long tod = recv.Kind == VKind.Time ? recv.Bits : recv.Kind == VKind.Stamp ? Temporal.FloorMod(recv.Bits, Temporal.NsPerDay) : 0;
        Value I(long n) => Value.Int(VKind.I32, n);
        switch (name)
        {
            case "year": return I(Temporal.DateParts(days).Y);
            case "month": return I(Temporal.DateParts(days).M);
            case "day": return I(Temporal.DateParts(days).D);
            case "weekday": return I(Temporal.Weekday(days));
            case "hour": return I(Temporal.TimeParts(tod).H);
            case "minute": return I(Temporal.TimeParts(tod).Mi);
            case "second": return I(Temporal.TimeParts(tod).S);
            case "iso": return Value.Str(Formatter.FormatTemporal(recv, "iso"));
            case "date": return Value.Temporal(VKind.Date, days);
            case "time": return Value.Temporal(VKind.Time, tod);
            case "ns": return Value.Int(VKind.I64, recv.Bits);
            case "ms": return Value.Int(VKind.I64, recv.Bits / Temporal.NsPerMs);
            case "secs": return Value.Float(VKind.F64, recv.Bits / (double)Temporal.NsPerSec);
            case "months": return Value.Int(VKind.I64, recv.Bits);
        }
        throw Fail(Templates.Internal(span, $"{recv.TypeName}.{name}"));
    }

    private Value TemporalBinary(TokenKind op, Value a, Value b, SourceSpan span)
    {
        // Comparisons.
        if (op is TokenKind.EqEq or TokenKind.BangEq or TokenKind.Lt or TokenKind.Le or TokenKind.Gt or TokenKind.Ge)
            return Ordered(op, a.Bits.CompareTo(b.Bits), span);

        long Chk(Func<long> f, string what)
        {
            try { return checked(f()); }
            catch (OverflowException) { throw Fail(Templates.TimeOverflow(span, $"{what} is outside the range a {a.TypeName} can hold.")); }
        }
        Value DateShift(long days, long delta)
        {
            if (Temporal.TryDateAddDays(days, delta, out var r)) return Value.Temporal(VKind.Date, r);
            throw Fail(Templates.TimeOverflow(span, "That date falls outside years 0001 to 9999."));
        }
        long WholeDays(long ns)
        {
            if (ns % Temporal.NsPerDay != 0)
                throw Fail(Templates.TimeOverflow(span, $"A Date moves in whole days, and {Temporal.DurText(ns)} is not a whole number of days."));
            return ns / Temporal.NsPerDay;
        }
        Value AddMonths(Value point, long months)
        {
            if (point.Kind == VKind.Date)
                return Temporal.TryDateAddMonths(point.Bits, months, out var d) ? Value.Temporal(VKind.Date, d)
                    : throw Fail(Templates.TimeOverflow(span, "That date falls outside years 0001 to 9999."));
            return Temporal.TryStampAddMonths(point.Bits, months, out var s) ? Value.Temporal(VKind.Stamp, s)
                : throw Fail(Templates.TimeOverflow(span, "That moment falls outside the Stamp range (about 1678 to 2262)."));
        }
        Value TimeShift(long t, long delta)
        {
            long r = Chk(() => t + delta, "that time");
            if (r < 0 || r >= Temporal.NsPerDay)
                throw Fail(Templates.TimeOverflow(span, $"{Temporal.IsoTime(t)} {(delta < 0 ? "-" : "+")} {Temporal.DurText(Math.Abs(delta))} runs past midnight; use wrap_time(t, d) to wrap around."));
            return Value.Temporal(VKind.Time, r);
        }

        bool IsInt(Value v) => v.IsInt;
        switch (op)
        {
            case TokenKind.Plus:
                if (a.Kind == VKind.Date && b.Kind == VKind.Dur) return DateShift(a.Bits, WholeDays(b.Bits));
                if (a.Kind == VKind.Dur && b.Kind == VKind.Date) return DateShift(b.Bits, WholeDays(a.Bits));
                if ((a.Kind is VKind.Date or VKind.Stamp) && b.Kind == VKind.Cal) return AddMonths(a, b.Bits);
                if (a.Kind == VKind.Cal && (b.Kind is VKind.Date or VKind.Stamp)) return AddMonths(b, a.Bits);
                if (a.Kind == VKind.Stamp && b.Kind == VKind.Dur) return Value.Temporal(VKind.Stamp, Chk(() => a.Bits + b.Bits, "that moment"));
                if (a.Kind == VKind.Dur && b.Kind == VKind.Stamp) return Value.Temporal(VKind.Stamp, Chk(() => a.Bits + b.Bits, "that moment"));
                if (a.Kind == VKind.Time && b.Kind == VKind.Dur) return TimeShift(a.Bits, b.Bits);
                if (a.Kind == VKind.Dur && b.Kind == VKind.Time) return TimeShift(b.Bits, a.Bits);
                if (a.Kind == VKind.Dur && b.Kind == VKind.Dur) return Value.Temporal(VKind.Dur, Chk(() => a.Bits + b.Bits, "that duration"));
                if (a.Kind == VKind.Cal && b.Kind == VKind.Cal) return Value.Temporal(VKind.Cal, Chk(() => a.Bits + b.Bits, "that span"));
                break;
            case TokenKind.Minus:
                if (a.Kind == VKind.Date && b.Kind == VKind.Date) return Value.Temporal(VKind.Dur, Chk(() => (a.Bits - b.Bits) * Temporal.NsPerDay, "that difference"));
                if (a.Kind == b.Kind && a.Kind is VKind.Stamp or VKind.Time) return Value.Temporal(VKind.Dur, Chk(() => a.Bits - b.Bits, "that difference"));
                if (a.Kind == VKind.Date && b.Kind == VKind.Dur) return DateShift(a.Bits, -WholeDays(b.Bits));
                if ((a.Kind is VKind.Date or VKind.Stamp) && b.Kind == VKind.Cal) return AddMonths(a, -b.Bits);
                if (a.Kind == VKind.Stamp && b.Kind == VKind.Dur) return Value.Temporal(VKind.Stamp, Chk(() => a.Bits - b.Bits, "that moment"));
                if (a.Kind == VKind.Time && b.Kind == VKind.Dur) return TimeShift(a.Bits, -b.Bits);
                if (a.Kind == VKind.Dur && b.Kind == VKind.Dur) return Value.Temporal(VKind.Dur, Chk(() => a.Bits - b.Bits, "that duration"));
                if (a.Kind == VKind.Cal && b.Kind == VKind.Cal) return Value.Temporal(VKind.Cal, Chk(() => a.Bits - b.Bits, "that span"));
                break;
            case TokenKind.Star:
                if (a.Kind is VKind.Dur or VKind.Cal && IsInt(b)) return Value.Temporal(a.Kind, Chk(() => checked(a.Bits * (long)b.AsInt128), "that product"));
                if (IsInt(a) && b.Kind is VKind.Dur or VKind.Cal) return Value.Temporal(b.Kind, Chk(() => checked((long)a.AsInt128 * b.Bits), "that product"));
                break;
            case TokenKind.Slash:
                if (a.Kind is VKind.Dur or VKind.Cal && IsInt(b))
                {
                    if (b.AsInt128 == 0) throw Fail(Templates.DivideByZero(span));
                    return Value.Temporal(a.Kind, a.Bits / (long)b.AsInt128);
                }
                if (a.Kind == VKind.Dur && b.Kind == VKind.Dur)
                {
                    if (b.Bits == 0) throw Fail(Templates.DivideByZero(span));
                    return Value.Float(VKind.F64, (double)a.Bits / b.Bits);
                }
                break;
        }
        throw Fail(Templates.Internal(span, $"operator {op} on {a.TypeName} and {b.TypeName}"));
    }

    // ---- networking ----

    private string HttpText(HttpMethod method, string url, string? body, SourceSpan span)
    {
        try
        {
            return Blocking(() =>
            {
                using var req = new HttpRequestMessage(method, url);
                if (body is not null) req.Content = new StringContent(body, Encoding.UTF8, "text/plain");
                using var resp = Http.Send(req);
                var text = new StreamReader(resp.Content.ReadAsStream(), Encoding.UTF8).ReadToEnd();
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"the server answered {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trim(text)}");
                return text;
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or UriFormatException)
        {
            throw Fail(Templates.NetworkFail(span, url, e is TaskCanceledException ? "the request timed out after 10 seconds" : e.InnerException?.Message ?? e.Message));
        }
    }

    private static string Trim(string s) => s.Length > 80 ? s[..80] + "..." : s;

    private byte[] HttpBytes(string url, SourceSpan span)
    {
        try
        {
            return Blocking(() =>
            {
                using var resp = Http.Send(new HttpRequestMessage(HttpMethod.Get, url));
                if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"the server answered {(int)resp.StatusCode} {resp.ReasonPhrase}");
                using var ms = new MemoryStream();
                resp.Content.ReadAsStream().CopyTo(ms);
                return ms.ToArray();
            });
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or UriFormatException)
        {
            throw Fail(Templates.NetworkFail(span, url, e is TaskCanceledException ? "the request timed out after 10 seconds" : e.InnerException?.Message ?? e.Message));
        }
    }

    private Value Serve(int port, Closure handler, SourceSpan span)
    {
        if (port == 0)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        try { listener.Start(); }
        catch (Exception e) when (e is HttpListenerException or InvalidOperationException)
        { throw Fail(Templates.NetworkFail(span, $"port {port}", e.Message)); }

        var server = new ServerObj(listener, port);
        lock (_servers) _servers.Add(server);
        var thread = new Thread(() => ServeLoop(server, handler), 64 * 1024 * 1024) { IsBackground = true };
        server.Thread = thread;
        thread.Start();
        return Value.Obj(server);
    }

    private void ServeLoop(ServerObj server, Closure handler)
    {
        while (!server.Stopped && server.Listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = server.Listener.GetContext(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { break; }

            int status = 200; string body = ""; var headers = new List<(string, string)>();
            try
            {
                string reqBody;
                using (var rd = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) reqBody = rd.ReadToEnd();
                lock (_gil)
                {
                    var req = new ClassInst(_classes["Req"]);
                    req.Fields["method"] = Value.Str(ctx.Request.HttpMethod);
                    req.Fields["path"] = Value.Str(ctx.Request.Url?.AbsolutePath ?? "/");
                    req.Fields["query"] = Value.Map(NameValues(ctx.Request.QueryString));
                    req.Fields["headers"] = Value.Map(NameValues(ctx.Request.Headers));
                    req.Fields["body"] = Value.Str(reqBody);
                    try
                    {
                        var res = Call(handler, [Value.Obj(req)], [true], SourceSpan.None);
                        if (res.Kind == VKind.Object && res.AsObj is ClassInst ri && ri.Class.Name == "Res")
                        {
                            status = (int)ri.Fields["status"].Bits;
                            body = ri.Fields["body"].AsString;
                            foreach (var (k, v) in ri.Fields["headers"].AsMap.Items) headers.Add((k.AsString, v.AsString));
                        }
                    }
                    catch (RuntimeError e)
                    {
                        status = 500;
                        body = e.Diagnostic.Message;
                        _stderr.WriteLine(e.Diagnostic.Message);
                    }
                }
            }
            catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException) { }

            try
            {
                ctx.Response.StatusCode = status;
                foreach (var (k, v) in headers)
                    if (k.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) ctx.Response.ContentType = v;
                    else ctx.Response.Headers[k] = v;
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes);
                ctx.Response.Close();
            }
            catch (Exception e) when (e is IOException or HttpListenerException or ObjectDisposedException) { }
        }
        server.Stopped = true;
    }

    private static SplatMap NameValues(System.Collections.Specialized.NameValueCollection c)
    {
        var m = new SplatMap();
        foreach (var key in c.AllKeys)
            if (key is not null) m.Set(Value.Str(key), Value.Str(c[key] ?? ""));
        return m;
    }

    private Value CallStatic(string type, string name, CallExpr c, Env env)
    {
        var a = c.Args.Select(x => Eval(x.Value, env)).ToArray();
        if (type == "Res")
        {
            var res = new ClassInst(_classes["Res"]);
            var headers = new SplatMap();
            int status = 200; string body = a[^1].AsString;
            switch (name)
            {
                case "ok" or "text": headers.Set(Value.Str("Content-Type"), Value.Str("text/plain; charset=utf-8")); break;
                case "json": headers.Set(Value.Str("Content-Type"), Value.Str("application/json")); break;
                case "not_found": status = 404; headers.Set(Value.Str("Content-Type"), Value.Str("text/plain; charset=utf-8")); break;
                case "status": status = (int)a[0].Bits; headers.Set(Value.Str("Content-Type"), Value.Str("text/plain; charset=utf-8")); break;
            }
            res.Fields["status"] = Value.Int(VKind.I32, status);
            res.Fields["headers"] = Value.Map(headers);
            res.Fields["body"] = Value.Str(body);
            return Value.Obj(res);
        }

        // Socket.tcp / Socket.udp / Socket.listen
        var span = c.Span;
        try
        {
            switch (name)
            {
                case "tcp":
                {
                    string host = a[0].AsString; int port = (int)a[1].Bits;
                    var client = new TcpClient();
                    var ok = Blocking(() => client.ConnectAsync(host, port).Wait(TimeSpan.FromSeconds(5)));
                    if (!ok) throw new TimeoutException("connecting timed out after 5 seconds");
                    client.ReceiveTimeout = 5000;
                    return Value.Obj(new SocketObj { Client = client });
                }
                case "listen":
                {
                    var l = new TcpListener(IPAddress.Loopback, (int)a[0].Bits);
                    l.Start();
                    return Value.Obj(new SocketObj { Listener = l });
                }
                case "udp":
                {
                    var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, (int)a[0].Bits));
                    u.Client.ReceiveTimeout = 5000;
                    return Value.Obj(new SocketObj { Udp = u });
                }
            }
        }
        catch (Exception e) when (e is SocketException or TimeoutException or AggregateException or IOException or ObjectDisposedException)
        {
            throw Fail(Templates.NetworkFail(span, $"{type}.{name}", e.InnerException?.Message ?? e.Message));
        }
        throw Fail(Templates.Internal(span, $"{type}.{name}"));
    }

    private Value SocketMethod(SocketObj s, string name, Value[] a, SourceSpan span)
    {
        if (s.Dead) throw Fail(Templates.NetworkFail(span, "a closed socket", "the socket is already closed"));
        try
        {
            switch (name)
            {
                case "port": return Value.Int(VKind.I32, s.Port);
                case "close": Destroy(s, span); return Value.Unit;
                case "accept":
                {
                    var client = Blocking(() => s.Listener!.AcceptTcpClient());
                    client.ReceiveTimeout = 5000;
                    return Value.Obj(new SocketObj { Client = client });
                }
                case "send":
                {
                    var bytes = Encoding.UTF8.GetBytes(a[0].AsString);
                    s.Client!.GetStream().Write(bytes);
                    return Value.Unit;
                }
                case "send_to":
                {
                    var bytes = Encoding.UTF8.GetBytes(a[2].AsString);
                    string host = a[0].AsString; int port = (int)a[1].Bits;
                    s.Udp!.Send(bytes, bytes.Length, host, port);
                    return Value.Unit;
                }
                case "recv":
                    if (s.Udp is not null)
                    {
                        var data = Blocking(() => { var ep = new IPEndPoint(IPAddress.Any, 0); return s.Udp.Receive(ref ep); });
                        return Value.Str(Encoding.UTF8.GetString(data));
                    }
                    else
                    {
                        var buf = new byte[65536];
                        int n = Blocking(() => s.Client!.GetStream().Read(buf, 0, buf.Length));
                        return Value.Str(Encoding.UTF8.GetString(buf, 0, n));
                    }
            }
        }
        catch (Exception e) when (e is SocketException or IOException or NullReferenceException or InvalidOperationException or ObjectDisposedException)
        {
            throw Fail(Templates.NetworkFail(span, "the socket", e.InnerException?.Message ?? e.Message));
        }
        throw Fail(Templates.Internal(span, $"Socket.{name}"));
    }
}
