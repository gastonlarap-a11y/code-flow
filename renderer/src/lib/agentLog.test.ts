import { describe, expect, test } from "vitest";
import { formatAgentLogLine } from "./agentLog";

/**
 * What the user watches while an agent run is in flight.
 *
 * The agentic CLIs stream one JSON object per line and this decides, per line, between three
 * outcomes: show it unwrapped, show it raw, or hide it. Getting that wrong is not an error anyone
 * sees — a run log that shows every `tool_result` payload buries the two lines that said what the
 * agent was doing, and one that hides too much reads as a hung process.
 *
 * The event shapes here are Claude's `--output-format stream-json`, which is the format the other
 * engines were made to match.
 */

const line = (event: unknown) => JSON.stringify(event);

const assistant = (content: unknown[]) => line({ type: "assistant", message: { content } });

describe("output that is not a stream event", () => {
  test("plain text comes through untouched", () => {
    expect(formatAgentLogLine("Running tests…")).toBe("Running tests…");
  });

  // The process can be killed mid-write. Showing half a line beats dropping output the user may
  // need to understand why it stopped.
  test("a truncated JSON line is shown raw rather than dropped", () => {
    expect(formatAgentLogLine('{"type":"assistant","mess')).toBe('{"type":"assistant","mess');
  });

  test("a line that merely starts with a brace is shown raw", () => {
    expect(formatAgentLogLine("{not json at all")).toBe("{not json at all");
  });

  test("valid JSON that is not an object is shown raw", () => {
    expect(formatAgentLogLine("[1, 2, 3]")).toBe("[1, 2, 3]");
  });

  // An engine printing a plain object is not the stream protocol, so it is not bookkeeping either.
  test("an untagged JSON object is shown raw", () => {
    expect(formatAgentLogLine('{"hello":"world"}')).toBe('{"hello":"world"}');
  });
});

describe("an assistant turn", () => {
  test("shows its prose", () => {
    expect(formatAgentLogLine(assistant([{ type: "text", text: "Looking at App.tsx" }]))).toBe(
      "Looking at App.tsx",
    );
  });

  test("collapses whitespace onto one line", () => {
    expect(formatAgentLogLine(assistant([{ type: "text", text: " a \n\n  b  " }]))).toBe("a b");
  });

  test("shows one line per tool the model decided to call", () => {
    const raw = assistant([
      { type: "text", text: "Reading the file" },
      { type: "tool_use", name: "Read", input: { file_path: "src/App.tsx" } },
    ]);

    expect(formatAgentLogLine(raw)).toBe("Reading the file\n⏵ Read: src/App.tsx");
  });

  test("falls back to the bare tool name when nothing in its input is worth showing", () => {
    expect(formatAgentLogLine(assistant([{ type: "tool_use", name: "TodoWrite", input: {} }]))).toBe(
      "⏵ TodoWrite",
    );
  });

  test("is hidden when it carries nothing to say", () => {
    expect(formatAgentLogLine(assistant([]))).toBe(null);
  });

  test("is hidden when its only text is blank", () => {
    expect(formatAgentLogLine(assistant([{ type: "text", text: "   " }]))).toBe(null);
  });
});

describe("choosing which argument of a tool call to show", () => {
  // The order is the point: a Bash call carries both `command` and, sometimes, a `path`, and the
  // command is what the user wants to read.
  test("prefers the earlier key when a call carries several", () => {
    const raw = assistant([
      { type: "tool_use", name: "Grep", input: { pattern: "TODO", path: "src" } },
    ]);

    expect(formatAgentLogLine(raw)).toBe("⏵ Grep: src");
  });

  for (const key of ["file_path", "path", "notebook_path", "command", "pattern", "url", "query", "prompt"]) {
    test(`reads \`${key}\``, () => {
      const raw = assistant([{ type: "tool_use", name: "Tool", input: { [key]: "value" } }]);

      expect(formatAgentLogLine(raw)).toBe("⏵ Tool: value");
    });
  }

  test("skips a key whose value is blank", () => {
    const raw = assistant([{ type: "tool_use", name: "Bash", input: { command: "  ", url: "x" } }]);

    expect(formatAgentLogLine(raw)).toBe("⏵ Bash: x");
  });

  test("truncates a long argument to 160 characters with an ellipsis", () => {
    const long = "x".repeat(200);
    const result = formatAgentLogLine(assistant([{ type: "tool_use", name: "Bash", input: { command: long } }]));

    expect(result).toBe(`⏵ Bash: ${"x".repeat(160)}…`);
  });
});

describe("events that are bookkeeping", () => {
  test("the init event names the model", () => {
    expect(formatAgentLogLine(line({ type: "system", subtype: "init", model: "opus" }))).toBe("· opus");
  });

  test("another system event is hidden", () => {
    expect(formatAgentLogLine(line({ type: "system", subtype: "warning" }))).toBe(null);
  });

  // The tool results the model reads back, and the final verdict the caller renders as the answer.
  test("user and result events are hidden", () => {
    expect(formatAgentLogLine(line({ type: "user", message: {} }))).toBe(null);
    expect(formatAgentLogLine(line({ type: "result", result: "done" }))).toBe(null);
  });

  // Whatever a future CLI version adds is hidden rather than dumped as raw JSON, which would bury
  // the lines that say what the agent is doing.
  test("an unrecognised tagged event is hidden", () => {
    expect(formatAgentLogLine(line({ type: "rate_limit_notice", seconds: 30 }))).toBe(null);
  });
});
