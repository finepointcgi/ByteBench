import { isSafePath } from './deeplink';

export interface ApiNotification {
  id: number;
  kind: string;
  title: string;
  url: string;
  isRead: boolean;
  createdAt: string;
  actor?: string | null;
}

export interface NotificationList {
  unreadCount: number;
  items: ApiNotification[];
}

/** Parses GET /api/v1/notifications defensively: the app trusts nothing a server (or a middlebox) returns. */
export function parseNotificationList(body: unknown): NotificationList | null {
  if (!body || typeof body !== 'object') return null;
  const o = body as Record<string, unknown>;
  if (typeof o.unreadCount !== 'number' || !Array.isArray(o.items)) return null;
  const items: ApiNotification[] = [];
  for (const raw of o.items) {
    if (!raw || typeof raw !== 'object') continue;
    const n = raw as Record<string, unknown>;
    if (typeof n.id !== 'number' || !Number.isInteger(n.id) || typeof n.title !== 'string' || typeof n.url !== 'string') continue;
    items.push({
      id: n.id,
      kind: typeof n.kind === 'string' ? n.kind : '',
      title: n.title.slice(0, 200),
      url: n.url,
      isRead: n.isRead === true,
      createdAt: typeof n.createdAt === 'string' ? n.createdAt : '',
      actor: typeof n.actor === 'string' ? n.actor.slice(0, 100) : null,
    });
  }
  return { unreadCount: Math.max(0, Math.floor(o.unreadCount)), items };
}

/**
 * Which notifications to announce. `lastNotifiedId` undefined means this profile was never polled: nothing is announced
 * (the first poll only records where we are) so adding a server does not produce a burst of old notifications.
 */
export function selectToAnnounce(items: readonly ApiNotification[], lastNotifiedId: number | undefined, max = 5): { show: ApiNotification[]; more: number; newest: number | undefined } {
  const newest = items.reduce<number | undefined>((m, n) => (m === undefined || n.id > m ? n.id : m), undefined);
  if (lastNotifiedId === undefined) return { show: [], more: 0, newest };
  const fresh = items.filter((n) => !n.isRead && n.id > lastNotifiedId).sort((a, b) => a.id - b.id);
  return { show: fresh.slice(-max), more: Math.max(0, fresh.length - max), newest: newest !== undefined && newest > lastNotifiedId ? newest : undefined };
}

/** The path to open for a notification, or "/notifications" if the server sent something that is not a safe server path. */
export function pathForNotification(n: Pick<ApiNotification, 'url'>, origin: string): string {
  return isSafePath(n.url, origin) ? n.url : '/notifications';
}

/** Back-off for polling after failures: 30s normally, doubling to at most 5 minutes. */
export function nextPollDelayMs(consecutiveFailures: number, baseMs = 30_000): number {
  return Math.min(baseMs * 2 ** Math.min(consecutiveFailures, 4), 300_000);
}
