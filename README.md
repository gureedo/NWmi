# NWmi

A pure managed WMI client for .NET. It talks to a remote Windows machine over DCOM/RPC or WinRM without
`System.Management`, COM interop or any native dependency.

```csharp
using NWmi.Auth;
using NWmi.Client;

await using var wmi = await WmiConnection.ConnectAsync(
    "server01", new Credential("admin", "password", "CORP"));

await foreach (var os in wmi.QueryAsync("SELECT Caption, Version FROM Win32_OperatingSystem"))
    Console.WriteLine($"{os["Caption"]} {os["Version"]}");
```

## Features

- **WQL queries**, streamed as `IAsyncEnumerable<WmiInstance>`
- **Classes and instances:** get an instance by path or a class by name, enumerate instances and classes, read a
  class's properties, methods and qualifiers, open child namespaces
- **Methods:** call static and instance methods, with array and embedded-object parameters
- **Writing:** create, update and delete instances
- **Events:** event queries, delivered by polling
- **Refreshers:** register performance objects once and re-read them all with one call
- **Two transports:** DCOM over TCP (port 135 plus the dynamic RPC range) or WinRM (5985/5986)
- **Authentication:** NTLMv2 (password), Kerberos (password, ticket cache or the Windows logon session)
  and SPNEGO, which negotiates between the two
- **Encryption:** packet privacy by default over DCOM; sealed messages or HTTPS over WinRM
- **A command-line tool**, `nwmi`, with text, JSON and JSON Lines output

## Status

Version 0.0.1. The library works against Windows 11 and Windows Server 2025, but the API may still change and there is
no NuGet package yet. Older Windows versions, and running the client on Linux or macOS, have not been tested.

## Requirements

- .NET 10 SDK to build; the library targets `net10.0`
- On the target: remote WMI allowed through the firewall for DCOM, or a WinRM listener for WinRM
- An account allowed to use WMI remotely (normally a local or domain administrator)

## Installation

Until a package is published, build from source and reference the project or the package it produces:

```
git clone <repository-url>
cd NWmi
dotnet build
dotnet pack src/NWmi -c Release
```

## Usage

### Connecting

| Call | Authentication |
|---|---|
| `ConnectAsync(host, Credential)` or `(host, NetworkCredential)` | NTLMv2 |
| `ConnectAsync(host, Credential, SpnegoOptions)` | SPNEGO: Kerberos if a ticket can be had, NTLM otherwise |
| `ConnectAsync(host, KerberosCredential, kdcHost, servicePrincipalName)` | Kerberos only |
| `ConnectWinRmAsync(host, Credential)` | NTLM over WinRM |
| `ConnectWinRmAsync(host, Credential, SpnegoOptions)` | SPNEGO over WinRM |

`WmiConnectionOptions` sets the namespace (default `root\cimv2`), timeouts, the protection level, keep-alive,
logging and the WinRM port and HTTPS settings.

```csharp
// Kerberos with NTLM as the fallback. The realm, KDC and SPN are found through DNS.
var credential = new Credential("Administrator", "password", "CONTOSO");
await using var wmi = await WmiConnection.ConnectAsync("w1.ad.contoso.com", credential, new SpnegoOptions());

// Kerberos without a password: a ticket cache written by kinit, or the current Windows logon.
var fromCache = KerberosCredential.FromTicketCache("/tmp/krb5cc_1000", "AD.CONTOSO.COM");
var fromLogon = KerberosCredential.FromLogonSession();

// WinRM instead of DCOM.
await using var winrm = await WmiConnection.ConnectWinRmAsync(
    "server01", credential, new WmiConnectionOptions { WinRmUseHttps = true });
```

### Reading

```csharp
await foreach (var service in wmi.QueryAsync("SELECT Name, State FROM Win32_Service WHERE StartMode = 'Auto'"))
    Console.WriteLine($"{service["Name"]}: {service["State"]}");

var disk = await wmi.GetInstanceAsync("Win32_LogicalDisk.DeviceID=\"C:\"");
Console.WriteLine(disk["FreeSpace"]);
Console.WriteLine(disk.SystemProperties.Path); // \\SERVER01\root\cimv2:Win32_LogicalDisk.DeviceID="C:"

await foreach (var cls in wmi.EnumerateClassesAsync())
    Console.WriteLine(cls.Name);

// ASSOCIATORS OF / REFERENCES OF, over DCOM and WinRM alike
await foreach (var dependency in wmi.GetAssociatorsAsync("Win32_Service.Name=\"WinRM\"",
                   associationClass: "Win32_DependentService", role: "Dependent"))
    Console.WriteLine(dependency["Name"]);

await foreach (var link in wmi.GetReferencesAsync("Win32_LogicalDisk.DeviceID=\"C:\"",
                   resultClass: "Win32_LogicalDiskToPartition"))
{
    var partition = (WmiInstance)link["Antecedent"]!;   // a reference: the keys of the instance it refers to
    Console.WriteLine(partition["DeviceID"]);            // Disk #0, Partition #1
    Console.WriteLine((await wmi.GetInstanceAsync(partition.SystemProperties.Path!))["Size"]);
}
```

Property values come back typed, the same over DCOM and WinRM:

| CIM type | .NET type |
|---|---|
| numbers, `boolean`, `char16`, `string` | `byte`, `sbyte`, `ushort`, `short`, `uint`, `int`, `ulong`, `long`, `float`, `double`, `bool`, `char`, `string` |
| `datetime`, a timestamp | `DateTime` in UTC (`DateTimeKind.Utc`); the server's UTC offset is not kept |
| `datetime`, an interval | `TimeSpan` |
| `datetime` with wildcards (`2026****093000.000000+060`) | the DMTF string |
| reference | `WmiInstance` built from the path: its class, namespace, server and key properties |
| embedded object | `WmiInstance` |
| arrays | an array of the element type: `string[]`, `uint[]`, `DateTime[]`, `WmiInstance[]`, ... |

On write a datetime also takes a `DateTimeOffset` or a DMTF string, a reference a path string or any `WmiInstance` (sent
as its path), and an array any sequence. A not-found instance or class throws a `WmiException` with
`WBEM_E_NOT_FOUND`.

`Properties` also gives each property's CIM type (`WmiType`), its flags (`WmiFlags`: `Key`, `ReadOnly`, `Required`,
`NullValue`, `NotModified`, ...), and whether it has been set since it was read:

```csharp
var handle = process.Properties["Handle"]!;
Console.WriteLine($"{handle.Type} {handle.Flags}"); // String Property, Key, ReadOnly, NotModified
```

A class (`WmiClass`) describes its properties, methods and qualifiers; every instance has one in `Class`:

```csharp
var processClass = await wmi.GetClassAsync("Win32_Process");
foreach (var parameter in processClass.Methods["Create"]!.Parameters)
    Console.WriteLine($"{parameter.Name} {parameter.Type} {parameter.Flags}"); // CommandLine String Parameter, In ...
```

Over DCOM the class an instance carries has no methods (the binary encoding leaves them out); `GetClassAsync` has them.
A query that returns classes (`SELECT * FROM meta_class`) gives one instance per class, with no path and every value
null; over WinRM its `Class` has only the property names, and `EnumerateClassesAsync` gives full definitions.

### Calling methods

```csharp
var startup = new WmiInstance(await wmi.GetClassAsync("Win32_ProcessStartup")); // an embedded-object parameter
startup["ShowWindow"] = (ushort)0;
var result = await wmi.InvokeMethodAsync("Win32_Process", "Create", new Dictionary<string, object?>
{
    ["CommandLine"] = "notepad.exe",
    ["ProcessStartupInformation"] = startup,
});

Console.WriteLine($"Return value {result?["ReturnValue"]}, process {result?["ProcessId"]}");
```

### Writing

```csharp
var created = new WmiInstance(await wmi.GetClassAsync("Win32_Environment"));
created["Name"] = "MY_VAR";
created["UserName"] = "<SYSTEM>";
created["VariableValue"] = "first value";
await wmi.PutInstanceAsync(created, WbemChangeFlags.CreateOnly);

var instance = await wmi.GetInstanceAsync("Win32_Environment.Name=\"MY_VAR\",UserName=\"<SYSTEM>\"");
instance["VariableValue"] = "new value";
await wmi.PutInstanceAsync(instance, WbemChangeFlags.UpdateOnly);

await wmi.DeleteInstanceAsync(instance.SystemProperties.RelativePath!);
```

### Events

```csharp
var query = "SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_Process'";
await foreach (var e in wmi.WatchAsync(query, cancellationToken: cancellationToken))
    Console.WriteLine(((WmiInstance)e["TargetInstance"]!)["Name"]);
```

### Refreshers

```csharp
await using var refresher = await wmi.CreateRefresherAsync();
var memory = await refresher.AddObjectAsync("Win32_PerfRawData_PerfOS_Memory=@");
for (var i = 0; i < 10; i++)
{
    await refresher.RefreshAsync();
    Console.WriteLine(memory.Instance!["AvailableBytes"]);
    await Task.Delay(1000);
}
```

### Call context

A `WmiCallContext` is a set of named values sent with every call, which providers can read. The usual use is the
32-bit or 64-bit registry view of `StdRegProv`:

```csharp
wmi.Context = WmiCallContext.Empty.With("__ProviderArchitecture", 64).With("__RequiredArchitecture", true);
```

### Logging

Pass an `ILoggerFactory` in `WmiConnectionOptions.LoggerFactory`. The library logs connection, authentication and
query steps at Debug level.

## Command-line tool

```
nwmi -h HOST -u [DOMAIN\]USER -p PASSWORD [-n NAMESPACE] [-o text|json|jsonl] "WQL QUERY"
```

| Option | Meaning |
|---|---|
| `-h`, `--host` | the remote host (required) |
| `-u`, `--user` / `-p`, `--password` | the account and its password |
| `-n`, `--namespace` | the namespace to query (default `root\cimv2`) |
| `-o`, `--output` | `text`, `json` (one array) or `jsonl` (one object per line) |
| `-c`, `--context NAME=VALUE` | a call-context value, repeatable; DCOM only |
| `--negotiate` | SPNEGO: offer Kerberos, fall back to NTLM |
| `--ticket-cache FILE` | sign in with the TGT in an MIT-format credential cache (needs `--realm`) |
| `--logon-session` | Windows only: use the current domain logon |
| `--realm`, `--kdc`, `--spn`, `--dns` | override the realm, KDC address, service principal name and DNS server |
| `--winrm`, `--winrm-https` | connect over WinRM (5985) or WinRM over HTTPS (5986) instead of DCOM |
| `--insecure` | with `--winrm-https`: accept any server certificate (self-signed or wrong name) |
| `-v`, `--verbose` | log each step to stderr |
| `--help`, `-?` | show help (`-h` is the host) |

```
dotnet run --project src/NWmi.Cli -- -h server01 -u CORP\admin -p secret -o json "SELECT Name FROM Win32_Process"
```

Timestamps are written in ISO 8601 UTC (`2026-10-29T06:19:44.5000000Z`), intervals as `d.hh:mm:ss.fffffff`, and
references as the path of the instance they refer to.

## Building and testing

```
dotnet build
dotnet test tests/NWmi.Tests
```

The unit tests need no network. The integration tests run against a real Windows host and are skipped unless one
is configured:

```
NWMI_TEST_HOST=<host> NWMI_TEST_USER=<user> NWMI_TEST_PASS=<password> dotnet test tests/NWmi.IntegrationTests
```

Tests use xunit v3 on Microsoft.Testing.Platform, so filter with `--filter-method "*Name*"` rather than `--filter`.

## Limitations

- Calls on one connection are serialized. Use several `WmiConnection`s for parallel work.
- Events are polled; callback delivery is not supported.
- Writing class definitions (`PutClass`) and repository backup and restore are not supported.
- No reconnect, retry or connection pooling.
- RPC over HTTP, UDP and named pipes are not supported.
- Kerberos RC4-HMAC, NTLMv1/LM and anonymous logons are not supported.
- Over WinRM there are no refreshers and no call context.

## Contributing

Issues and pull requests are welcome. Please keep changes focused, add or update unit tests for what you change,
and make sure `dotnet build` is free of warnings and `dotnet test tests/NWmi.Tests` passes before you send them.

## License

[MIT](LICENSE)
