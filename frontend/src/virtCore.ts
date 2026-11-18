import type { ComponentType } from 'react'
import type { SessionViewProps } from '@nkeeper/sdk'

/** What the guests tab is told about a hypervisor. */
export interface HypervisorInfo {
  product: string
}

/** What virt-core's frontend shares (docs/architecture.md §7.36): its guests tab. */
export interface VirtCore {
  GuestsView: ComponentType<SessionViewProps & { hypervisor: HypervisorInfo }>
}

let shared: VirtCore | null = null

/** Keeps virt-core's shared tab, from the plugin's activate. */
export function keepVirtCore(virtCore: VirtCore): void {
  shared = virtCore
}

export function virtCore(): VirtCore | null {
  return shared
}
