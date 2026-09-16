import { beforeEach, describe, expect, test, vi } from "vitest";
import type { DbmlTablePosition } from "../types/domain";

/** A promise the test releases by hand, to hold one command in flight while another runs. */
function deferred<T>(): { promise: Promise<T>; release: (value: T) => void } {
  let release: (value: T) => void = () => {};
  const promise = new Promise<T>((resolve) => {
    release = resolve;
  });
  return { promise, release };
}

vi.mock("../lib/ipc/commands", () => ({
  dbmlListDocuments: vi.fn(() => Promise.resolve<string[]>([])),
  readFileText: vi.fn(() => Promise.resolve("")),
  writeFileText: vi.fn(() => Promise.resolve(undefined)),
  createFile: vi.fn(() => Promise.resolve(undefined)),
  dbmlLoadLayout: vi.fn(() => Promise.resolve<DbmlTablePosition[]>([])),
  dbmlSavePositions: vi.fn(() => Promise.resolve(undefined)),
  dbmlClearLayout: vi.fn(() => Promise.resolve(undefined)),
}));

const toasts: string[] = [];
vi.mock("./toastStore", () => ({
  pushErrorToast: (message: string) => toasts.push(message),
  useToastStore: { getState: () => ({ pushToast: () => {} }) },
}));

const api = vi.mocked(await import("../lib/ipc/commands"));
const { useDbmlStore } = await import("./dbmlStore");

const initial = useDbmlStore.getState();

beforeEach(() => {
  toasts.length = 0;
  vi.resetAllMocks();
  useDbmlStore.setState(initial, true);
});

describe("loading the document list", () => {
  test("holds what the sidecar found", async () => {
    api.dbmlListDocuments.mockResolvedValue(["db/orders.dbml", "schema.dbml"]);

    await useDbmlStore.getState().loadDocuments("/project");

    expect(api.dbmlListDocuments).toHaveBeenCalledWith("/project");
    expect(useDbmlStore.getState().documents).toEqual(["db/orders.dbml", "schema.dbml"]);
    expect(useDbmlStore.getState().documentsLoading).toBe(false);
  });

  test("a failure is reported and clears the loading flag", async () => {
    api.dbmlListDocuments.mockRejectedValue(new Error("no such folder"));

    await useDbmlStore.getState().loadDocuments("/project");

    expect(toasts).toHaveLength(1);
    expect(useDbmlStore.getState().documentsLoading).toBe(false);
  });
});

describe("opening a document", () => {
  test("reads the file into the buffer, clean, with its stored positions", async () => {
    api.readFileText.mockResolvedValue("Table users { id int }");
    api.dbmlLoadLayout.mockResolvedValue([{ table_key: "public.users", x: 10, y: 20 }]);

    await useDbmlStore.getState().openDocument("p1", "/project", "schema.dbml");

    expect(api.readFileText).toHaveBeenCalledWith("/project", "schema.dbml");
    expect(api.dbmlLoadLayout).toHaveBeenCalledWith("p1", "schema.dbml");
    expect(useDbmlStore.getState().source).toBe("Table users { id int }");
    expect(useDbmlStore.getState().positions).toEqual({ "public.users": { x: 10, y: 20 } });
    expect(useDbmlStore.getState().dirty).toBe(false);
    expect(useDbmlStore.getState().sourceLoading).toBe(false);
  });

  test("a layout that cannot be read still opens the document, auto-laid out", async () => {
    // The positions are a convenience; the document is the thing the user asked for.
    api.readFileText.mockResolvedValue("Table users { id int }");
    api.dbmlLoadLayout.mockRejectedValue(new Error("database is locked"));

    await useDbmlStore.getState().openDocument("p1", "/project", "schema.dbml");

    expect(toasts).toHaveLength(1);
    expect(useDbmlStore.getState().activePath).toBe("schema.dbml");
    expect(useDbmlStore.getState().source).toBe("Table users { id int }");
    expect(useDbmlStore.getState().positions).toEqual({});
  });

  test("a read that lands after the user picked another document is dropped", async () => {
    // Otherwise the first document's text appears under the second one's name, and saving writes
    // it there.
    const slow = deferred<string>();
    api.readFileText.mockImplementation((_root, relPath) =>
      relPath === "first.dbml" ? slow.promise : Promise.resolve("second text"),
    );
    api.dbmlLoadLayout.mockResolvedValue([]);

    const openingFirst = useDbmlStore.getState().openDocument("p1", "/project", "first.dbml");
    await useDbmlStore.getState().openDocument("p1", "/project", "second.dbml");
    slow.release("first text");
    await openingFirst;

    expect(useDbmlStore.getState().activePath).toBe("second.dbml");
    expect(useDbmlStore.getState().source).toBe("second text");
  });

  test("a read that fails closes the document rather than leaving an empty one open", async () => {
    api.readFileText.mockRejectedValue(new Error("permission denied"));
    api.dbmlLoadLayout.mockResolvedValue([]);

    await useDbmlStore.getState().openDocument("p1", "/project", "schema.dbml");

    expect(toasts).toHaveLength(1);
    expect(useDbmlStore.getState().activePath).toBeNull();
  });
});

describe("saving", () => {
  test("writes the buffer and marks it clean", async () => {
    useDbmlStore.setState({ activePath: "schema.dbml", source: "Table a { id int }", dirty: true });

    await useDbmlStore.getState().save("/project");

    expect(api.writeFileText).toHaveBeenCalledWith("/project", "schema.dbml", "Table a { id int }");
    expect(useDbmlStore.getState().dirty).toBe(false);
  });

  test("an edit made while the write was in flight stays dirty", async () => {
    // The flag is cleared against the text that was written, not against the current buffer —
    // otherwise that edit is silently lost the next time documents are switched.
    const write = deferred<void>();
    api.writeFileText.mockReturnValue(write.promise);
    useDbmlStore.setState({ activePath: "schema.dbml", source: "first", dirty: true });

    const saving = useDbmlStore.getState().save("/project");
    useDbmlStore.getState().setSource("first, edited");
    write.release();
    await saving;

    expect(useDbmlStore.getState().dirty).toBe(true);
  });

  test("a clean buffer writes nothing", async () => {
    useDbmlStore.setState({ activePath: "schema.dbml", source: "Table a { id int }", dirty: false });

    await useDbmlStore.getState().save("/project");

    expect(api.writeFileText).not.toHaveBeenCalled();
  });

  test("a failure is reported and the buffer stays dirty", async () => {
    api.writeFileText.mockRejectedValue(new Error("read-only file system"));
    useDbmlStore.setState({ activePath: "schema.dbml", source: "Table a { id int }", dirty: true });

    await useDbmlStore.getState().save("/project");

    expect(toasts).toHaveLength(1);
    expect(useDbmlStore.getState().dirty).toBe(true);
  });
});

describe("creating a document", () => {
  test("creates it with a starter schema and opens it", async () => {
    api.createFile.mockResolvedValue(undefined);
    api.writeFileText.mockResolvedValue(undefined);
    api.dbmlListDocuments.mockResolvedValue(["orders.dbml"]);

    const error = await useDbmlStore.getState().createDocument("/project", "orders");

    expect(error).toBeNull();
    expect(api.createFile).toHaveBeenCalledWith("/project", "orders.dbml");
    expect(useDbmlStore.getState().activePath).toBe("orders.dbml");
    // A starter table, not an empty file: DBML's syntax is not guessable from a blank editor.
    expect(useDbmlStore.getState().source).toContain("Table users");
    expect(useDbmlStore.getState().dirty).toBe(false);
  });

  test("an invalid name is refused without touching the disk", async () => {
    expect(await useDbmlStore.getState().createDocument("/project", "../outside")).toBe("escapes");
    expect(await useDbmlStore.getState().createDocument("/project", "  ")).toBe("empty");
    expect(api.createFile).not.toHaveBeenCalled();
  });

  test("a name already taken is refused case-insensitively", async () => {
    // macOS and Windows are both case-insensitive, so `create_file` would overwrite the existing
    // document rather than refuse.
    useDbmlStore.setState({ documents: ["Orders.dbml"] });

    expect(await useDbmlStore.getState().createDocument("/project", "orders")).toBe("exists");
    expect(api.createFile).not.toHaveBeenCalled();
  });
});

describe("placing tables", () => {
  test("a dropped table moves in memory and is remembered, in whole pixels", async () => {
    api.dbmlSavePositions.mockResolvedValue(undefined);
    useDbmlStore.setState({ activePath: "schema.dbml" });

    await useDbmlStore.getState().placeTable("p1", "public.users", { x: 120.4, y: 80.6 });

    expect(useDbmlStore.getState().positions).toEqual({ "public.users": { x: 120, y: 81 } });
    expect(api.dbmlSavePositions).toHaveBeenCalledWith("p1", "schema.dbml", [
      { table_key: "public.users", x: 120, y: 81 },
    ]);
  });

  test("a position that cannot be saved stays where it was dropped, and the user is told", async () => {
    // Snapping the card back would contradict what the user just did.
    api.dbmlSavePositions.mockRejectedValue(new Error("database is locked"));
    useDbmlStore.setState({ activePath: "schema.dbml" });

    await useDbmlStore.getState().placeTable("p1", "public.users", { x: 5, y: 5 });

    expect(toasts).toHaveLength(1);
    expect(useDbmlStore.getState().positions).toEqual({ "public.users": { x: 5, y: 5 } });
  });

  test("with no document open nothing is placed or saved", async () => {
    await useDbmlStore.getState().placeTable("p1", "public.users", { x: 5, y: 5 });

    expect(api.dbmlSavePositions).not.toHaveBeenCalled();
    expect(useDbmlStore.getState().positions).toEqual({});
  });

  test("arranging everything forgets the positions on disk and in memory", async () => {
    api.dbmlClearLayout.mockResolvedValue(undefined);
    useDbmlStore.setState({ activePath: "schema.dbml", positions: { "public.users": { x: 5, y: 5 } } });

    await useDbmlStore.getState().arrangeAll("p1");

    expect(api.dbmlClearLayout).toHaveBeenCalledWith("p1", "schema.dbml");
    expect(useDbmlStore.getState().positions).toEqual({});
  });

  test("an arrangement that fails keeps the positions the user had", async () => {
    api.dbmlClearLayout.mockRejectedValue(new Error("database is locked"));
    useDbmlStore.setState({ activePath: "schema.dbml", positions: { "public.users": { x: 5, y: 5 } } });

    await useDbmlStore.getState().arrangeAll("p1");

    expect(toasts).toHaveLength(1);
    expect(useDbmlStore.getState().positions).toEqual({ "public.users": { x: 5, y: 5 } });
  });
});
