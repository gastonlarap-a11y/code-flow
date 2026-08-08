/// <reference types="vitest/config" />
import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],

  // Assets are referenced relatively so the built renderer works under the shell's `app://`
  // protocol handler. Absolute `/assets/…` paths would resolve against the protocol root and 404
  // in a packaged build while working fine in dev — the worst kind of difference to debug.
  base: "./",

  server: {
    // The shell's dev mode points at this exact URL, so the port is fixed. Failing fast on a
    // collision beats the shell silently loading someone else's dev server.
    port: 1420,
    strictPort: true,
  },

  build: {
    // Monaco is its own chunk now, loaded when an editor is first opened, so this is back to being
    // a signal instead of an excuse. It was 4000 while everything shipped as one 21 MB bundle --
    // a threshold nothing could ever cross, which trains everyone to ignore it. 1000 is above the
    // app's own code and below anything that would be worth splitting again.
    //
    // No `manualChunks`: Rollup already hoists what several lazy entries share into one chunk, and
    // hand-written chunk boundaries would go stale the moment an import moves.
    chunkSizeWarningLimit: 1000,
  },

  // Vitest reads this same config, so tests resolve modules exactly the way the app does. That is
  // the whole reason it is here rather than `node --test`: `renderer/src` is full of extensionless
  // relative imports, which Node's resolver rejects and Vite's accepts.
  test: {
    // The default, stated rather than implied: nothing here touches a DOM. Component tests would
    // need `jsdom` and `@testing-library/react`, and none of the three are installed — this covers
    // pure logic, and says so.
    environment: "node",

    // Tests live beside what they test. The i18n check is the exception: it reads
    // `translations.ts` as text rather than importing it, so it sits in `scripts/`.
    include: ["src/**/*.test.ts", "scripts/**/*.test.mjs"],
  },
});
