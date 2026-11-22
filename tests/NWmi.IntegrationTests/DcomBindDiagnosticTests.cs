using NWmi.Auth;
using NWmi.Dcom;
using NWmi.Options;
using NWmi.Rpc;

namespace NWmi.IntegrationTests;

/// <summary>
/// Regression coverage, against a real Windows endpoint mapper, for two findings that unit tests (which only
/// exercise a fake transport) could not have caught:
///   1. A combined bind offering IObjectExporter and IRemoteSCMActivator in one PDU gets the second context
///      rejected (ProviderRejection/ProposedTransferSyntaxesNotSupported) regardless of which interface is
///      listed second. <see cref="DcomClient"/> now binds one interface and alter_contexts the other.
///   2. A real Windows server returns RPC_S_SEC_PKG_ERROR for a signed/sealed call unless the client offers
///      RPC header signing (PFC_SUPPORT_HEADER_SIGN) on the authenticated bind, so the client always offers it.
/// </summary>
public class DcomBindDiagnosticTests
{
    private static Credential Credential => Credential.Parse(IntegrationSettings.User!, IntegrationSettings.Password!);

    [IntegrationFact]
    public async Task Combined_bind_of_both_activation_interfaces_is_rejected_by_a_real_server()
    {
        await using var rpc = await RpcClient.ConnectTcpAsync(IntegrationSettings.Host!, DcomConstants.EndpointMapperPort);
        var ex = await Assert.ThrowsAsync<RpcBindRejectedException>(() => rpc.BindAsync(
        [
            RpcSyntaxId.Parse("99fcfec4-5260-101b-bbcb-00aa0021347a"),
            new RpcSyntaxId(Guid.Parse("000001a0-0000-0000-c000-000000000046"), 0, 0),
        ]));
        Assert.Contains("ProposedTransferSyntaxesNotSupported", ex.Message);
    }

    [IntegrationFact]
    public async Task DcomClient_binds_the_two_activation_interfaces_separately()
    {
        var security = new RpcSecurity(new NtlmSecurityProvider(Credential));
        await using var dcom = await DcomClient.ConnectAsync(IntegrationSettings.Host!, security);
        var alive = await dcom.Exporter.ServerAlive2Async();
        Assert.NotNull(alive);
    }

    [IntegrationFact]
    public async Task Signed_call_succeeds_with_header_signing()
    {
        var security = new RpcSecurity(new NtlmSecurityProvider(Credential), RpcAuthLevel.PacketIntegrity);
        await using var dcom = await DcomClient.ConnectAsync(IntegrationSettings.Host!, security);
        var alive = await dcom.Exporter.ServerAlive2Async();
        Assert.NotNull(alive);
    }
}
