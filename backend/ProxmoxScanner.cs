using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using NKeeper.Abstractions;

namespace NKeeper.Plugins.Proxmox;

/// <summary>What the shell posts to a scan action (docs/architecture.md §7.26).</summary>
internal sealed record ScanRequest(Guid ServiceId, Guid? CredentialId, string? PromptReference);

/// <summary>
/// 1.x's hypervisor scan for a Proxmox VE cluster (docs/architecture.md §7.38, §7.49), on SDK 1.15: every VM and container
/// in the cluster, with the node it runs on, its vCPUs and memory, its MAC addresses (from its configuration) and a
/// container's static addresses, over one SSH login, which core opens and signs in to. Nothing in the cluster changes.
/// </summary>
internal static partial class ProxmoxScanner
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A "proxmox" line with this node's name, the cluster's VMs and containers as JSON, then their configurations' <c>net</c> lines.</summary>
    internal const string Script =
        ProxmoxConnection.AsRoot
        + "[ -d /etc/pve/nodes ] || exit 71; "
        + "printf 'proxmox\\t%s\\n' \"$(uname -n | cut -d. -f1)\"; "
        + "$p pvesh get /cluster/resources --type vm --output-format json || exit 70; echo; "
        + "$p grep -H '^net[0-9]*:' /etc/pve/nodes/*/qemu-server/*.conf /etc/pve/nodes/*/lxc/*.conf 2>/dev/null; exit 0";

    internal static async Task<IResult> ScanAsync(HostContext host, ScanRequest request, ICredentialBroker broker, IConnectivity connectivity, IPluginAudit audit, CancellationToken cancellationToken)
    {
        if (host.Services.FirstOrDefault(s => s.Id == request.ServiceId) is not { Type: ProxmoxPlugin.ServiceType })
        {
            return Problem(StatusCodes.Status400BadRequest, "bad_request", "This host has no such Proxmox VE service. Reload the host and try again.");
        }

        try
        {
            var handle = request.CredentialId is { } credentialId
                ? await broker.ResolveSavedAsync(host, request.ServiceId, credentialId, cancellationToken)
                : request.PromptReference is { Length: > 0 } reference
                    ? await broker.ResolvePromptAsync(host, request.ServiceId, reference, cancellationToken)
                    : throw new ConnectException(ConnectFailure.NoSecret, "Choose a credential, or type one, to scan the cluster.");
            await using var ssh = await connectivity.OpenSshAsync(host, request.ServiceId, handle, cancellationToken);
            SshCommandResult result;
            try
            {
                result = await ssh.RunAsync(Script, Timeout, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return Problem(StatusCodes.Status504GatewayTimeout, "timeout", "The cluster didn't finish the scan in time.");
            }

            if (result.ExitStatus is not 0 || Parse(result.Output) is not { } guests)
            {
                return Problem(
                    StatusCodes.Status409Conflict,
                    "proxmox_refused",
                    result.ExitStatus == 71
                        ? "This host isn't a Proxmox VE node."
                        : "Proxmox VE refused the scan. The SSH user needs to be root, or able to run pvesh with sudo without a password.");
            }

            await audit.RecordAsync("proxmox.scan", host.Id, $"service {request.ServiceId}: {guests.Count} guests", cancellationToken);
            return Results.Ok(new ScanFindings
            {
                Guests = guests,
                Notes = [$"The cluster runs {guests.Count} {(guests.Count == 1 ? "virtual machine or container" : "virtual machines and containers")}. MAC addresses come from their configurations; only containers' static addresses are known."],
            });
        }
        catch (ConnectException ex)
        {
            return ex.Presented is { } presented
                ? Problem(StatusCodes.Status409Conflict, "host_key_unknown", ex.Message, presented)
                : Problem(ex.Reason == ConnectFailure.NoSecret ? StatusCodes.Status400BadRequest : StatusCodes.Status502BadGateway, Code(ex.Reason), ex.Message);
        }
        catch (CredentialDeniedException ex)
        {
            return Problem(StatusCodes.Status403Forbidden, "credential_denied", ex.Message);
        }
    }

    /// <summary>The script's output as guests, those on this node with no node named; null when it isn't the script's.</summary>
    internal static IReadOnlyList<ScannedGuest>? Parse(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries);
        if (lines.Length < 2 || !lines[0].StartsWith("proxmox\t", StringComparison.Ordinal))
        {
            return null;
        }

        var local = lines[0]["proxmox\t".Length..];
        List<Resource>? resources;
        try
        {
            resources = JsonSerializer.Deserialize<List<Resource>>(lines[1], Json);
        }
        catch (JsonException)
        {
            return null;
        }

        var macs = new Dictionary<int, List<string>>();
        var ips = new Dictionary<int, List<string>>();
        foreach (var match in lines.Skip(2).Select(l => NetLine().Match(l)).Where(m => m.Success))
        {
            var id = int.Parse(match.Groups["id"].Value, CultureInfo.InvariantCulture);
            var value = match.Groups["value"].Value;
            if (Mac().Match(value) is { Success: true } mac)
            {
                var list = macs.TryGetValue(id, out var known) ? known : macs[id] = [];
                var address = mac.Groups["mac"].Value.ToUpperInvariant();
                if (!list.Contains(address))
                {
                    list.Add(address);
                }
            }

            if (StaticIp().Match(value) is { Success: true } ip)
            {
                var list = ips.TryGetValue(id, out var known) ? known : ips[id] = [];
                if (!list.Contains(ip.Groups["ip"].Value))
                {
                    list.Add(ip.Groups["ip"].Value);
                }
            }
        }

        return [.. (resources ?? [])
            .Where(r => r.Type is "qemu" or "lxc" && r.Template is not 1 && r.Vmid > 0 && r.Node is { Length: > 0 })
            .Select(r => new ScannedGuest(
                r.Name is { Length: > 0 } name ? name : $"{(r.Type == "lxc" ? "CT" : "VM")} {r.Vmid}",
                r.Status,
                macs.GetValueOrDefault(r.Vmid) ?? [],
                ips.GetValueOrDefault(r.Vmid) ?? [],
                r.Maxcpu is > 0 and var cpus ? (int)Math.Ceiling(cpus) : null,
                r.Maxmem is >= 1024 * 1024 and var bytes ? (int)Math.Min(bytes / (1024 * 1024), int.MaxValue) : null,
                string.Equals(r.Node, local, StringComparison.OrdinalIgnoreCase) ? null : r.Node))
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static IResult Problem(int status, string code, string detail, PresentedIdentity? presented = null)
    {
        var extensions = new Dictionary<string, object?> { ["code"] = code };
        if (presented is not null)
        {
            extensions["presented"] = presented;
        }

        return Results.Problem(statusCode: status, detail: detail, extensions: extensions);
    }

    private static string Code(ConnectFailure reason) => reason switch
    {
        ConnectFailure.HostKeyChanged => "host_key_changed",
        ConnectFailure.AuthenticationFailed => "authentication_failed",
        ConnectFailure.NoSecret => "no_secret",
        ConnectFailure.InvalidPrivateKey => "invalid_private_key",
        ConnectFailure.TimedOut => "timeout",
        ConnectFailure.JumpHostUnavailable => "jump_host_unavailable",
        _ => "unreachable",
    };

    private sealed record Resource(string? Type, int Vmid, string? Name, string? Node, string? Status, int? Template, double? Maxcpu, long? Maxmem);

    /// <summary>A configuration's network line: <c>…/qemu-server/101.conf:net0: virtio=BC:24:11:…,bridge=vmbr0</c>.</summary>
    [GeneratedRegex(@"/(?:qemu-server|lxc)/(?<id>\d+)\.conf:net\d+:\s*(?<value>.*)$")]
    private static partial Regex NetLine();

    /// <summary>A VM's <c>virtio=MAC</c> (or another model's) or a container's <c>hwaddr=MAC</c>.</summary>
    [GeneratedRegex(@"(?:^|,)[a-z0-9]+=(?<mac>[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5})(?:,|$)")]
    private static partial Regex Mac();

    /// <summary>A container's static IPv4 address: <c>ip=192.0.2.5/24</c>.</summary>
    [GeneratedRegex(@"(?:^|,)ip=(?<ip>\d{1,3}(?:\.\d{1,3}){3})/")]
    private static partial Regex StaticIp();
}
