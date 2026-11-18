import type { SessionViewProps } from '@nkeeper/sdk'
import { virtCore, type HypervisorInfo } from './virtCore'

const PROXMOX: HypervisorInfo = { product: 'Proxmox VE' }

/** The Proxmox tab: virt-core's guests tab, told it's Proxmox VE. */
export function ProxmoxView(props: SessionViewProps) {
  const shared = virtCore()
  return shared ? <shared.GuestsView {...props} hypervisor={PROXMOX} /> : null
}
