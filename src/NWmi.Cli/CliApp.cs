using System.CommandLine;
using System.CommandLine.Help;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NWmi.Auth;
using NWmi.Client;

namespace NWmi.Cli;

/// <summary>The ways the tool can open a connection; replaced in tests so no network is needed.</summary>
internal interface IWmiConnector
{
    Task<WmiConnection> ConnectAsync(string host, Credential credential, WmiConnectionOptions options,
        CancellationToken cancellationToken);

    Task<WmiConnection> ConnectAsync(string host, Credential credential, SpnegoOptions spnego,
        WmiConnectionOptions options, CancellationToken cancellationToken);

    Task<WmiConnection> ConnectAsync(string host, KerberosCredential credential, SpnegoOptions spnego,
        WmiConnectionOptions options, CancellationToken cancellationToken);

    Task<WmiConnection> ConnectWinRmAsync(string host, Credential credential, WmiConnectionOptions options,
        CancellationToken cancellationToken);

    Task<WmiConnection> ConnectWinRmAsync(string host, Credential credential, SpnegoOptions spnego,
        WmiConnectionOptions options, CancellationToken cancellationToken);
}

internal sealed class DefaultWmiConnector : IWmiConnector
{
    public Task<WmiConnection> ConnectAsync(string host, Credential credential, WmiConnectionOptions options,
        CancellationToken cancellationToken) =>
        WmiConnection.ConnectAsync(host, credential, options, cancellationToken);

    public Task<WmiConnection> ConnectAsync(string host, Credential credential, SpnegoOptions spnego,
        WmiConnectionOptions options, CancellationToken cancellationToken) =>
        WmiConnection.ConnectAsync(host, credential, spnego, options, cancellationToken);

    public Task<WmiConnection> ConnectAsync(string host, KerberosCredential credential, SpnegoOptions spnego,
        WmiConnectionOptions options, CancellationToken cancellationToken) =>
        WmiConnection.ConnectAsync(host, credential, spnego, options, cancellationToken);

    public Task<WmiConnection> ConnectWinRmAsync(string host, Credential credential, WmiConnectionOptions options,
        CancellationToken cancellationToken) =>
        WmiConnection.ConnectWinRmAsync(host, credential, options, cancellationToken);

    public Task<WmiConnection> ConnectWinRmAsync(string host, Credential credential, SpnegoOptions spnego,
        WmiConnectionOptions options, CancellationToken cancellationToken) =>
        WmiConnection.ConnectWinRmAsync(host, credential, spnego, options, cancellationToken);
}

/// <summary>The command line: option parsing, choosing a connection method, and writing the results.</summary>
internal static class CliApp
{
    public static async Task<int> RunAsync(
        string[] args, Stream stdout, TextWriter stdoutText, TextWriter stderr, IWmiConnector connector,
        CancellationToken cancellationToken = default)
    {
        var root = BuildCommand(stdout, stdoutText, stderr, connector);
        var configuration = new InvocationConfiguration { Output = stdoutText, Error = stderr };
        return await root.Parse(args).InvokeAsync(configuration, cancellationToken).ConfigureAwait(false);
    }

    private static RootCommand BuildCommand(Stream stdout, TextWriter stdoutText, TextWriter stderr,
        IWmiConnector connector)
    {
        var hostOption = new Option<string>("--host", "-h")
            { Description = "the remote host to connect to", Required = true };
        var userOption = new Option<string>("--user", "-u")
            { Description = "the account to log in as, optionally DOMAIN\\user" };
        var passwordOption = new Option<string>("--password", "-p") { Description = "the account's password" };
        var spnegoOption = new Option<bool>("--negotiate")
        {
            Description =
                "negotiate Kerberos or NTLM (SPNEGO) instead of NTLM only; the realm, KDC and SPN are found through DNS"
        };
        var ticketCacheOption = new Option<string>("--ticket-cache")
        {
            Description =
                "sign in with the ticket-granting ticket in this MIT-format credential cache file (needs --realm; no password)"
        };
        var logonSessionOption = new Option<bool>("--logon-session")
        {
            Description = "Windows only: sign in with Kerberos using the current domain logon (no user or password)"
        };
        var realmOption = new Option<string>("--realm")
        {
            Description = "the Kerberos realm, e.g. AD.CONTOSO.COM (default: from the account or the host's DNS name)"
        };
        var kdcOption = new Option<string>("--kdc")
            { Description = "the KDC's address (default: the realm's _kerberos._tcp DNS records)" };
        var spnOption = new Option<string>("--spn")
            { Description = "the target's service principal name (default: host/<its DNS name>)" };
        var dnsOption = new Option<string>("--dns")
            { Description = "a DNS server for the Kerberos lookups (default: the machine's own)" };
        var namespaceOption = new Option<string>("--namespace", "-n")
            { Description = "the namespace to query", DefaultValueFactory = _ => @"root\cimv2" };
        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "text, json (one array) or jsonl (one object per line)", DefaultValueFactory = _ => "text"
        };
        outputOption.AcceptOnlyFromAmong("text", "json", "jsonl");
        var winrmOption = new Option<bool>("--winrm")
        {
            Description =
                "connect over WinRM (HTTP 5985, NTLM; Kerberos with --negotiate) instead of DCOM; needs --user and --password"
        };
        var winrmHttpsOption = new Option<bool>("--winrm-https")
            { Description = "like --winrm, over HTTPS (5986)" };
        var insecureOption = new Option<bool>("--insecure")
        {
            Description =
                "with --winrm-https: accept any server certificate (self-signed, wrong name); the connection is then open to interception"
        };
        var contextOption = new Option<string[]>("--context", "-c")
        {
            Description =
                "send a name=value context with every call, e.g. __ProviderArchitecture=64 (true/false and whole numbers are typed, anything else is a string); repeatable; DCOM only",
            AllowMultipleArgumentsPerToken = false,
        };
        var verboseOption = new Option<bool>("--verbose", "-v") { Description = "log each step to stderr" };
        var queryArgument = new Argument<string>("query") { Description = "the WQL query to run" };

        var root = new RootCommand("nwmi: a sample WMI client")
        {
            hostOption,
            userOption,
            passwordOption,
            spnegoOption,
            ticketCacheOption,
            logonSessionOption,
            realmOption,
            kdcOption,
            spnOption,
            dnsOption,
            namespaceOption,
            contextOption,
            outputOption,
            verboseOption,
            queryArgument,
        };

        // -h is the host (as in the original tool), so help is --help and -? only.
        for (var i = root.Options.Count - 1; i >= 0; i--)
            if (root.Options[i] is HelpOption)
                root.Options.RemoveAt(i);

        root.Options.Add(winrmOption);
        root.Options.Add(winrmHttpsOption);
        root.Options.Add(insecureOption);
        root.Options.Add(new HelpOption("--help", "-?"));

        root.SetAction(async (parseResult, cancellationToken) =>
        {
            var output = parseResult.GetRequiredValue(outputOption);
            var verbose = parseResult.GetValue(verboseOption);

            try
            {
                var options = new WmiConnectionOptions
                {
                    Namespace = parseResult.GetRequiredValue(namespaceOption),
                    LoggerFactory = verbose
                        ? LoggerFactory.Create(b =>
                            b.SetMinimumLevel(LogLevel.Debug).AddSimpleConsole(o => o.SingleLine = true))
                        : null,
                    Context = ParseContext(parseResult.GetValue(contextOption))
                };

                var winRmHttps = parseResult.GetValue(winrmHttpsOption);
                options.WinRmUseHttps = winRmHttps;
                var insecure = parseResult.GetValue(insecureOption);
                if (insecure && !winRmHttps)
                    throw new ArgumentException("--insecure only applies to --winrm-https.");

                options.WinRmIgnoreCertificateErrors = insecure;
                var host = parseResult.GetRequiredValue(hostOption);
                var account = parseResult.GetValue(userOption);
                var password = parseResult.GetValue(passwordOption);
                var ticketCache = parseResult.GetValue(ticketCacheOption);
                var logonSession = parseResult.GetValue(logonSessionOption);
                var spnego = new SpnegoOptions
                {
                    Realm = parseResult.GetValue(realmOption),
                    KdcHost = parseResult.GetValue(kdcOption),
                    ServicePrincipalName = parseResult.GetValue(spnOption),
                    DnsServers = parseResult.GetValue(dnsOption) is { } dns ? [System.Net.IPAddress.Parse(dns)] : null,
                };

                Task<WmiConnection> connecting;
                if (logonSession || ticketCache is not null)
                {
                    if (logonSession && ticketCache is not null)
                        throw new ArgumentException("Use either --logon-session or --ticket-cache, not both.");

                    KerberosCredential kerberos;
                    if (logonSession)
                    {
                        if (!OperatingSystem.IsWindows())
                            throw new PlatformNotSupportedException("--logon-session only works on Windows.");

                        kerberos = KerberosCredential.FromLogonSession();
                    }
                    else
                    {
                        var realm = spnego.Realm ?? throw new ArgumentException("--ticket-cache needs --realm.");
                        kerberos = KerberosCredential.FromTicketCache(ticketCache!, realm);
                    }

                    connecting = connector.ConnectAsync(host, kerberos, spnego, options, cancellationToken);
                }
                else if (parseResult.GetValue(winrmOption) || winRmHttps)
                {
                    if (account is null || password is null)
                        throw new ArgumentException("--winrm needs --user and --password.");

                    if (options.Context is { IsEmpty: false })
                        throw new ArgumentException("--context needs DCOM; it is not available with --winrm.");

                    connecting = parseResult.GetValue(spnegoOption)
                        ? connector.ConnectWinRmAsync(host, Credential.Parse(account, password), spnego, options,
                            cancellationToken)
                        : connector.ConnectWinRmAsync(host, Credential.Parse(account, password), options,
                            cancellationToken);
                }
                else
                {
                    if (account is null || password is null)
                        throw new ArgumentException(
                            "--user and --password are required (or use --ticket-cache or --logon-session).");

                    var credential = Credential.Parse(account, password);
                    connecting = parseResult.GetValue(spnegoOption)
                        ? connector.ConnectAsync(host, credential, spnego, options, cancellationToken)
                        : connector.ConnectAsync(host, credential, options, cancellationToken);
                }

                await using var wmi = await connecting.ConfigureAwait(false);
                var results = wmi.QueryAsync(parseResult.GetRequiredValue(queryArgument),
                    cancellationToken: cancellationToken);
                var any = await WriteResultsAsync(results, output, stdout, stdoutText, cancellationToken)
                    .ConfigureAwait(false);
                if (!any)
                    await stderr.WriteLineAsync("nwmi: no results.").ConfigureAwait(false);

                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await stderr.WriteLineAsync($"nwmi: {ex.Message}").ConfigureAwait(false);
                return 1;
            }
        });

        return root;
    }

    /// <summary>Turns repeated <c>name=value</c> arguments into a context: <c>true</c>/<c>false</c> are booleans, whole numbers integers, the rest strings.</summary>
    internal static WmiCallContext? ParseContext(string[]? entries)
    {
        if (entries is not { Length: > 0 })
            return null;

        var context = WmiCallContext.Empty;
        foreach (var entry in entries)
        {
            var split = entry.IndexOf('=', StringComparison.Ordinal);
            if (split <= 0)
                throw new ArgumentException($"--context '{entry}' is not name=value.");

            var name = entry[..split];
            var text = entry[(split + 1)..];
            context = bool.TryParse(text, out var flag)
                ? context.With(name, flag)
                : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                    ? context.With(name, number)
                    : context.With(name, text);
        }

        return context;
    }

    /// <summary>Writes every object in the chosen format ("text", "json" or "jsonl"); returns whether there were any.</summary>
    internal static async Task<bool> WriteResultsAsync(
        IAsyncEnumerable<WmiObject> results, string output, Stream stdout, TextWriter stdoutText,
        CancellationToken cancellationToken = default)
    {
        var any = false;
        await using var writer = IResultWriter.Create(output, stdout, stdoutText);
        await writer.BeginAsync().ConfigureAwait(false);
        await foreach (var obj in results.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            any = true;
            await writer.WriteAsync(obj).ConfigureAwait(false);
        }

        await writer.EndAsync().ConfigureAwait(false);
        return any;
    }
}
