import { definePlugin } from '@nkeeper/sdk'
import { ProxmoxView } from './ProxmoxView'
import { keepVirtCore, type VirtCore } from './virtCore'

/**
 * The proxmox plugin's frontend (docs/architecture.md §7.38): it draws its session type with virt-core's guests tab,
 * which virt-core shares with the hypervisor plugins that depend on it (SDK 1.8).
 */
const plugin = definePlugin({
  id: 'proxmox',
  activate(context) {
    const shared = context.use('virt-core')
    if (typeof shared.GuestsView !== 'function' && typeof shared.GuestsView !== 'object') {
      throw new Error('virt-core shares no guests tab: it is older than this plugin needs.')
    }
    keepVirtCore(shared as unknown as VirtCore)
  },
  sessionViews: { proxmox: ProxmoxView },
})

export default plugin
