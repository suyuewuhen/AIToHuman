import { HubConnectionBuilder, HubConnectionState, type HubConnection } from '@microsoft/signalr'
import type { OrderItem } from './tasks'

let connection: HubConnection | null = null
export async function connectOrderNotifications(userId: string, onOrderCreated: (order: OrderItem) => void) {
  await disconnectOrderNotifications()
  const token = localStorage.getItem('aitohuman.accessToken')
  connection = new HubConnectionBuilder().withUrl(`/hubs/notifications?userId=${encodeURIComponent(userId)}`, { accessTokenFactory: () => token ?? '' }).withAutomaticReconnect().build()
  connection.on('OrderCreated', onOrderCreated)
  await connection.start()
}
export async function disconnectOrderNotifications() {
  if (connection && connection.state !== HubConnectionState.Disconnected) await connection.stop()
  connection = null
}
