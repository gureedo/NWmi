using NWmi.Rpc;

namespace NWmi.Dcom;

/// <summary>An RPC security provider and service principal name (MS-DCOM 2.2.19.4).</summary>
/// <param name="AuthnService">The authentication service; any other value than the ones named is kept as it came.</param>
/// <param name="PrincipalName">The SPN, or empty when none is specified.</param>
internal sealed record SecurityBinding(RpcAuthType AuthnService, string PrincipalName);
