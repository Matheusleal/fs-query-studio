import * as esbuild from "esbuild";

const watch = process.argv.includes("--watch");

/** @type {import("esbuild").BuildOptions[]} */
const builds = [
  {
    entryPoints: ["src/extension.ts"],
    bundle: true,
    platform: "node",
    format: "cjs",
    target: "node20",
    outfile: "dist/extension.js",
    external: ["vscode"],
    sourcemap: true,
    logLevel: "info",
  },
  {
    // The results-grid renderer runs in the notebook output webview — a
    // browser context. No `vscode` import; nothing Node-only may enter
    // this graph (see the plan's Global Constraints).
    entryPoints: ["src/renderer/index.ts"],
    bundle: true,
    platform: "browser",
    format: "esm",
    target: "es2020",
    outfile: "media/renderer/index.js",
    sourcemap: true,
    logLevel: "info",
  },
];

if (watch) {
  const contexts = await Promise.all(builds.map((options) => esbuild.context(options)));
  await Promise.all(contexts.map((context) => context.watch()));
} else {
  await Promise.all(builds.map((options) => esbuild.build(options)));
}
