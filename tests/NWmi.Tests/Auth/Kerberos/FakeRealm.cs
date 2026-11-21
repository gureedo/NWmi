using System.Security.Cryptography;
using Kerberos.NET.Configuration;
using Kerberos.NET.Crypto;
using Kerberos.NET.Entities;
using Kerberos.NET.Server;
using Kerberos.NET.Transport;
using Microsoft.Extensions.Logging;

namespace NWmi.Tests.Auth.Kerberos;

/// <summary>
/// A KDC for tests, from Kerberos.NET's own server side and run in-process: a realm with one user (a password) and
/// one service (a random key the test keeps, so it can play the acceptor). <see cref="Transport"/> hands the client's
/// messages straight to the KDC, with no sockets.
/// </summary>
internal sealed class FakeRealm
{
    public const string RealmName = "EXAMPLE.COM";
    public const string UserName = "alice";
    public const string Password = "P@ssw0rd!";
    public const string ServiceName = "host/server01.example.com";

    private readonly Dictionary<string, FakePrincipal> _principals = new(StringComparer.OrdinalIgnoreCase);
    private readonly KdcServer _kdc;

    public FakeRealm(ILoggerFactory? loggerFactory = null)
    {
        var config = Krb5Config.Parse(string.Join('\n',
            "[libdefaults]", $" default_realm = {RealmName}", "[realms]", $" {RealmName} = {{", "  kdc = fake:88", " }", string.Empty));

        var user = new PrincipalName(PrincipalNameType.NT_PRINCIPAL, RealmName, [UserName]);
        Add(UserName, PrincipalType.User, new KerberosKey(Password, user, etype: EncryptionType.AES256_CTS_HMAC_SHA1_96, saltType: SaltType.ActiveDirectoryUser));
        Add($"krbtgt/{RealmName}", PrincipalType.Service, RandomKey());
        ServiceKey = RandomKey();
        Add(ServiceName, PrincipalType.Service, ServiceKey);

        var realm = new Realm(RealmName, config, _principals);
        _kdc = new KdcServer(new KdcServerOptions
        {
            DefaultRealm = RealmName,
            Configuration = config,
            Log = loggerFactory,
            RealmLocator = _ => realm,
        });
        Transport = new InProcessTransport(_kdc, loggerFactory, config);
        Config = config;
    }

    /// <summary>The service's long-term key: what an acceptor decrypts the service ticket with.</summary>
    public KerberosKey ServiceKey { get; }

    public KerberosTransportBase Transport { get; }

    public Krb5Config Config { get; }

    /// <summary>Requests the KDC saw, by name of the message type.</summary>
    public List<string> Seen { get; } = [];

    private static KerberosKey RandomKey() => new(new KrbEncryptionKey
    {
        EType = EncryptionType.AES256_CTS_HMAC_SHA1_96,
        KeyValue = RandomNumberGenerator.GetBytes(32),
    });

    private void Add(string name, PrincipalType type, KerberosKey key) =>
        _principals[name] = new FakePrincipal(name, type, key);

    public static Exception? Failure { get; set; }

    private sealed class InProcessTransport(KdcServer kdc, ILoggerFactory? loggerFactory)
        : KerberosTransportBase(loggerFactory)
    {
        public InProcessTransport(KdcServer kdc, ILoggerFactory? loggerFactory, Krb5Config config)
            : this(kdc, loggerFactory)
        {
            Configuration = config;
            Enabled = true;
            MaximumAttempts = 3;
            ConnectTimeout = SendTimeout = ReceiveTimeout = TimeSpan.FromSeconds(5);
        }

        public override async Task<ReadOnlyMemory<byte>> SendMessage(
            string domain, ReadOnlyMemory<byte> encoded, CancellationToken cancellation = default) =>
            await Process(encoded).ConfigureAwait(false);

        private async Task<ReadOnlyMemory<byte>> Process(ReadOnlyMemory<byte> encoded)
        {
            try
            {
                return await kdc.ProcessMessage(encoded).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Failure = ex;
                throw;
            }
        }
    }

    private sealed class FakePrincipal(string name, PrincipalType type, KerberosKey key) : IKerberosPrincipal
    {
        public string PrincipalName => name;

        public PrincipalType Type => type;

        public DateTimeOffset? Expires => null;

        public SupportedEncryptionTypes SupportedEncryptionTypes =>
            SupportedEncryptionTypes.Aes256CtsHmacSha196 | SupportedEncryptionTypes.Aes128CtsHmacSha196;

        public IEnumerable<PaDataType> SupportedPreAuthenticationTypes => [PaDataType.PA_ENC_TIMESTAMP];

        public KerberosKey RetrieveLongTermCredential() => key;

        public KerberosKey RetrieveLongTermCredential(EncryptionType etype) => key;

        public PrivilegedAttributeCertificate GeneratePac() => new();

        public void Validate(System.Security.Cryptography.X509Certificates.X509Certificate2Collection certificates)
        {
        }
    }

    private sealed class Realm(string name, Krb5Config config, Dictionary<string, FakePrincipal> principals) : IRealmService
    {
        public string Name => name;

        public IRealmSettings Settings { get; } = new DefaultRealmSettings(config.Defaults, config.Realms[name]);

        public IPrincipalService Principals { get; } = new Directory(principals);

        public ITrustedRealmService? TrustedRealms => null;

        public Krb5Config Configuration => config;

        public DateTimeOffset Now() => DateTimeOffset.UtcNow;
    }

    private sealed class Directory(Dictionary<string, FakePrincipal> principals) : IPrincipalService
    {
        public IKerberosPrincipal? Find(KrbPrincipalName principalName, string realm) =>
            principals.GetValueOrDefault(principalName.FullyQualifiedName.Replace($"@{realm}", string.Empty, StringComparison.OrdinalIgnoreCase));

        public Task<IKerberosPrincipal?> FindAsync(KrbPrincipalName principalName, string realm) =>
            Task.FromResult(Find(principalName, realm));

        public System.Security.Cryptography.X509Certificates.X509Certificate2 RetrieveKdcCertificate() =>
            throw new NotSupportedException();

        public IExchangeKey RetrieveKeyCache(KeyAgreementAlgorithm algorithm) => throw new NotSupportedException();

        public IExchangeKey CacheKey(IExchangeKey key) => throw new NotSupportedException();
    }
}
