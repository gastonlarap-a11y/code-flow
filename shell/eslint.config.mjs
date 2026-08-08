// The shell's second static check, alongside `tsc`.
//
// `.mjs` rather than `.js`, and that is not a preference: this package has no `"type"` field, so
// Node reads a `.js` here as CommonJS and ESLint 10 only evaluates flat config as ESM. The
// extension is what makes the file loadable without turning the package — whose preload script
// must stay CJS — into a module.
//
// No React plugins: this is the Electron main and preload, ~600 lines of glue with no components.

import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";

export default tseslint.config(
  // `build/` holds the staged renderer bundle (Monaco included) that `build-app.sh` copies in.
  // Linting a 21 MB generated chunk would be minutes of work to report nothing actionable.
  { ignores: ["dist/**", "build/**", "node_modules/**"] },

  {
    files: ["src/**/*.ts"],

    extends: [js.configs.recommended, tseslint.configs.recommended],

    languageOptions: {
      globals: globals.node,
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },

    rules: {
      // The same four as the renderer. `checksVoidReturn` needs no exception here: there is no JSX
      // in this package, so the shape that made 91 reports over there does not exist.
      "@typescript-eslint/no-explicit-any": "error",
      "@typescript-eslint/consistent-type-imports": "error",
      // `node:test`'s `test()` returns a promise the runner awaits itself, so every one of the 21
      // test cases here reads as a floating promise without this. Named rather than switched off
      // for test files: a genuinely unawaited call inside a test is still an error.
      "@typescript-eslint/no-floating-promises": [
        "error",
        { allowForKnownSafeCalls: [{ from: "package", name: "test", package: "node:test" }] },
      ],
      "@typescript-eslint/no-misused-promises": "error",

      // Same convention as the renderer, and the same reason: `_` is how this codebase marks a
      // binding it destructured only to discard, which is what `tsc` already ignores.
      "@typescript-eslint/no-unused-vars": [
        "error",
        {
          argsIgnorePattern: "^_",
          varsIgnorePattern: "^_",
          caughtErrorsIgnorePattern: "^_",
          destructuredArrayIgnorePattern: "^_",
          ignoreRestSiblings: true,
        },
      ],
    },
  },
);
