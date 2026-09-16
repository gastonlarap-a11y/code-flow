import { create } from "zustand";
import * as api from "../lib/ipc/commands";
import { pushErrorToast } from "./toastStore";
import { normalizeDocumentPath, type DocumentPathError } from "../lib/dbml/documentPath";
import type { Point } from "../lib/dbml/layout";
import type { DbmlTablePosition } from "../types/domain";

/**
 * The schema designer's document state: which `.dbml` files the project holds, which one is open,
 * its text, and where a person placed its tables (DBML-002, DBML-005).
 *
 * A document is a file in the user's folder, so this store owns no copy of it beyond the buffer
 * being edited — opening and saving go straight to `read_file_text`/`write_file_text`, the same
 * commands the editor uses, which work whether or not the folder is a repository (GIT-039).
 */
interface DbmlState {
  /** Project-relative paths of every `.dbml` in the folder, sorted by the sidecar. */
  documents: string[];
  /** The open document, or `null` when none is. */
  activePath: string | null;
  /** The buffer. Authoritative while open: the diagram renders from this, not from disk. */
  source: string;
  /** Whether `source` has edits that are not on disk yet. */
  dirty: boolean;
  /**
   * The positions a person set for the open document's tables, by table key. A table absent here is
   * placed by the auto-layout; a key with no table in the document is simply not drawn.
   */
  positions: Record<string, Point>;
  documentsLoading: boolean;
  sourceLoading: boolean;
  saving: boolean;

  loadDocuments: (rootPath: string) => Promise<void>;
  openDocument: (projectId: string, rootPath: string, relPath: string) => Promise<void>;
  setSource: (source: string) => void;
  save: (rootPath: string) => Promise<void>;
  /** Creates an empty document and opens it. Returns the error to show under the field, or null. */
  createDocument: (rootPath: string, name: string) => Promise<DocumentPathError | "exists" | null>;
  /** Puts one table where a person dropped it, and remembers that. */
  placeTable: (projectId: string, tableKey: string, point: Point) => Promise<void>;
  /** Forgets every position of the open document, so the auto-layout arranges all of it again. */
  arrangeAll: (projectId: string) => Promise<void>;
  /** Drops everything, for a project switch. */
  reset: () => void;
}

/**
 * What a new document starts as.
 *
 * Not empty: an empty canvas with an empty editor beside it says nothing about what to type, and
 * DBML's syntax is not guessable. One table is the smallest thing that renders.
 */
const STARTER_SOURCE = `Table users {
  id integer [primary key]
  name varchar
}
`;

function toPositions(rows: readonly DbmlTablePosition[]): Record<string, Point> {
  return Object.fromEntries(rows.map((row) => [row.table_key, { x: row.x, y: row.y }]));
}

export const useDbmlStore = create<DbmlState>((set, get) => ({
  documents: [],
  activePath: null,
  source: "",
  dirty: false,
  positions: {},
  documentsLoading: false,
  sourceLoading: false,
  saving: false,

  loadDocuments: async (rootPath) => {
    set({ documentsLoading: true });
    try {
      const documents = await api.dbmlListDocuments(rootPath);
      set({ documents });
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      set({ documentsLoading: false });
    }
  },

  openDocument: async (projectId, rootPath, relPath) => {
    set({ activePath: relPath, sourceLoading: true, source: "", dirty: false, positions: {} });
    try {
      const [source, layout] = await Promise.all([
        api.readFileText(rootPath, relPath),
        // A layout that cannot be read is not a document that cannot be opened: the auto-layout
        // places every table, and the failure is reported once.
        api.dbmlLoadLayout(projectId, relPath).catch((e: unknown) => {
          pushErrorToast(String(e));
          const none: DbmlTablePosition[] = [];
          return none;
        }),
      ]);
      // The user can pick another document while these reads are in flight; the newer one owns the
      // buffer. Same guard `repoStore.setRepoPath` applies to its own reads.
      if (get().activePath !== relPath) return;
      set({ source, positions: toPositions(layout) });
    } catch (e) {
      if (get().activePath !== relPath) return;
      pushErrorToast(String(e));
      set({ activePath: null });
    } finally {
      if (get().activePath === relPath) set({ sourceLoading: false });
    }
  },

  setSource: (source) => set({ source, dirty: true }),

  save: async (rootPath) => {
    const { activePath, source, dirty } = get();
    if (activePath === null || !dirty) return;

    set({ saving: true });
    try {
      await api.writeFileText(rootPath, activePath, source);
      // Compared against the text that was written, not against the current buffer: an edit made
      // while the write was in flight must stay dirty, or it is silently lost at the next switch.
      if (get().source === source) set({ dirty: false });
    } catch (e) {
      pushErrorToast(String(e));
    } finally {
      set({ saving: false });
    }
  },

  createDocument: async (rootPath, name) => {
    const normalized = normalizeDocumentPath(name);
    if (!normalized.ok) return normalized.reason;

    const { relPath } = normalized;
    // Case-insensitively, because macOS and Windows both are: "Orders.dbml" and "orders.dbml" are
    // one file there, and `create_file` would overwrite rather than refuse.
    const taken = get().documents.some((d) => d.toLowerCase() === relPath.toLowerCase());
    if (taken) return "exists";

    try {
      await api.createFile(rootPath, relPath);
      await api.writeFileText(rootPath, relPath, STARTER_SOURCE);
    } catch (e) {
      pushErrorToast(String(e));
      return null;
    }

    await get().loadDocuments(rootPath);
    set({ activePath: relPath, source: STARTER_SOURCE, dirty: false, sourceLoading: false, positions: {} });

    return null;
  },

  placeTable: async (projectId, tableKey, point) => {
    const { activePath } = get();
    if (activePath === null) return;

    // Whole pixels: sub-pixel positions blur card borders and text, and nobody drags to a fraction.
    const rounded = { x: Math.round(point.x), y: Math.round(point.y) };
    set((s) => ({ positions: { ...s.positions, [tableKey]: rounded } }));

    try {
      await api.dbmlSavePositions(projectId, activePath, [{ table_key: tableKey, ...rounded }]);
    } catch (e) {
      // The card stays where it was dropped — moving it back would contradict what the user just
      // did — and they are told it was not remembered.
      pushErrorToast(String(e));
    }
  },

  arrangeAll: async (projectId) => {
    const { activePath } = get();
    if (activePath === null) return;

    try {
      await api.dbmlClearLayout(projectId, activePath);
      if (get().activePath === activePath) set({ positions: {} });
    } catch (e) {
      pushErrorToast(String(e));
    }
  },

  reset: () =>
    set({
      documents: [],
      activePath: null,
      source: "",
      dirty: false,
      positions: {},
      documentsLoading: false,
      sourceLoading: false,
      saving: false,
    }),
}));
