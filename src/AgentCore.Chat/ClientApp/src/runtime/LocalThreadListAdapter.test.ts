import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { browserStorage } from "./LocalThreadListAdapter.ts";

/** The smallest thing that behaves like `window.localStorage` for these tests. */
function fakeStore(): Storage {
  const map = new Map<string, string>();

  return {
    get length() {
      return map.size;
    },
    clear: () => map.clear(),
    getItem: (key: string) => map.get(key) ?? null,
    key: (index: number) => [...map.keys()][index] ?? null,
    removeItem: (key: string) => void map.delete(key),
    setItem: (key: string, value: string) => void map.set(key, value),
  };
}

describe("browserStorage", () => {
  it("round-trips a value", async () => {
    const storage = browserStorage(fakeStore());

    await storage.setItem("thread", "{}");

    assert.equal(await storage.getItem("thread"), "{}");
  });

  it("resolves null for a key that was never written", async () => {
    const storage = browserStorage(fakeStore());

    assert.equal(await storage.getItem("absent"), null);
  });

  it("removes a value", async () => {
    const storage = browserStorage(fakeStore());
    await storage.setItem("thread", "{}");

    await storage.removeItem("thread");

    assert.equal(await storage.getItem("thread"), null);
  });

  it("rejects rather than throwing when the store refuses a write", async () => {
    // What a browser in private mode does once its quota is refused. The adapter above us awaits
    // every call, so a synchronous throw here would escape as an unhandled error instead of a
    // rejected promise it can catch.
    const refusing = {
      ...fakeStore(),
      setItem: () => {
        throw new Error("QuotaExceededError");
      },
    } as Storage;

    await assert.rejects(() => browserStorage(refusing).setItem("thread", "{}"));
  });
});
