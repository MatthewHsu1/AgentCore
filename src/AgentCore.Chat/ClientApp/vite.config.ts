import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The build writes into the C# project's wwwroot, which is embedded into AgentCore.Chat.dll. The
// output is committed, so a consumer running `dotnet publish` needs no Node at all — see the
// AgentCore.Chat.csproj comment on why that matters for a library nobody deploys for you.
export default defineConfig({
  plugins: [react()],
  base: "/chat/",
  build: {
    outDir: "../wwwroot",
    emptyOutDir: true,
    rollupOptions: {
      // Stable names, not the default content hashes. The C# project embeds wwwroot with a glob
      // that MSBuild evaluates when it loads the project, before the build that writes those files
      // has run. A hashed name therefore breaks the first build after any UI change: the glob still
      // names the file vite just deleted. Re-globbing inside the target does not fix it. Names that
      // never change do, and the cache policy in AgentCoreChatExtensions revalidates instead of
      // trusting the name.
      output: {
        entryFileNames: "assets/[name].js",
        chunkFileNames: "assets/[name].js",
        assetFileNames: "assets/[name].[ext]",
      },
    },
  },
  server: {
    // The dev server serves the UI and forwards the API to the running host, so `npm run dev` and
    // `dotnet run` together give hot reload against a real turn loop.
    proxy: {
      "/v1": "http://127.0.0.1:5199",
    },
  },
});
