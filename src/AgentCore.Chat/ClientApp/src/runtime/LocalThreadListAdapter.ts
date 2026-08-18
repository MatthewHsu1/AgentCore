import { createLocalStorageAdapter } from "@assistant-ui/core/react";
import type { RemoteThreadListAdapter } from "@assistant-ui/react";

/**
 * The thread list, stored in the browser and nowhere else.
 *
 * This file is a stub with a real seam behind it. AgentCore's server knows one call per
 * `X-AgentCore-Session` header and nothing about threads, and it has no notion of a caller to scope
 * threads to — the header names a call, not a person. Until it does, the list lives here.
 *
 * That the browser outlives the server's calls is safe, and only because of what `transport.ts`
 * already does: a turn answered `session_not_found` clears the id and retries with the whole
 * message list. A thread restored from storage after the server forgot its call heals on its next
 * turn rather than failing.
 *
 * The adapter itself is upstream's. `createLocalStorageAdapter` implements every method the thread
 * list needs and handles the exported-message-repository round trip, which is the fiddly part and
 * the part least worth writing twice. What is ours is the store beneath it and the name above it.
 *
 * When .NET grows a thread API, {@link localThreadListAdapter} is the one export that changes.
 * Nothing else in this application knows where threads come from.
 */

/** Everything under this prefix in `localStorage` belongs to the thread list. */
const StoragePrefix = "agentcore-threads";

/**
 * Wraps a synchronous web {@link Storage} as the asynchronous store the adapter wants.
 *
 * The promises are what earn this function its existence. `createLocalStorageAdapter` awaits every
 * call, so a `localStorage` that throws — a browser in private mode, once it refuses the quota —
 * has to arrive as a rejection it can catch rather than as a synchronous throw escaping mid-await.
 *
 * @param store The browser store to read and write through.
 * @returns The same store, with each operation deferred into a promise.
 */
export function browserStorage(store: Storage) {
  return {
    getItem: async (key: string) => store.getItem(key),
    setItem: async (key: string, value: string) => store.setItem(key, value),
    removeItem: async (key: string) => store.removeItem(key),
  };
}

/**
 * The thread list this application runs on.
 *
 * Built on first use rather than at module scope. The tests run under Node, which has no `window`,
 * and they import {@link browserStorage} from this same module — a `window.localStorage` read while
 * the module was evaluating would throw before a single test ran.
 *
 * @returns The adapter, the same instance on every call.
 */
let adapter: RemoteThreadListAdapter | undefined;

export function localThreadListAdapter(): RemoteThreadListAdapter {
  adapter ??= createLocalStorageAdapter({
    storage: browserStorage(window.localStorage),
    prefix: StoragePrefix,
  });

  return adapter;
}
