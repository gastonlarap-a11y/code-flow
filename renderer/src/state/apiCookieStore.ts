/**
 * The API client's cookie jar, one row per (domain, path, name).
 *
 * Workspace-scoped: `hydrate` stamps the owning workspace atomically with the rows, and `reset`
 * stamps the *incoming* one so nothing written during a switch can land under the old scope.
 */

import { create } from "zustand";
import { apiClearCookies, apiDeleteCookie, apiListCookies, apiUpsertCookie } from "../lib/ipc/apiCommands";
import { guarded } from "./apiShared";
import type { ApiCookie } from "../types/api";

interface ApiCookieState {
  workspaceId: string | null;
  cookies: ApiCookie[];
  hydrate: (workspaceId: string, cookies: ApiCookie[]) => void;
  reset: (workspaceId: string | null) => void;
  reloadCookies: () => Promise<void>;
  upsertCookie: (cookie: ApiCookie) => Promise<void>;
  deleteCookie: (id: string) => Promise<void>;
  clearCookies: () => Promise<void>;
}

export const useApiCookieStore = create<ApiCookieState>((set, get) => ({
  workspaceId: null,
  cookies: [],

  hydrate: (workspaceId, cookies) => set({ workspaceId, cookies }),
  reset: (workspaceId) => set({ workspaceId, cookies: [] }),

  reloadCookies: async () => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return;
    set({ cookies: await apiListCookies(workspaceId) });
  },

  upsertCookie: async (cookie) => {
    await guarded(async () => {
      await apiUpsertCookie(cookie);
      set((s) => {
        const index = s.cookies.findIndex(
          (c) => c.domain === cookie.domain && c.path === cookie.path && c.name === cookie.name,
        );
        if (index < 0) return { cookies: [...s.cookies, cookie] };
        const cookies = [...s.cookies];
        cookies[index] = cookie;
        return { cookies };
      });
    });
  },

  deleteCookie: async (id) => {
    await guarded(async () => {
      await apiDeleteCookie(id);
      set((s) => ({ cookies: s.cookies.filter((c) => c.id !== id) }));
    });
  },

  clearCookies: async () => {
    const workspaceId = get().workspaceId;
    if (workspaceId === null) return;
    await guarded(async () => {
      await apiClearCookies(workspaceId);
      set({ cookies: [] });
    });
  },
}));
