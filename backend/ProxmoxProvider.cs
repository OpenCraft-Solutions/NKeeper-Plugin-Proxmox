using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NKeeper.Abstractions;
using NKeeper.Plugins.Virtualization;

namespace NKeeper.Plugins.Proxmox;

/// <summary>
/// Proxmox VE nodes for virt-core (docs/architecture.md §7.38), ported from 1.x's <c>ProxmoxHost</c>: over SSH, which
/// core opens and signs in to (§7.9), running <c>pvesh</c> and <c>qm</c> as root.
/// </summary>
public sealed class ProxmoxProvider(IConnectivity connectivity) : IGuestProvider
{
    public const string PluginId = "proxmox";

    string IGuestProvider.PluginId => PluginId;

    public string Product => "Proxmox VE";

    public async Task<IGuestConnection> OpenAsync(StreamSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handle = request.Credential ?? throw new ConnectException(ConnectFailure.NoSecret, "Choose a credential, or type one, to sign in to the Proxmox node.");
        return new ProxmoxConnection(await connectivity.OpenSshAsync(request.Host, request.ServiceId, handle, cancellationToken));
    }
}

/// <summary>
/// One SSH connection to a Proxmox VE node: its cluster's virtual machines, their power through <c>pvesh</c> (which
/// reaches every node), and their consoles through <c>qm vncproxy</c> (SDK 1.12).
/// </summary>
public sealed partial class ProxmoxConnection(ISshConnection ssh) : IGuestConnection
{
    /// <summary>The exit status of our scripts when <c>pvesh</c> or <c>qm</c> refuse.</summary>
    internal const int RefusedExitStatus = 70;

    /// <summary>Sets <c>$p</c> to the prefix that runs a command as root.</summary>
    internal const string AsRoot = "p=''; [ \"$(id -u)\" = 0 ] || p='sudo -n'; ";

    /// <summary>
    /// Prints a "proxmox" line with this node's name, the cluster's VMs as JSON, then each VM configuration's
    /// <c>vga:</c> lines (the current configuration's come first).
    /// </summary>
    internal const string ListScript =
        AsRoot
        + "[ -d /etc/pve/nodes ] || exit 71; "
        + "printf 'proxmox\\t%s\\n' \"$(uname -n | cut -d. -f1)\"; "
        + "$p pvesh get /cluster/resources --type vm --output-format json || exit 70; echo; "
        + "$p grep -H '^vga:' /etc/pve/nodes/*/qemu-server/*.conf 2>/dev/null; exit 0";

    /// <summary>
    /// Run on the VM's node with the VNC password as the first line of input: <c>qm vncproxy</c> sets it on the VM's
    /// display (valid for 30 seconds) and relays the display on its standard input and output.
    /// </summary>
    internal const string VncProxy = "read -r t && LC_PVE_TICKET=$t exec qm vncproxy ";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<Guest>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await ListingAsync(cancellationToken)).Machines.Select(m => m.Guest)];

    public async Task PowerAsync(string guestId, string action, CancellationToken cancellationToken)
    {
        var machine = await FindAsync(guestId, cancellationToken);
        // The node and VM ID come from pvesh and are checked, and the path is ours: all safe in the command line.
        await RunAsync($"{AsRoot}$p pvesh create /nodes/{machine.Node}/qemu/{machine.VmId}/status/{Command(action)} || exit 70", TimeSpan.FromMinutes(10), cancellationToken);
    }

    public async Task<GuestConsole> OpenConsoleAsync(string guestId, CancellationToken cancellationToken)
    {
        var listing = await ListingAsync(cancellationToken);
        var machine = listing.Machines.FirstOrDefault(m => m.Guest.Id == guestId) ?? throw new GuestException("That virtual machine isn't in this cluster any more.");
        if (machine.Guest.Console is null)
        {
            throw new GuestException("This virtual machine has no graphical display (its display is none, or a serial terminal).");
        }

        if (machine.Guest.State != "running")
        {
            throw new GuestException("This virtual machine isn't running.");
        }

        var command = string.Equals(machine.Node, listing.LocalNode, StringComparison.OrdinalIgnoreCase)
            ? $"{AsRoot}exec $p sh -c '{VncProxy}{machine.VmId}'"
            : await NodeSshCommandAsync(machine.Node, $"{VncProxy}{machine.VmId}", "where this VM runs", cancellationToken);
        var ticket = NewTicket();
        var proxy = await ssh.StartAsync(command, cancellationToken);
        try
        {
            await proxy.Input.WriteAsync(Encoding.ASCII.GetBytes(ticket + "\n"), cancellationToken);
            await proxy.Input.FlushAsync(cancellationToken);
        }
        catch
        {
            await proxy.DisposeAsync();
            throw;
        }

        return new GuestConsole(ConsoleProtocols.Vnc, ticket, new CommandStream(proxy), () => proxy.DisposeAsync());
    }

    public ValueTask DisposeAsync() => ssh.DisposeAsync();

    /// <summary>A virtual machine as the cluster lists it: virt-core's guest, and the node and ID Proxmox knows it by.</summary>
    internal sealed record Machine(Guest Guest, string Node, int VmId);

    /// <summary>The cluster's virtual machines, and the name of the node this connection is to.</summary>
    internal sealed record Listing(IReadOnlyList<Machine> Machines, string LocalNode);

    /// <summary>The listing, from <see cref="ListScript"/>'s output.</summary>
    internal static Listing ParseListing(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !lines[0].StartsWith("proxmox\t", StringComparison.Ordinal))
        {
            throw new GuestException("Couldn't read the Proxmox cluster's list of virtual machines.");
        }

        var local = lines[0]["proxmox\t".Length..];
        List<Resource>? resources;
        try
        {
            resources = JsonSerializer.Deserialize<List<Resource>>(lines[1], Json);
        }
        catch (JsonException)
        {
            throw new GuestException("Couldn't read the Proxmox cluster's list of virtual machines.");
        }

        // The first vga line of each configuration is its current one (snapshots follow).
        var vga = new Dictionary<int, string>();
        foreach (var match in lines.Skip(2).Select(l => VgaLine().Match(l)).Where(m => m.Success))
        {
            vga.TryAdd(int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture), match.Groups["vga"].Value);
        }

        var machines = (resources ?? [])
            .Where(r => r.Type == "qemu" && r.Template is not 1 && r.Vmid > 0 && r.Node is { } node && NodeName().IsMatch(node))
            .Select(r =>
            {
                var id = r.Vmid.ToString(CultureInfo.InvariantCulture);
                var state = r.Status ?? "unknown";
                var guest = new Guest(id, r.Name is { Length: > 0 } name ? name : $"VM {id}", state, HasVncDisplay(vga.GetValueOrDefault(r.Vmid)) ? ConsoleProtocols.Vnc : null, Actions(state));
                return new Machine(guest, r.Node!, r.Vmid);
            })
            .OrderBy(m => m.Guest.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new Listing(machines, local);
    }

    /// <summary>A VM shows its display over VNC unless it has none or uses a serial terminal.</summary>
    /// <param name="vga">The configuration's <c>vga</c> value, e.g. <c>std</c>, <c>type=virtio,memory=32</c>, <c>serial0</c> or <c>none</c>.</param>
    internal static bool HasVncDisplay(string? vga)
    {
        var type = vga?.Split(',')[0].Trim() ?? string.Empty;
        type = type.StartsWith("type=", StringComparison.Ordinal) ? type["type=".Length..] : type;
        return !(type == "none" || type.StartsWith("serial", StringComparison.Ordinal));
    }

    /// <summary>
    /// The power actions a VM takes in its state. The cluster's listing doesn't tell paused from running, so a running
    /// VM offers both pause and resume.
    /// </summary>
    internal static IReadOnlyList<string> Actions(string state) => state switch
    {
        "running" => [GuestActions.Shutdown, GuestActions.Reboot, GuestActions.Pause, GuestActions.Resume, GuestActions.Hibernate, GuestActions.Stop, GuestActions.Reset],
        "stopped" => [GuestActions.Start],
        _ => [GuestActions.Stop],
    };

    /// <summary>
    /// The <c>pvesh</c> status call for an action. Hibernating suspends to disk; starting a hibernated VM resumes it.
    /// Stopping is off at once, as pulling the plug is; nothing is deleted.
    /// </summary>
    internal static string Command(string action) => action switch
    {
        GuestActions.Start or GuestActions.Shutdown or GuestActions.Reboot or GuestActions.Resume or GuestActions.Stop or GuestActions.Reset => action,
        GuestActions.Pause => "suspend",
        GuestActions.Hibernate => "suspend --todisk 1",
        _ => throw new GuestException($"“{action}” isn't a power action Proxmox has."),
    };

    /// <summary>A one-time VNC password. VNC uses only the first 8 characters of a password.</summary>
    internal static string NewTicket() => RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789", 8);

    /// <summary>
    /// The command that runs <paramref name="remote"/> (a fixed command of ours, in single quotes) on another node of the
    /// cluster, over SSH as root from this node with the cluster's own host keys, as Proxmox does. A new key is never
    /// accepted (<c>BatchMode</c>, and only the cluster's known hosts).
    /// </summary>
    /// <param name="why">Why the node is needed, for errors, e.g. "where this VM runs".</param>
    internal async Task<string> NodeSshCommandAsync(string node, string remote, string why, CancellationToken cancellationToken)
    {
        if (!NodeName().IsMatch(node))
        {
            throw new GuestException($"“{node}” isn't a Proxmox node name.");
        }

        var output = await RunAsync(AsRoot + "$p pvesh get /cluster/status --output-format json || exit 70", CommandTimeout, cancellationToken);
        List<ClusterMember>? members;
        try
        {
            members = JsonSerializer.Deserialize<List<ClusterMember>>(output.Trim(), Json);
        }
        catch (JsonException)
        {
            members = null;
        }

        var member = members?.FirstOrDefault(m => m.Type == "node" && string.Equals(m.Name, node, StringComparison.OrdinalIgnoreCase));
        if (member?.Ip is not { } ip || !IPAddress.TryParse(ip, out var address))
        {
            throw new GuestException($"Couldn't find the address of node “{node}”, {why}. Add that node to NKeeper and use it directly.");
        }

        if (member.Online is 0)
        {
            throw new GuestException($"Node “{node}”, {why}, is offline.");
        }

        // Newer Proxmox versions keep each node's host keys in its own file; older ones in the cluster-wide known_hosts.
        return AsRoot
            + $"k=/etc/pve/nodes/{node}/ssh_known_hosts; o=''; [ -f \"$k\" ] && o=\"-o UserKnownHostsFile=$k -o GlobalKnownHostsFile=none\"; "
            + $"exec $p ssh -e none -T -o BatchMode=yes -o HostKeyAlias={node} $o -o ConnectTimeout=10 root@{address} '{remote}'";
    }

    private async Task<Listing> ListingAsync(CancellationToken cancellationToken) =>
        ParseListing(await RunAsync(ListScript, CommandTimeout, cancellationToken));

    private async Task<Machine> FindAsync(string guestId, CancellationToken cancellationToken) =>
        (await ListingAsync(cancellationToken)).Machines.FirstOrDefault(m => m.Guest.Id == guestId)
        ?? throw new GuestException("That virtual machine isn't in this cluster any more.");

    /// <summary>Runs a command and returns its output, or explains why Proxmox refused.</summary>
    private async Task<string> RunAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        SshCommandResult result;
        try
        {
            result = await ssh.RunAsync(command, timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new GuestException("The Proxmox node didn't answer in time.");
        }

        if (result.ExitStatus is 71)
        {
            throw new GuestException("This host isn't a Proxmox VE node (it has no /etc/pve).");
        }

        if (result.ExitStatus is not 0)
        {
            var error = result.Error.Trim();
            throw new GuestException(
                $"Proxmox refused the request: {(error.Length > 0 ? error.Split('\n')[0] : $"exit code {result.ExitStatus}")}. "
                + "NKeeper runs pvesh and qm on Proxmox nodes, which needs root: sign in as root, or as a user who may use sudo without a password.");
        }

        return result.Output;
    }

    private sealed record Resource(string? Type, int Vmid, string? Name, string? Node, string? Status, int? Template);

    private sealed record ClusterMember(string? Type, string? Name, string? Ip, int? Online);

    [GeneratedRegex(@"/qemu-server/(?<id>\d+)\.conf:vga:\s*(?<vga>\S*)")]
    private static partial Regex VgaLine();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,62})$")]
    private static partial Regex NodeName();
}

/// <summary>A running command as one stream: reads are its output, writes its input (SDK 1.12).</summary>
internal sealed class CommandStream(ISshCommand command) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => command.Output.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => command.Output.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => command.Output.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count) => command.Input.Write(buffer, offset, count);

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await command.Input.WriteAsync(buffer, cancellationToken);
        await command.Input.FlushAsync(cancellationToken);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await command.Input.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
        await command.Input.FlushAsync(cancellationToken);
    }

    public override void Flush() => command.Input.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => command.Input.FlushAsync(cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();
}
