// The renderer's second static check, after `tsc --noEmit`.
//
// It exists because of a bug `tsc` cannot see: `useDialog` was called *after* an early return in
// `SettingsView.tsx` and `UpdateNotesModal.tsx`, which type-checks perfectly and breaks the hook
// order at runtime. `react-hooks/rules-of-hooks` is the rule that catches it, and it is the only
// one here set to `error` on purpose — see the severity note below.
//
// Flat config, and no `.eslintrc` fallback: ESLint 10 removed the old format entirely.

import js from "@eslint/js";
import globals from "globals";
import tseslint from "typescript-eslint";
import reactHooks from "eslint-plugin-react-hooks";
import reactRefresh from "eslint-plugin-react-refresh";

// `recommended-latest` ships seventeen rules, and fifteen of them are the React Compiler's
// (`purity`, `immutability`, `refs`, `set-state-in-effect`, …) at `error`. On 55 k lines that have
// never seen a linter that is not a diagnostic, it is a refactor — so everything except
// `rules-of-hooks` is demoted to `warn` and the promotion is a decision of its own, taken once the
// first run says how much there is.
//
// Derived from the preset rather than listed by hand: a plugin upgrade that adds a rule lands as a
// warning, not as a red build nobody asked for.
const reactHooksAdvisory = Object.fromEntries(
  Object.keys(reactHooks.configs["recommended-latest"].rules)
    .filter((rule) => rule !== "react-hooks/rules-of-hooks")
    .map((rule) => [rule, "warn"]),
);

export default tseslint.config(
  { ignores: ["dist/**", "node_modules/**"] },

  {
    files: ["src/**/*.{ts,tsx}"],

    extends: [
      js.configs.recommended,
      // `recommended`, not `recommendedTypeChecked`: the latter is a policy change of its own size
      // (`no-unsafe-*` across every `JSON.parse` boundary in the app). The parser below is still
      // type-aware, which is what the two rules that need type information depend on.
      tseslint.configs.recommended,
      reactRefresh.configs.vite,
    ],

    languageOptions: {
      globals: globals.browser,
      parserOptions: {
        projectService: true,
        tsconfigRootDir: import.meta.dirname,
      },
    },

    plugins: { "react-hooks": reactHooks },

    rules: {
      ...reactHooksAdvisory,
      "react-hooks/rules-of-hooks": "error",

      // Fast refresh only reloads a module that exports components and nothing else. Real, but it
      // fires on files that pair a component with its constants, and rearranging those is not this
      // change.
      "react-refresh/only-export-components": "warn",

      // Monaco has exactly one door: `lib/monacoEditor.ts`, which pulls in the setup that points
      // `@monaco-editor/react` at the bundled copy and wires the language workers. Importing the
      // package directly compiles, renders, and then quietly asks a CDN for the editor in an
      // offline desktop app. It also puts Monaco in whatever chunk did the importing, which is how
      // the entry bundle got to 21 MB. `allowTypeImports` because `import type { editor }` is
      // erased before any of that can happen.
      "@typescript-eslint/no-restricted-imports": [
        "error",
        {
          paths: [
            {
              name: "@monaco-editor/react",
              message: "Import Editor/DiffEditor from lib/monacoEditor instead.",
              allowTypeImports: true,
            },
            {
              name: "monaco-editor",
              message: "Import the `monaco` instance from lib/monacoEditor instead.",
              allowTypeImports: true,
            },
          ],
        },
      ],

      // The four rules from the TypeScript conventions that apply here. The last two are the
      // reason the parser above is type-aware; the first two are syntactic.
      "@typescript-eslint/no-explicit-any": "error",
      "@typescript-eslint/consistent-type-imports": "error",
      "@typescript-eslint/no-floating-promises": "error",

      // `checksVoidReturn` is off for JSX attributes and object properties, which is where 91 of
      // this rule's 105 first-run reports came from: `onClick={publish}` and its kind. React
      // discards a handler's return value, so `onClick={() => void publish()}` changes nothing at
      // runtime -- the rejection it might produce is exactly as unhandled after the rewrite as
      // before. The rule stays on for the shapes that do bite: a promise used as a condition,
      // spread, or passed where a value is expected.
      "@typescript-eslint/no-misused-promises": [
        "error",
        { checksVoidReturn: { attributes: false, properties: false } },
      ],

      // A leading underscore is how this codebase says "destructured only to drop it"
      // (`const { _removed, ...rest } = …`), and it is what `tsc`'s own `noUnusedLocals` already
      // ignores. Without this the two checks would disagree about the same twelve lines.
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

  {
    // The setup chain itself. `monacoEditor` is the door, `monacoSetup` is what it opens, and
    // `goToDefinition` is imported *by* `monacoSetup` — pointing it at the door would close a
    // cycle. These three are the only place allowed to name the Monaco packages.
    files: ["src/lib/monacoSetup.ts", "src/lib/monacoEditor.ts", "src/lib/goToDefinition.ts"],
    rules: { "@typescript-eslint/no-restricted-imports": "off" },
  },

  {
    // Build and script files: Node, not the browser, and outside `tsconfig.json`'s `include`, so a
    // type-aware parse would reject them outright.
    files: ["vite.config.ts", "scripts/**/*.mjs"],
    extends: [js.configs.recommended, tseslint.configs.disableTypeChecked],
    languageOptions: { globals: globals.node },
  },
);
