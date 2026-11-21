using System.Text;
using System.Text.Json;
using NWmi.Auth;
using NWmi.Cli;
using NWmi.Client;
using NWmi.Dcom;
using NWmi.Ndr;
using NWmi.Ndr.Ole;
using NWmi.Rpc;
using NWmi.Rpc.Pdu;
using NWmi.Tests.Rpc;
using NWmi.Wmi;

namespace NWmi.Tests.Cli;

public class CliAppTests
{
    private sealed class StopException : Exception
    {
        public StopException() : base("stop") { }
    }

    /// <summary>Records which connect method the tool chose, then fails (or hands back a prepared connection).</summary>
    private sealed class RecordingConnector : IWmiConnector
    {
        public WmiConnection? Connection { get; init; }
        public string? Method { get; private set; }
        public string? Host { get; private set; }
        public Credential? Credential { get; private set; }
        public KerberosCredential? Kerberos { get; private set; }
        public SpnegoOptions? Spnego { get; private set; }
        public WmiConnectionOptions? Options { get; private set; }

        private Task<WmiConnection> Record(string method, string host, WmiConnectionOptions options)
        {
            Method = method;
            Host = host;
            Options = options;
            return Connection is null ? Task.FromException<WmiConnection>(new StopException()) : Task.FromResult(Connection);
        }

        public Task<WmiConnection> ConnectAsync(string host, Credential credential, WmiConnectionOptions options, CancellationToken cancellationToken)
        {
            Credential = credential;
            return Record("ntlm", host, options);
        }

        public Task<WmiConnection> ConnectAsync(string host, Credential credential, SpnegoOptions spnego, WmiConnectionOptions options, CancellationToken cancellationToken)
        {
            Credential = credential;
            Spnego = spnego;
            return Record("negotiate", host, options);
        }

        public Task<WmiConnection> ConnectAsync(string host, KerberosCredential credential, SpnegoOptions spnego, WmiConnectionOptions options, CancellationToken cancellationToken)
        {
            Kerberos = credential;
            Spnego = spnego;
            return Record("kerberos", host, options);
        }

        public Task<WmiConnection> ConnectWinRmAsync(string host, Credential credential, WmiConnectionOptions options, CancellationToken cancellationToken)
        {
            Credential = credential;
            return Record("winrm", host, options);
        }

        public Task<WmiConnection> ConnectWinRmAsync(string host, Credential credential, SpnegoOptions spnego, WmiConnectionOptions options, CancellationToken cancellationToken)
        {
            Credential = credential;
            Spnego = spnego;
            return Record("winrm-negotiate", host, options);
        }
    }

    private sealed record Result(int ExitCode, string Stdout, string Console, string Stderr);

    private static async Task<Result> RunAsync(RecordingConnector connector, params string[] args)
    {
        using var stdout = new MemoryStream();
        var console = new StringWriter();
        var stderr = new StringWriter();
        var exit = await CliApp.RunAsync(args, stdout, console, stderr, connector, TestContext.Current.CancellationToken);
        return new Result(exit, Encoding.UTF8.GetString(stdout.ToArray()), console.ToString(), stderr.ToString());
    }

    // ---- connection choice -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_user_and_password_connect_with_NTLM_over_DCOM_and_split_the_domain()
    {
        var connector = new RecordingConnector();
        var result = await RunAsync(connector, "-h", "server01", "-u", @"CORP\admin", "-p", "pw", "SELECT 1");

        Assert.Equal(1, result.ExitCode); // the connector refuses; only the choice is checked
        Assert.Equal("nwmi: stop" + Environment.NewLine, result.Stderr);
        Assert.Equal("ntlm", connector.Method);
        Assert.Equal("server01", connector.Host);
        Assert.Equal("admin", connector.Credential!.UserName);
        Assert.Equal("CORP", connector.Credential.Domain);
        Assert.Equal("pw", connector.Credential.Password);
        Assert.Equal(@"root\cimv2", connector.Options!.Namespace);
        Assert.False(connector.Options.WinRmUseHttps);
        Assert.Null(connector.Options.LoggerFactory);
    }

    [Fact]
    public async Task A_plain_user_name_has_no_domain()
    {
        var connector = new RecordingConnector();
        await RunAsync(connector, "-h", "server01", "-u", "admin", "-p", "pw", "SELECT 1");

        Assert.Equal("admin", connector.Credential!.UserName);
        Assert.Equal(string.Empty, connector.Credential.Domain);
    }

    [Fact]
    public async Task The_namespace_option_is_passed_on()
    {
        var connector = new RecordingConnector();
        await RunAsync(connector, "-h", "server01", "-u", "a", "-p", "b", "-n", @"root\default", "SELECT 1");

        Assert.Equal(@"root\default", connector.Options!.Namespace);
    }

    [Fact]
    public async Task Negotiate_passes_the_Kerberos_settings_through()
    {
        var connector = new RecordingConnector();
        await RunAsync(connector, "-h", "server01", "-u", "a", "-p", "b", "--negotiate",
            "--realm", "AD.CONTOSO.COM", "--kdc", "10.0.0.1", "--spn", "host/server01.ad.contoso.com", "--dns", "10.0.0.2",
            "SELECT 1");

        Assert.Equal("negotiate", connector.Method);
        Assert.Equal("AD.CONTOSO.COM", connector.Spnego!.Realm);
        Assert.Equal("10.0.0.1", connector.Spnego.KdcHost);
        Assert.Equal("host/server01.ad.contoso.com", connector.Spnego.ServicePrincipalName);
        Assert.Equal([System.Net.IPAddress.Parse("10.0.0.2")], connector.Spnego.DnsServers);
    }

    [Fact]
    public async Task Winrm_uses_the_WinRM_connector_over_HTTP()
    {
        var connector = new RecordingConnector();
        await RunAsync(connector, "-h", "server01", "-u", "a", "-p", "b", "--winrm", "SELECT 1");

        Assert.Equal("winrm", connector.Method);
        Assert.False(connector.Options!.WinRmUseHttps);
        Assert.False(connector.Options.WinRmIgnoreCertificateErrors);
    }

    [Fact]
    public async Task Winrm_https_implies_winrm_over_HTTPS_without_certificate_checks()
    {
        var connector = new RecordingConnector();
        await RunAsync(connector, "-h", "server01", "-u", "a", "-p", "b", "--winrm-https", "SELECT 1");

        Assert.Equal("winrm", connector.Method);
        Assert.True(connector.Options!.WinRmUseHttps);
        Assert.True(connector.Options.WinRmIgnoreCertificateErrors);
    }

    [Fact]
    public async Task Winrm_with_negotiate_uses_the_SPNEGO_WinRM_connector()
    {
        var connector = new RecordingConnector();
        await RunAsync(connector, "-h", "server01", "-u", "a", "-p", "b", "--winrm", "--negotiate", "SELECT 1");

        Assert.Equal("winrm-negotiate", connector.Method);
    }

    [Fact]
    public async Task A_ticket_cache_with_a_realm_uses_the_Kerberos_connector_and_needs_no_password()
    {
        var connector = new RecordingConnector();
        var path = Path.Combine(Path.GetTempPath(), "nwmi-test-ccache");
        await RunAsync(connector, "-h", "server01", "--ticket-cache", path, "--realm", "AD.CONTOSO.COM", "SELECT 1");

        Assert.Equal("kerberos", connector.Method);
        Assert.Equal(path, connector.Kerberos!.TicketCachePath);
        Assert.False(connector.Kerberos.UsesLogonSession);
    }

    // ---- argument errors ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "-h", "s", "SELECT 1" }, "--user and --password are required")]
    [InlineData(new[] { "-h", "s", "-u", "a", "SELECT 1" }, "--user and --password are required")]
    [InlineData(new[] { "-h", "s", "--winrm", "SELECT 1" }, "--winrm needs --user and --password.")]
    [InlineData(new[] { "-h", "s", "-u", "a", "--winrm-https", "SELECT 1" }, "--winrm needs --user and --password.")]
    [InlineData(new[] { "-h", "s", "--ticket-cache", "x", "SELECT 1" }, "--ticket-cache needs --realm.")]
    [InlineData(new[] { "-h", "s", "--ticket-cache", "x", "--logon-session", "SELECT 1" }, "either --logon-session or --ticket-cache")]
    public async Task Bad_combinations_exit_1_with_an_explanation_and_never_connect(string[] args, string message)
    {
        var connector = new RecordingConnector();
        var result = await RunAsync(connector, args);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains(message, result.Stderr, StringComparison.Ordinal);
        Assert.StartsWith("nwmi: ", result.Stderr, StringComparison.Ordinal);
        Assert.Null(connector.Method);
        Assert.Empty(result.Stdout);
    }

    [Fact]
    public async Task A_malformed_DNS_address_is_reported_not_thrown()
    {
        var connector = new RecordingConnector();
        var result = await RunAsync(connector, "-h", "s", "-u", "a", "-p", "b", "--dns", "not-an-ip", "SELECT 1");

        Assert.Equal(1, result.ExitCode);
        Assert.StartsWith("nwmi: ", result.Stderr, StringComparison.Ordinal);
        Assert.Null(connector.Method);
    }

    [Theory]
    [InlineData(new[] { "-u", "a", "-p", "b", "SELECT 1" }, "--host")] // host missing
    [InlineData(new[] { "-h", "s", "-u", "a", "-p", "b" }, "Required argument missing")] // query missing
    [InlineData(new[] { "-h", "s", "-u", "a", "-p", "b", "-o", "xml", "SELECT 1" }, "xml")] // bad output format
    public async Task Parse_errors_exit_nonzero_without_connecting(string[] args, string mention)
    {
        var connector = new RecordingConnector();
        var result = await RunAsync(connector, args);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(mention, result.Stderr, StringComparison.Ordinal);
        Assert.Null(connector.Method);
    }

    [Fact]
    public async Task Help_lists_the_options_and_dash_h_is_not_help()
    {
        var connector = new RecordingConnector();
        var result = await RunAsync(connector, "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--host", result.Console, StringComparison.Ordinal);
        Assert.Contains("--winrm-https", result.Console, StringComparison.Ordinal);
        Assert.Contains("--ticket-cache", result.Console, StringComparison.Ordinal);
        Assert.Null(connector.Method);

        var short_ = await RunAsync(new RecordingConnector(), "-?");
        Assert.Equal(0, short_.ExitCode);
        Assert.Contains("--host", short_.Console, StringComparison.Ordinal);
    }

    // ---- a whole run against a fake server ---------------------------------------------------------------------

    private static readonly Guid RemUnknownIpid = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private const ulong ServicesOxid = 0x9999;
    private static readonly StdObjRef ServicesStd = new(0, 5, ServicesOxid, 0x1, Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

    private static async Task<(WmiConnection Connection, FakeRpcServer Server)> FakeConnectionAsync(Func<int, byte[][]> instances)
    {
        var (clientSide, serverSide) = LoopbackTransport.CreatePair();
        var server = new FakeRpcServer(serverSide);
        server.Start();

        var rpcClient = new RpcClient(clientSide);
        var exporterConnection = await ExporterConnection.FromRpcClientAsync(rpcClient, RemUnknownIpid, security: null, cancellationToken: default);
        var connection = await WmiOrpcConnection.FromExporterConnectionAsync(
            exporterConnection, ownsConnection: true, ServicesStd, WmiIds.IidWbemServices, security: null, cancellationToken: default);
        var wmi = WmiConnection.FromServices(new WbemServicesClient(connection), "server01", @"root\cimv2",
            new WmiConnectionOptions { UseSmartEnumeration = false, BatchSize = 1 });

        var enumStd = new StdObjRef(0, 5, ServicesOxid, 0x2, Guid.Parse("bbbbbbbb-cccc-dddd-eeee-ffffffffffff"));
        var enumObjRef = new StandardObjRef(WmiIds.IidEnumWbemClassObject, enumStd, new DualStringArray([], [])).ToBytes();
        var nextCalls = 0;
        server.CallHandler = fragments =>
        {
            var reply = new NdrWriter();
            reply.WriteUInt32(0);
            reply.WriteUInt32(0); // ORPCTHAT
            switch (fragments[0].OpNum)
            {
                case 20: // ExecQuery
                    reply.WriteMInterfacePointer(enumObjRef);
                    reply.WriteInt32(0);
                    break;
                case 4: // Next
                    var batch = instances(nextCalls++);
                    reply.WriteMaxCount(batch.Length);
                    reply.WriteUInt32(0);
                    reply.WriteMaxCount(batch.Length);
                    foreach (var objRef in batch)
                        reply.Struct(w => w.WriteMInterfacePointer(objRef));

                    reply.WriteUInt32((uint)batch.Length);
                    reply.WriteInt32(batch.Length == 0 ? (int)WbemStatus.False : 0);
                    break;
                default:
                    reply.WriteInt32(0);
                    break;
            }

            return [new RpcResponsePdu { CallId = fragments[0].CallId, ContextId = fragments[0].ContextId, Stub = reply.ToArray() }];
        };
        return (wmi, server);
    }

    private static byte[] ProcessObjRef(string handle)
    {
        var @class = new NWmi.Wmio.WmioClass(
            "Win32_Process", [], [],
            [new NWmi.Wmio.WmioProperty("Handle", NWmi.Wmio.CimType.String, false, 0, 0, [], new NWmi.Wmio.NullAndDefaultFlag(true, false), null, 0)], []);
        var block = NWmi.Wmio.WmioObjectBlock.ForInstance(NWmi.Wmio.WmioInstance.Create(@class, new Dictionary<string, object?> { ["Handle"] = handle }));
        return WmiClassObjectCodec.ToObjRefBytes(block);
    }

    [Fact]
    public async Task A_query_prints_one_line_per_object_and_exits_0()
    {
        var (wmi, server) = await FakeConnectionAsync(call => call == 0 ? [ProcessObjRef("4")] : []);
        await using (server)
        {
            var result = await RunAsync(new RecordingConnector { Connection = wmi }, "-h", "server01", "-u", "a", "-p", "b", "SELECT Handle FROM Win32_Process");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("Handle=4" + Environment.NewLine, result.Console);
            Assert.Empty(result.Stderr);
        }
    }

    [Fact]
    public async Task A_query_with_no_matches_says_so_on_stderr_and_still_exits_0()
    {
        var (wmi, server) = await FakeConnectionAsync(_ => []);
        await using (server)
        {
            var result = await RunAsync(new RecordingConnector { Connection = wmi }, "-h", "server01", "-u", "a", "-p", "b", "SELECT 1");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("nwmi: no results." + Environment.NewLine, result.Stderr);
            Assert.Empty(result.Console);
        }
    }

    [Fact]
    public async Task Json_output_of_a_query_with_no_matches_is_an_empty_array()
    {
        var (wmi, server) = await FakeConnectionAsync(_ => []);
        await using (server)
        {
            var result = await RunAsync(new RecordingConnector { Connection = wmi }, "-h", "server01", "-u", "a", "-p", "b", "-o", "json", "SELECT 1");

            Assert.Equal(0, result.ExitCode);
            Assert.Equal("[]", result.Stdout.Trim());
        }
    }

    [Fact]
    public async Task Context_options_become_a_typed_call_context_on_the_connection_options()
    {
        var connector = new RecordingConnector();
        await RunAsync(connector, "-h", "s", "-u", "a", "-p", "b",
            "--context", "__ProviderArchitecture=64", "-c", "__RequiredArchitecture=true", "-c", "Locale=MS_409", "-c", "Expr=a=b",
            "SELECT 1");

        Assert.Equal("ntlm", connector.Method);
        Assert.Equal(
            new Dictionary<string, object>
            {
                ["__ProviderArchitecture"] = 64,
                ["__RequiredArchitecture"] = true,
                ["Locale"] = "MS_409",
                ["Expr"] = "a=b",
            },
            connector.Options!.Context!.Values);
    }

    [Fact]
    public async Task Without_context_options_there_is_no_context()
    {
        var connector = new RecordingConnector();
        await RunAsync(connector, "-h", "s", "-u", "a", "-p", "b", "SELECT 1");

        Assert.Null(connector.Options!.Context);
    }

    [Theory]
    [InlineData("novalue")]
    [InlineData("=value")]
    public async Task A_context_entry_that_is_not_name_equals_value_is_reported_without_connecting(string entry)
    {
        var connector = new RecordingConnector();
        var result = await RunAsync(connector, "-h", "s", "-u", "a", "-p", "b", "--context", entry, "SELECT 1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("is not name=value", result.Stderr, StringComparison.Ordinal);
        Assert.Null(connector.Method);
    }

    [Fact]
    public async Task A_context_cannot_be_used_over_WinRM()
    {
        var connector = new RecordingConnector();
        var result = await RunAsync(connector, "-h", "s", "-u", "a", "-p", "b", "--winrm", "--context", "A=1", "SELECT 1");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("--context needs DCOM", result.Stderr, StringComparison.Ordinal);
        Assert.Null(connector.Method);
    }

    // ---- output formats ----------------------------------------------------------------------------------------

    private static WmiObject Make(string className, params (string Name, object? Value)[] values) =>
        new(className, values.Select(v => KeyValuePair.Create(v.Name, v.Value)), [], null, null);

    private static async IAsyncEnumerable<WmiObject> Stream(params WmiObject[] objects)
    {
        foreach (var o in objects)
            yield return o;

        await Task.CompletedTask;
    }

    private static async Task<(bool Any, string Json, string Text)> WriteAsync(string format, params WmiObject[] objects)
    {
        using var stdout = new MemoryStream();
        var text = new StringWriter();
        var any = await CliApp.WriteResultsAsync(Stream(objects), format, stdout, text, TestContext.Current.CancellationToken);
        return (any, Encoding.UTF8.GetString(stdout.ToArray()), text.ToString());
    }

    [Fact]
    public async Task Text_output_is_name_equals_value_with_invariant_formatting()
    {
        var obj = Make("X",
            ("Name", "a b"),
            ("Flag", true),
            ("Off", false),
            ("Nothing", null),
            ("Ratio", 1.5),
            ("Numbers", new object?[] { 1, 2 }));

        var (any, _, text) = await WriteAsync("text", obj);

        Assert.True(any);
        Assert.Equal("Name=a b Flag=true Off=false Nothing=null Ratio=1.5 Numbers=[1, 2]" + Environment.NewLine, text);
    }

    [Fact]
    public async Task Text_output_nests_embedded_objects_in_braces()
    {
        var inner = Make("Inner", ("X", 5), ("Y", "z"));
        var (_, _, text) = await WriteAsync("text", Make("Outer", ("Child", inner), ("List", new object?[] { inner })));

        Assert.Equal("Child={X=5 Y=z} List=[{X=5 Y=z}]" + Environment.NewLine, text);
    }

    [Fact]
    public async Task Json_output_is_one_indented_array_with_the_class_name_first()
    {
        var (any, json, _) = await WriteAsync("json", Make("A", ("Id", 1)), Make("B", ("Id", 2)));

        Assert.True(any);
        var parsed = JsonDocument.Parse(json).RootElement;
        Assert.Equal(JsonValueKind.Array, parsed.ValueKind);
        Assert.Equal(2, parsed.GetArrayLength());
        Assert.Equal("A", parsed[0].GetProperty("__CLASS").GetString());
        Assert.Equal(2, parsed[1].GetProperty("Id").GetInt32());
        Assert.Contains(Environment.NewLine, json.Trim(), StringComparison.Ordinal); // indented
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
        Assert.Equal("__CLASS", parsed[0].EnumerateObject().First().Name);
    }

    [Fact]
    public async Task Jsonl_output_is_one_compact_object_per_line()
    {
        var (_, json, _) = await WriteAsync("jsonl", Make("A", ("Id", 1)), Make("B", ("Id", 2)));

        var lines = json.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("""{"__CLASS":"A","Id":1}""", lines[0]);
        Assert.Equal("""{"__CLASS":"B","Id":2}""", lines[1]);
        Assert.EndsWith("\n", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_values_keep_their_types_and_full_integer_precision()
    {
        var obj = Make("T",
            ("Text", "quote \" and ünï"),
            ("Bool", true),
            ("Null", null),
            ("Byte", (byte)255),
            ("Neg", short.MinValue),
            ("Big", ulong.MaxValue),
            ("Long", long.MinValue),
            ("Double", 0.25),
            ("Char", 'x'),
            ("Date", "20260101000000.000000+000"),
            ("Array", new object?[] { "a", 2, null }),
            ("Child", Make("Inner", ("X", 1))));

        var (_, json, _) = await WriteAsync("jsonl", obj);

        var o = JsonDocument.Parse(json).RootElement;
        Assert.Equal("quote \" and ünï", o.GetProperty("Text").GetString());
        Assert.True(o.GetProperty("Bool").GetBoolean());
        Assert.Equal(JsonValueKind.Null, o.GetProperty("Null").ValueKind);
        Assert.Equal(255, o.GetProperty("Byte").GetInt32());
        Assert.Equal(short.MinValue, o.GetProperty("Neg").GetInt16());
        Assert.Equal(ulong.MaxValue, o.GetProperty("Big").GetUInt64());
        Assert.Equal(long.MinValue, o.GetProperty("Long").GetInt64());
        Assert.Equal(0.25, o.GetProperty("Double").GetDouble());
        Assert.Equal("x", o.GetProperty("Char").GetString());
        Assert.Equal("20260101000000.000000+000", o.GetProperty("Date").GetString());
        var array = o.GetProperty("Array");
        Assert.Equal("a", array[0].GetString());
        Assert.Equal(2, array[1].GetInt32());
        Assert.Equal(JsonValueKind.Null, array[2].ValueKind);
        Assert.Equal("Inner", o.GetProperty("Child").GetProperty("__CLASS").GetString());
        Assert.Equal(1, o.GetProperty("Child").GetProperty("X").GetInt32());
    }

    [Fact]
    public async Task No_objects_means_Any_is_false_in_every_format()
    {
        foreach (var format in new[] { "text", "json", "jsonl" })
        {
            var (any, json, text) = await WriteAsync(format);
            Assert.False(any);
            Assert.Empty(text);
            Assert.Equal(format == "json" ? "[]" : string.Empty, json.Trim());
        }
    }

    [Theory]
    [InlineData(null, "null")]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    [InlineData(42, "42")]
    [InlineData(1.5, "1.5")]
    [InlineData("s", "s")]
    public void Format_renders_scalars(object? value, string expected) => Assert.Equal(expected, TextResultWriter.Format(value));

    [Fact]
    public void Format_does_not_depend_on_the_current_culture()
    {
        var saved = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", TextResultWriter.Format(1.5));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = saved;
        }
    }
}
