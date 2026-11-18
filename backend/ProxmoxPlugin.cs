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
    }

    public void ConfigureServices(IServiceCollection services, PluginContext context) => services.AddGuestProvider<ProxmoxProvider>();

    public void MapEndpoints(IEndpointRouteBuilder endpoints, PluginContext context)
    {
    }
}
