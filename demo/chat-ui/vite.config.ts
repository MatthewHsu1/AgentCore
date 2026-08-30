/// <reference types="vitest/config" />
import tailwindcss from "@tailwindcss/vite";
import react from "@vitejs/plugin-react";
import { fileURLToPath, URL } from "node:url";
import { defineConfig } from "vite";
import { resolve } from "node:path";

// The build writes into the demo host's wwwroot, which serves it as static files at /chat.
export default defineConfig({
  plugins: [react(), tailwindcss()],
  base: "/chat/",
  // The vendored components under src/components are assistant-ui's own and import through this
  // alias. It is not a style preference: rewriting their imports would make every future
  // `shadcn add` a merge instead of an overwrite.
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  build: {
    outDir: "../AgentCore.Demo/wwwroot/chat",
    emptyOutDir: true,
    rollupOptions: {
      // Two pages, one bundle of shared chunks. `index` is the full app at /chat; `widget` is the
      // embeddable bubble at /chat/widget.html, framed by public/embed.js.
      input: {
        index: resolve(__dirname, "index.html"),
        widget: resolve(__dirname, "widget.html"),
      },
    },
  },
  // Vitest reads this block, so the runner needs no config file of its own.
  test: {
    // The renderer tests mount React, so they need a DOM. The old `node --test` runner could not
    // even load a .tsx file.
    environment: "happy-dom",
    include: ["src/**/*.test.{ts,tsx}"],
  },
  server: {
    // The dev server serves the UI and forwards the API to the running host, so `npm run dev` and
    // `dotnet run` together give hot reload against a real turn loop.
    proxy: {
      "/v1": "http://127.0.0.1:5199",
    },
  },
});
