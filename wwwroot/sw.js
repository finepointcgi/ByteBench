// ByteBench service worker: shows browser push notifications (Services/Push) and opens their link on click.
// Served from the site root, so its scope is the whole site. It caches nothing and never touches page requests.
'use strict';

const FALLBACK = '/notifications';

// Only same-origin links: a payload can never send the user to another site.
function safeUrl(value) {
    try {
        const url = new URL(typeof value === 'string' && value ? value : FALLBACK, self.location.origin);
        return url.origin === self.location.origin ? url.href : new URL(FALLBACK, self.location.origin).href;
    } catch {
        return new URL(FALLBACK, self.location.origin).href;
    }
}

function text(value, max) {
    const s = typeof value === 'string' ? value : '';
    return s.length > max ? s.slice(0, max - 1) + '…' : s;
}

self.addEventListener('install', () => self.skipWaiting());
self.addEventListener('activate', event => event.waitUntil(self.clients.claim()));

self.addEventListener('push', event => {
    let data = {};
    try { data = event.data ? event.data.json() : {}; } catch { data = {}; }
    const title = text(data.title, 120) || 'ByteBench';
    event.waitUntil(self.registration.showNotification(title, {
        body: text(data.body, 120),
        icon: '/favicon.png',
        badge: '/favicon.png',
        tag: typeof data.tag === 'string' ? data.tag.slice(0, 64) : undefined,
        data: { url: safeUrl(data.url) },
    }));
});

self.addEventListener('notificationclick', event => {
    event.notification.close();
    const url = safeUrl(event.notification.data && event.notification.data.url);
    event.waitUntil((async () => {
        const windows = await self.clients.matchAll({ type: 'window', includeUncontrolled: true });
        const exact = windows.find(w => w.url === url);
        if (exact) return exact.focus();
        const same = windows.find(w => new URL(w.url).origin === self.location.origin);
        if (same && 'navigate' in same) {
            const focused = await same.focus();
            return (focused || same).navigate(url);
        }
        return self.clients.openWindow(url);
    })());
});
