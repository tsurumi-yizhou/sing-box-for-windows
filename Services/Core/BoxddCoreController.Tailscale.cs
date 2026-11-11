using System.Runtime.CompilerServices;
using Daemon;
using Google.Protobuf.Collections;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace SFW.Services.Core;

/// <summary>
/// Tailscale surface: the daemon owns the engine, so every member is a thin
/// StartedService RPC pass-through that only maps daemon protos onto the app's
/// immutable records (libbox parity, no app-side Tailscale state).
/// </summary>
public sealed partial class BoxddCoreController
{
    /// <summary>
    /// The daemon localizes user-visible text per call: it reads the gRPC
    /// "accept-language" metadata and matches it against its built-in locales
    /// (zh-Hans/zh-Hant/...). There is no connection-level default, so each call
    /// has to declare the app's UI language or StateText comes back untranslated.
    /// </summary>
    private static Metadata CreateLocaleHeaders() =>
        new() { { "accept-language", CurrentDaemonLocale() } };

    /// <summary>
    /// The daemon's locale matcher distinguishes Simplified from Traditional
    /// Chinese by region (see upstream experimental/locale): zh-TW/HK/MO resolve
    /// to zh-Hant, any other Chinese region to zh-Hans.
    /// </summary>
    internal static string CurrentDaemonLocale()
    {
        // The app's UI language, i.e. the language the resource system resolved.
        // CultureInfo is the unrelated regional-format setting, so reading it
        // here would let the daemon answer in a different language than the UI.
        var language = Loc.UiLanguage;
        if (!language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return "en-US";
        if (language.Contains("Hant", StringComparison.OrdinalIgnoreCase)) return "zh-Hant";
        var region = language.Split('-').LastOrDefault() ?? string.Empty;
        return region is "TW" or "HK" or "MO" ? "zh-Hant" : "zh-Hans";
    }

    public async IAsyncEnumerable<IReadOnlyList<CoreTailscaleEndpoint>> SubscribeTailscaleStatusAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        using var call = client.SubscribeTailscaleStatus(
            new Empty(),
            new CallOptions(headers: CreateLocaleHeaders(), cancellationToken: cancellationToken));
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var update = call.ResponseStream.Current;
            // Each update is the full endpoint set, not a delta: an empty list
            // simply means the running profile has no tailscale endpoint.
            var endpoints = new List<CoreTailscaleEndpoint>(update.Endpoints.Count);
            foreach (var endpoint in update.Endpoints)
                endpoints.Add(MapTailscaleEndpoint(endpoint));
            yield return endpoints;
        }
    }

    public async IAsyncEnumerable<CoreTailscalePingResult> StartTailscalePingAsync(
        string endpointTag,
        string peerIp,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        using var call = client.StartTailscalePing(
            new TailscalePingRequest { EndpointTag = endpointTag, PeerIP = peerIp },
            new CallOptions(headers: CreateLocaleHeaders(), cancellationToken: cancellationToken));
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            var result = call.ResponseStream.Current;
            // The daemon reports ping failures in-band instead of failing the
            // stream, so Error comes with the (zeroed) latency sample.
            yield return new CoreTailscalePingResult(
                result.LatencyMs, result.IsDirect, result.Endpoint, result.DerpRegionID,
                result.DerpRegionCode, result.PeerRelay,
                string.IsNullOrEmpty(result.Error) ? null : result.Error);
        }
    }

    public async Task SetTailscaleExitNodeAsync(
        string endpointTag, string stableId, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        // The daemon treats an empty stableID as "clear the exit node", which is
        // exactly the "use direct connection" option in the picker.
        await client.SetTailscaleExitNodeAsync(
            new SetTailscaleExitNodeRequest { EndpointTag = endpointTag, StableID = stableId ?? string.Empty },
            cancellationToken: cancellationToken);
    }

    public async Task TailscaleLogoutAsync(string endpointTag, CancellationToken cancellationToken = default)
    {
        var client = GetClient();
        await client.TailscaleLogoutAsync(
            new TailscaleLogoutRequest { EndpointTag = endpointTag },
            cancellationToken: cancellationToken);
    }

    private static CoreTailscaleEndpoint MapTailscaleEndpoint(TailscaleEndpointStatus endpoint) =>
        new(
            endpoint.EndpointTag,
            endpoint.BackendState,
            endpoint.StateText,
            endpoint.AuthURL,
            endpoint.NetworkName,
            endpoint.MagicDNSSuffix,
            endpoint.Self is null ? null : MapTailscalePeer(endpoint.Self),
            MapTailscaleUserGroups(endpoint.UserGroups),
            endpoint.ExitNode is null ? null : MapTailscalePeer(endpoint.ExitNode),
            endpoint.KeyAuth,
            endpoint.CanShareFiles,
            endpoint.WaitingFileCount,
            endpoint.ReceivingFileCount,
            endpoint.UnreadFileCount);

    private static IReadOnlyList<CoreTailscaleUserGroup> MapTailscaleUserGroups(
        RepeatedField<TailscaleUserGroup> groups)
    {
        var result = new List<CoreTailscaleUserGroup>(groups.Count);
        foreach (var group in groups)
        {
            var peers = new List<CoreTailscalePeer>(group.Peers.Count);
            foreach (var peer in group.Peers)
                peers.Add(MapTailscalePeer(peer));
            result.Add(new CoreTailscaleUserGroup(
                group.UserID, group.LoginName, group.DisplayName, group.ProfilePicURL, peers));
        }
        return result;
    }

    private static CoreTailscalePeer MapTailscalePeer(TailscalePeer peer) =>
        new(
            peer.HostName,
            peer.DnsName,
            peer.Os,
            peer.TailscaleIPs.ToList(),
            peer.Online,
            peer.ExitNode,
            peer.ExitNodeOption,
            peer.Active,
            peer.RxBytes,
            peer.TxBytes,
            peer.KeyExpiry,
            peer.StableID,
            peer.Expired,
            peer.SshHostKeys.ToList(),
            peer.ShareeNode,
            peer.LastSeen,
            peer.CanReceiveFiles);
}
