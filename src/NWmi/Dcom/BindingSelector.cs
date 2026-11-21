namespace NWmi.Dcom;

/// <summary>A host and port to connect to, picked out of a <see cref="DualStringArray"/>.</summary>
/// <param name="Host">As given in the binding: a name, FQDN, or IPv4/IPv6 address.</param>
/// <param name="Port">The RPC endpoint (TCP port).</param>
internal sealed record TcpTarget(string Host, int Port);

/// <summary>Picks a usable TCP binding out of the bindings a server returns (MS-DCOM 2.2.19.3).</summary>
internal static class BindingSelector
{
    /// <summary>
    /// The first TCP binding with an endpoint, which is what object exporter bindings carry (as opposed to
    /// object resolver bindings, which omit the endpoint since it is the well-known one, port 135).
    /// </summary>
    public static TcpTarget SelectExporterTarget(DualStringArray bindings) =>
        EnumerateExporterTargets(bindings).FirstOrDefault()
        ?? throw new DcomException("No usable TCP binding (with an endpoint) was offered.");

    /// <summary>
    /// Every TCP binding with an endpoint, in the order the server listed them (its preference order,
    /// MS-DCOM 2.2.19.1). A caller that can retry should try each in turn rather than only the first.
    /// </summary>
    public static IEnumerable<TcpTarget> EnumerateExporterTargets(DualStringArray bindings, string? preferredHost)
    {
        var offered = EnumerateExporterTargets(bindings).ToList();
        if (string.IsNullOrEmpty(preferredHost))
            return offered;

        // The server lists its own names and every local address, some of which the client cannot reach (a
        // hostname it cannot resolve, another adapter's subnet); each unreachable one costs a TCP timeout. The host
        // the client already reached is almost always right, so try it first with each port the server gave.
        var preferred = offered.Select(t => t.Port).Distinct().Select(port => new TcpTarget(preferredHost, port))
            .ToList();
        return [.. preferred, .. offered.Where(t => !preferred.Contains(t))];
    }

    /// <summary>
    /// Where to ask for an OXID's bindings: the TCP bindings with an endpoint if there are any, otherwise the
    /// endpoint-less ones (the form an object reference uses for an object on another apartment, e.g. the refresher
    /// of IWbemRefreshingServices) on the object resolver's well-known port 135. Addresses go before names, since a
    /// name the client cannot resolve only costs time.
    /// </summary>
    public static IEnumerable<TcpTarget> EnumerateResolverTargets(DualStringArray bindings)
    {
        var withEndpoint = EnumerateExporterTargets(bindings).ToList();
        if (withEndpoint.Count > 0)
            return withEndpoint;

        return bindings.StringBindings
            .Where(b => b.TowerId == DualStringArray.TcpTowerId && string.IsNullOrEmpty(b.Endpoint))
            .Select(b => new TcpTarget(b.Host, 135))
            .OrderBy(t => System.Net.IPAddress.TryParse(t.Host, out _) ? 0 : 1);
    }

    /// <inheritdoc cref="EnumerateExporterTargets(DualStringArray, string?)"/>
    public static IEnumerable<TcpTarget> EnumerateExporterTargets(DualStringArray bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        foreach (var binding in bindings.StringBindings)
            if (binding is { TowerId: DualStringArray.TcpTowerId, Endpoint: { } endpoint }
                && int.TryParse(endpoint, out var port))
                yield return new TcpTarget(binding.Host, port);
    }
}
