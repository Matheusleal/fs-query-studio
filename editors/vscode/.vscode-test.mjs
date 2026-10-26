import { defineConfig } from "@vscode/test-cli";

export default defineConfig({
  files: "out/test/integration/**/*.test.js",
  version: "1.94.2",
  mocha: {
    ui: "bdd",
    timeout: 60000,
  },
});
