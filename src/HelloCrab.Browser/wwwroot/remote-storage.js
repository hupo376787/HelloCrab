(function registerHelloCrabRemoteStorage() {
    // 首次从桌面主机打开时，连接地址直接使用当前页面的 IP 和端口。
    // 已保存的自定义服务器地址仍由用户控制，不覆盖。
    try {
        const key = 'HelloCrab.Remote.ServerAddress';
        if (!globalThis.localStorage?.getItem(key) && globalThis.location?.origin) {
            globalThis.localStorage?.setItem(key, globalThis.location.origin);
        }
    } catch (error) {
        console.warn('HelloCrab: cannot save the page origin.', error);
    }

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
