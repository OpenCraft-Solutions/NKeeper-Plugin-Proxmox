using System.Text;
using NKeeper.Abstractions;
using NKeeper.Plugins.Virtualization;

namespace NKeeper.Plugins.Proxmox.Tests;

/// <summary>The Proxmox provider (docs/architecture.md §7.38) on a stand-in for core's SSH connection, with Proxmox's output from 1.x's tests.</summary>
public sealed class ProxmoxTests
{
    private const string Resources = """[{"type":"qemu","vmid":101,"name":"web01","node":"pve1","status":"running"},{"type":"qemu","vmid":102,"name":"db01","node":"pve2","status":"stopped"},{"type":"qemu","vmid":900,"name":"tmpl","node":"pve1","status":"stopped","template":1},{"type":"lxc","vmid":200,"name":"ct","node":"pve1","status":"running"},{"type":"qemu","vmid":103,"node":"pve1","status":"running"},{"type":"qemu","vmid":104,"name":"bad","node":"pve 1; rm","status":"running"}]""";

    private const string Listing = "proxmox\tpve1\n" + Resources + "\n"
        + "/etc/pve/nodes/pve1/qemu-server/101.conf:vga: std\n"
        + "/etc/pve/nodes/pve1/qemu-server/101.conf:vga: none\n"
        + "/etc/pve/nodes/pve1/qemu-server/103.conf:vga: serial0\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_scan_lists_the_clusters_vms_and_containers_with_their_node_resources_macs_and_static_addresses()
    {
        const string output = "proxmox\tpve1\n"
            + """[{"type":"qemu","vmid":101,"name":"web01","node":"pve1","status":"running","maxcpu":4,"maxmem":8589934592},{"type":"qemu","vmid":102,"name":"db01","node":"pve2","status":"stopped","maxcpu":2,"maxmem":4294967296},{"type":"qemu","vmid":900,"name":"tmpl","node":"pve1","status":"stopped","template":1},{"type":"lxc","vmid":200,"name":"ct","node":"pve1","status":"running","maxcpu":1,"maxmem":536870912}]"""
            + "\n/etc/pve/nodes/pve1/qemu-server/101.conf:net0: virtio=BC:24:11:AA:BB:01,bridge=vmbr0\n"
            + "/etc/pve/nodes/pve1/qemu-server/101.conf:net0: virtio=bc:24:11:aa:bb:01,bridge=vmbr0\n"
            + "/etc/pve/nodes/pve2/qemu-server/102.conf:net1: e1000=BC:24:11:AA:BB:02,bridge=vmbr1,firewall=1\n"
            + "/etc/pve/nodes/pve1/lxc/200.conf:net0: name=eth0,bridge=vmbr0,hwaddr=BC:24:11:AA:BB:03,ip=192.0.2.50/24,type=veth\n";

        var guests = ProxmoxScanner.Parse(output)!;

        Assert.Equal(["ct", "db01", "web01"], guests.Select(g => g.Name));
        var web = guests.Single(g => g.Name == "web01");
        Assert.Equal((null, 4, 8192), (web.Node, web.CpuCores, web.MemoryMiB));
        Assert.Equal(["BC:24:11:AA:BB:01"], web.MacAddresses);
        // Another node's guest names its node; this node's, none.
        Assert.Equal("pve2", guests.Single(g => g.Name == "db01").Node);
        var ct = guests.Single(g => g.Name == "ct");
        Assert.Equal(["192.0.2.50"], ct.IpAddresses);
        Assert.Equal(["BC:24:11:AA:BB:03"], ct.MacAddresses);
        Assert.Null(ProxmoxScanner.Parse("not proxmox"));
    }

    [Fact]
    public async Task The_clusters_vms_are_listed_by_name_without_templates_containers_or_bad_node_names()
    {
        var ssh = new FakeSsh { Output = Listing };
        await using var connection = new ProxmoxConnection(ssh);

        var guests = await connection.ListAsync(Ct);

        Assert.Equal(["db01", "VM 103", "web01"], guests.Select(g => g.Name));
        Assert.Equal(("102", "stopped"), (guests[0].Id, guests[0].State));
        Assert.Equal(ConsoleProtocols.Vnc, guests[0].Console); // no vga line: Proxmox's default display
        Assert.Equal([GuestActions.Start], guests[0].Actions);
        Assert.Null(guests[1].Console); // a serial terminal
        Assert.Equal(ConsoleProtocols.Vnc, guests[2].Console); // the current configuration's std, not the snapshot's none
        Assert.Contains(GuestActions.Resume, guests[2].Actions);
        Assert.Equal(ProxmoxConnection.ListScript, Assert.Single(ssh.Commands));
    }

    [Theory]
    [InlineData("start", "start")]
    [InlineData("shutdown", "shutdown")]
    [InlineData("reboot", "reboot")]
    [InlineData("pause", "suspend")]
    [InlineData("resume", "resume")]
    [InlineData("hibernate", "suspend --todisk 1")]
    [InlineData("stop", "stop")]
    [InlineData("reset", "reset")]
    public async Task Each_power_action_is_its_pvesh_status_call_on_the_vms_own_node(string action, string call)
    {
        var ssh = new FakeSsh { Output = Listing };
        await using var connection = new ProxmoxConnection(ssh);

        await connection.PowerAsync("102", action, Ct);

        Assert.EndsWith($"$p pvesh create /nodes/pve2/qemu/102/status/{call} || exit 70", ssh.Commands[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Refusals_are_explained_and_an_unknown_vm_never_reaches_a_command_line()
    {
        var ssh = new FakeSsh { Output = Listing };
        await using var connection = new ProxmoxConnection(ssh);
        Assert.Contains("isn't in this cluster", (await Assert.ThrowsAsync<GuestException>(() => connection.PowerAsync("1; reboot", "start", Ct))).Message, StringComparison.Ordinal);
        Assert.Single(ssh.Commands);

        ssh.Result = new SshCommandResult(70, string.Empty, "sudo: a password is required\n");
        Assert.Contains("needs root", (await Assert.ThrowsAsync<GuestException>(() => connection.ListAsync(Ct))).Message, StringComparison.Ordinal);
        ssh.Result = new SshCommandResult(71, string.Empty, string.Empty);
        Assert.Contains("isn't a Proxmox VE node", (await Assert.ThrowsAsync<GuestException>(() => connection.ListAsync(Ct))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_console_on_this_node_is_qm_vncproxy_given_its_one_time_password_first()
    {
        var ssh = new FakeSsh { Output = Listing };
        await using var connection = new ProxmoxConnection(ssh);

        await using var console = await connection.OpenConsoleAsync("101", Ct);

        Assert.Equal(ConsoleProtocols.Vnc, console.Protocol);
        Assert.Matches("^[A-Za-z0-9]{8}$", console.Password!);
        var started = Assert.Single(ssh.Started);
        Assert.EndsWith("exec $p sh -c 'read -r t && LC_PVE_TICKET=$t exec qm vncproxy 101'", started.Command, StringComparison.Ordinal);
        Assert.Equal(console.Password + "\n", Encoding.ASCII.GetString(started.Written.ToArray()));

        // Both ways through the one stream: the display's bytes in, the browser's out.
        var greeting = new byte[12];
        await console.Stream.ReadExactlyAsync(greeting, Ct);
        Assert.Equal("RFB 003.008\n", Encoding.ASCII.GetString(greeting));
        await console.Stream.WriteAsync("RFB 003.008\n"u8.ToArray(), Ct);
        Assert.EndsWith("RFB 003.008\n", Encoding.ASCII.GetString(started.Written.ToArray()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_console_on_another_node_goes_over_the_clusters_own_ssh_to_that_nodes_address()
    {
        var ssh = new FakeSsh { Output = Listing.Replace("\"stopped\"}", "\"running\"}", StringComparison.Ordinal) };
        ssh.Outputs["pvesh get /cluster/status"] = """[{"type":"cluster","name":"lab"},{"type":"node","name":"pve2","ip":"10.0.0.12","online":1}]""";
        await using var connection = new ProxmoxConnection(ssh);

        await using var console = await connection.OpenConsoleAsync("102", Ct);

        var command = Assert.Single(ssh.Started).Command;
        Assert.Contains("k=/etc/pve/nodes/pve2/ssh_known_hosts", command, StringComparison.Ordinal);
        Assert.Contains("-o BatchMode=yes -o HostKeyAlias=pve2", command, StringComparison.Ordinal);
        Assert.EndsWith("root@10.0.0.12 'read -r t && LC_PVE_TICKET=$t exec qm vncproxy 102'", command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_console_needs_a_running_vm_with_a_display_and_a_node_that_can_be_reached()
    {
        var ssh = new FakeSsh { Output = Listing };
        await using var connection = new ProxmoxConnection(ssh);
        Assert.Contains("no graphical display", (await Assert.ThrowsAsync<GuestException>(() => connection.OpenConsoleAsync("103", Ct))).Message, StringComparison.Ordinal);

        ssh.Output = Listing.Replace("\"stopped\"}", "\"running\"}", StringComparison.Ordinal);
        ssh.Outputs["pvesh get /cluster/status"] = """[{"type":"node","name":"pve2","ip":"10.0.0.12","online":0}]""";
        Assert.Contains("is offline", (await Assert.ThrowsAsync<GuestException>(() => connection.OpenConsoleAsync("102", Ct))).Message, StringComparison.Ordinal);
        Assert.Empty(ssh.Started);
    }

    [Theory]
    [InlineData("std", true)]
    [InlineData("type=virtio,memory=32", true)]
    [InlineData(null, true)]
    [InlineData("none", false)]
    [InlineData("serial0", false)]
    [InlineData("type=serial1", false)]
    public void A_vm_has_a_vnc_display_unless_it_has_none_or_a_serial_terminal(string? vga, bool vnc) =>
        Assert.Equal(vnc, ProxmoxConnection.HasVncDisplay(vga));
}

/// <summary>Core's SSH connection, as the provider sees it: commands answered from <see cref="Output"/>, started ones recorded.</summary>
internal sealed class FakeSsh : ISshConnection
{
    public string Output { get; set; } = string.Empty;

    /// <summary>Output for commands that contain a key, before <see cref="Output"/>.</summary>
    public Dictionary<string, string> Outputs { get; } = [];

    public SshCommandResult? Result { get; set; }

    public List<string> Commands { get; } = [];

    public List<FakeCommand> Started { get; } = [];

    public Task<SshCommandResult> RunAsync(string command, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Commands.Add(command);
        var output = Outputs.FirstOrDefault(o => command.Contains(o.Key, StringComparison.Ordinal)).Value ?? Output;
        return Task.FromResult(Result ?? new SshCommandResult(0, output, string.Empty));
    }

    public Task<ISshCommand> StartAsync(string command, CancellationToken cancellationToken = default)
    {
        var started = new FakeCommand(command);
        Started.Add(started);
        return Task.FromResult<ISshCommand>(started);
    }

    public Task<ISshShell> OpenShellAsync(SshTerminal terminal, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary><c>qm vncproxy</c>, as the provider sees it: it greets as a VNC server, and keeps what's written to it.</summary>
internal sealed class FakeCommand(string command) : ISshCommand
{
    public string Command { get; } = command;

    public MemoryStream Written { get; } = new();

    public Stream Output { get; } = new MemoryStream(Encoding.ASCII.GetBytes("RFB 003.008\n"));

    public Stream Input => Written;

    public Task<SshCommandResult> WaitAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SshCommandResult(0, string.Empty, string.Empty));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
