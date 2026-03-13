self.importScripts('./service-worker-assets.js');
self.addEventListener('install', event => event.waitUntil(onInstall(event)));
self.addEventListener('activate', event => event.waitUntil(onActivate(event)));
self.addEventListener('fetch', event => event.respondWith(onFetch(event)));

const cacheNamePrefix = 'offline-cache-';
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;
const offlineAssetsInclude = [/\.dll$/, /\.pdb$/, /\.wasm/, /\.html/, /\.js$/, /\.json$/, /\.css$/, /\.woff$/, /\.png$/, /\.jpe?g$/, /\.gif$/, /\.ico$/, /\.blat$/, /\.dat$/];
const offlineAssetsExclude = [/^service-worker\.js$/];

async function onInstall(event) {
    const assetsRequests = self.assetsManifest.assets
        .filter(a => offlineAssetsInclude.some(p => p.test(a.url)))
        .filter(a => !offlineAssetsExclude.some(p => p.test(a.url)))
        .map(a => new Request(a.url, { integrity: a.hash, cache: 'no-cache' }));
    const cache = await caches.open(cacheName);
    await cache.addAll(assetsRequests);
}

async function onActivate(event) {
    const cacheKeys = await caches.keys();
    await Promise.all(cacheKeys.filter(k => k.startsWith(cacheNamePrefix) && k !== cacheName).map(k => caches.delete(k)));
}

async function onFetch(event) {
    if (event.request.method !== 'GET') return fetch(event.request);
    if (event.request.url.startsWith(self.origin)) {
        const cache = await caches.open(cacheName);
        return (await cache.match(event.request)) || fetch(event.request);
    }
    return fetch(event.request);
}
