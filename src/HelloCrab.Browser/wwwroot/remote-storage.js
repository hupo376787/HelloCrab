(function registerHelloCrabRemoteStorage() {
    globalThis.helloCrabRemoteStorageGetItem = function getItem(key) {
        try {
            return globalThis.localStorage?.getItem(key) ?? null;
        } catch (error) {
            console.warn('HelloCrab: localStorage read failed.', error);
            return null;
        }
    };

    globalThis.helloCrabRemoteStorageSetItem = function setItem(key, value) {
        try {
            globalThis.localStorage?.setItem(key, value ?? '');
        } catch (error) {
            // Private browsing and hardened browser profiles may disable storage.
            // The application must remain usable even when persistence is unavailable.
            console.warn('HelloCrab: localStorage write failed.', error);
        }
    };
})();
