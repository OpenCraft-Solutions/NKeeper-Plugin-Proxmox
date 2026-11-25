using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NKeeper.Abstractions;
using NKeeper.Plugins.Virtualization;

namespace NKeeper.Plugins.Proxmox;

/// <summary>
/// The proxmox plugin (docs/architecture.md §7.38): the <c>proxmox</c> service type and its guests tab, which virt-core
/// runs and draws (§7.36). Core opens the SSH connection and signs in (§7.9); this plugin never sees the password.
/// </summary>
public sealed class ProxmoxPlugin : IPlugin
{
    public const string ServiceType = "proxmox";

    public const string SessionType = "proxmox";

    public void Contribute(IPluginContributions contributions, PluginContext context)
    {
        contributions.AddServiceType(new ServiceType(ServiceType, "Proxmox VE") { DefaultPort = 22, Health = HealthProbe.Tcp });
        contributions.AddSessionType(new SessionType(SessionType, "Virtual machines", typeof(GuestSessionHandler<ProxmoxProvider>)) { ServiceTypes = [ServiceType] });
        contributions.AddHostAction(new HostAction("proxmox.guests", "Virtual machines", SessionType: SessionType));

        // SDK 1.15 (§7.49): 1.x's hypervisor scan, the cluster's guests for the shell to link to the hosts they are.
        contributions.AddHostAction(new HostAction("proxmox.scan", "Proxmox VE guests") { Endpoint = "scan", ServiceTypes = [ServiceType], Scan = true });
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddGuestProvider<ProxmoxProvider>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context) =>
        // Only someone who can change the host applies what a scan finds (§7.26).
        endpoints.MapHostGroup(HostPermission.Manage).MapPost("/scan", ProxmoxScanner.ScanAsync);
}
