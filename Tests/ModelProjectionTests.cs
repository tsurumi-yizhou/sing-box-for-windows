using SFW.Models;
using SFW.Services.Core;
using Xunit;

namespace SFW.Tests;

/// <summary>
/// The Tools and dashboard pages render from these projections only, so the
/// SFA/SFM display names, defaults and exit-node candidate rules must not drift.
/// </summary>
public sealed class ModelProjectionTests
{
    [Fact]
    public void DebugReportDefaultsMatchSfaSfm()
    {
        Assert.True(CoreStartOptions.MemoryLimitMegabytes.SequenceEqual(new[] { 50, 100, 200, 300, 500, 750, 1024 }),
            "The OOM memory limit choices must match SFM's picker.");

        var settings = new AppSettings();
        Assert.False(settings.OomKillerEnabled, "The memory limit must be disabled by default.");
        Assert.False(settings.OomKillerKillConnections, "Kill connections must default to off.");
        Assert.Equal(50, settings.OomMemoryLimitMegabytes);
        Assert.False(settings.PowerReportEnabled, "Power reports must be disabled by default.");

        var counts = new CoreReportUnreadCounts(1, 2, 3);
        Assert.True(counts.For(CoreReportKind.Crash) == 1 &&
                    counts.For(CoreReportKind.OutOfMemory) == 2 &&
                    counts.For(CoreReportKind.Power) == 3,
            "Unread counts must map to the matching report store.");
        Assert.Equal(6, counts.Total);

        var reference = new CoreReportReference(CoreReportKind.Power, "report-name", DateTimeOffset.UnixEpoch);
        Assert.True(reference.Kind == CoreReportKind.Power && reference.Name == "report-name",
            "Report navigation references must preserve kind and name.");
    }

    [Fact]
    public void TailscaleProjectionsMatchTheToolsPageRules()
    {
        static CoreTailscalePeer Peer(
            string hostName, string dnsName = "", bool exitNodeOption = false, string stableId = "") =>
            new(hostName, dnsName, string.Empty, Array.Empty<string>(),
                Online: false, ExitNode: false, ExitNodeOption: exitNodeOption, Active: false,
                RxBytes: 0, TxBytes: 0, KeyExpiry: 0, StableId: stableId, Expired: false,
                SshHostKeys: Array.Empty<string>(), ShareeNode: false, LastSeen: 0, CanReceiveFiles: false);

        static CoreTailscaleEndpoint Endpoint(
            CoreTailscalePeer? self, CoreTailscalePeer? exitNode,
            IReadOnlyList<CoreTailscaleUserGroup> groups, string state = "Running") =>
            new("ts", state, state, string.Empty, "example.ts.net", "ts.net",
                self, groups, exitNode, KeyAuth: false, CanShareFiles: false,
                WaitingFileCount: 0, ReceivingFileCount: 0, UnreadFileCount: 0);

        Assert.Equal("mac", Peer("host", "mac.tailnet.ts.net").DisplayName);
        Assert.Equal("only-host", Peer("only-host").DisplayName);

        var self = Peer("self", "self.tailnet.ts.net", exitNodeOption: true, stableId: "self-id");
        var selfOnly = new[] { new CoreTailscaleUserGroup(1, "me", "Me", string.Empty, new[] { self }) };
        Assert.False(Endpoint(self, null, selfOnly).HasExitNodeCandidates,
            "An endpoint must not offer its own node as an exit node.");

        var other = Peer("other", "other.tailnet.ts.net", exitNodeOption: true, stableId: "other-id");
        var withCandidate = new[]
        {
            new CoreTailscaleUserGroup(1, "me", "Me", string.Empty, new[] { self }),
            new CoreTailscaleUserGroup(2, "friend", "Friend", string.Empty, new[] { other }),
        };
        Assert.True(Endpoint(self, null, withCandidate).HasExitNodeCandidates,
            "Exit-node candidates from other users must be selectable.");
        Assert.True(Endpoint(self, other, selfOnly).HasExitNodeCandidates,
            "A selected exit node must keep the picker available.");

        Assert.True(Endpoint(self, null, selfOnly).IsRunning);
        Assert.False(Endpoint(self, null, selfOnly, "NeedsLogin").IsRunning,
            "Any other backend state must not report IsRunning.");
    }
}
