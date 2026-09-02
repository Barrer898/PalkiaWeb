namespace Palkia.Core.Services;

/// <summary>
/// Describes one named, long-lived OpenRC service the app is allowed to control
/// (as opposed to ephemeral jobs like a render, which never get an init script).
/// The <see cref="RcName"/> must exactly match the init script name under /etc/init.d
/// and must appear in the allow-list enforced by the root-owned control wrapper -
/// this record alone does not grant any privilege.
/// </summary>
public sealed record ManagedService(
    string Id,
    string DisplayName,
    string RcName,
    string Description,
    bool AdminOnly = true);

/// <summary>
/// Central, hardcoded catalog of services the UI is allowed to show/control.
/// Deliberately not driven by free-text config: every entry here is one the
/// operator has explicitly reviewed and added to the wrapper's allow-list too.
/// </summary>
public static class ManagedServiceCatalog
{
    public static readonly IReadOnlyList<ManagedService> All = new[]
    {
        new ManagedService(
            Id: "samba",
            DisplayName: "Samba (SMB file shares)",
            RcName: "samba",
            Description: "Windows/SMB file sharing.",
            AdminOnly: true),

        new ManagedService(
            Id: "nfs",
            DisplayName: "NFS server",
            RcName: "nfs",
            Description: "NFS exports for Linux/Unix clients.",
            AdminOnly: true),

        new ManagedService(
            Id: "nfsclient",
            DisplayName: "NFS client helper",
            RcName: "nfsclient",
            Description: "rpc.statd/idmapd support required by the NFS server.",
            AdminOnly: true),
    };

    public static ManagedService? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
}
