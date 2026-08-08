import { connect, type Socket } from "node:net";
import { randomUUID } from "node:crypto";

/**
 * The shell's half of the IPC transport.
 *
 * Two connections to one endpoint: `rpc` carries all 218 commands, `stream` carries PTY output
 * and every event. Framing solves message boundaries, not write-side contention — a pipe is one
 * ordered byte stream, so a multi-megabyte diff response and a PTY keystroke echo sharing a
 * connection have to serialise, and the terminal stutters. Splitting bulk from streaming is what
 * removes that.
 *
 * Wire format: a 4-byte little-endian length followed by that many bytes of UTF-8 JSON.
 */

const HEADER_SIZE = 4;

/** Matches the sidecar's own cap; a longer prefix means the stream is out of sync. */
const MAX_FRAME_SIZE = 64 * 1024 * 1024;

export type ChannelKind = "rpc" | "stream";

export type EventHandler = (payload: unknown) => void;

type Pending = {
  resolve: (value: unknown) => void;
  reject: (reason: Error) => void;
};

/** Reassembles length-prefixed frames from a socket's arbitrary chunk boundaries. */
export class FrameReader {
  // Annotated as plain `Buffer` (= `Buffer<ArrayBufferLike>`): `Buffer.alloc` would infer the
  // narrower `Buffer<ArrayBuffer>`, which incoming socket chunks are not assignable to.
  private buffer: Buffer = Buffer.alloc(0);

  constructor(private readonly onFrame: (frame: Buffer) => void) {}

  // `string` is in the signature because `@types/node` 25 widened the socket's `data` listener from
  // `Buffer` to `string | Buffer`. No encoding is ever set on these sockets, so what actually
  // arrives is a Buffer; decoding rather than casting keeps this correct if that stops being true,
  // and lets the signature hold under both major versions of the types.
  push(chunk: string | Buffer): void {
    const bytes = typeof chunk === "string" ? Buffer.from(chunk, "utf8") : chunk;
    this.buffer = this.buffer.length === 0 ? bytes : Buffer.concat([this.buffer, bytes]);

    for (;;) {
      if (this.buffer.length < HEADER_SIZE) return;

      const length = this.buffer.readUInt32LE(0);
      if (length > MAX_FRAME_SIZE) {
        throw new Error(`frame length ${length} exceeds the limit — the stream is out of sync`);
      }
      if (this.buffer.length < HEADER_SIZE + length) return;

      const frame = this.buffer.subarray(HEADER_SIZE, HEADER_SIZE + length);
      this.buffer = this.buffer.subarray(HEADER_SIZE + length);
      this.onFrame(frame);
    }
  }
}

function writeFrame(socket: Socket, value: unknown): void {
  const payload = Buffer.from(JSON.stringify(value), "utf8");
  const header = Buffer.allocUnsafe(HEADER_SIZE);
  header.writeUInt32LE(payload.length, 0);
  socket.write(header);
  socket.write(payload);
}

export type SidecarStatus = "starting" | "ready" | "down";

export class IpcClient {
  private rpc: Socket | null = null;
  private stream: Socket | null = null;

  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private readonly listeners = new Map<string, Set<EventHandler>>();

  private status: SidecarStatus = "starting";
  private statusDetail: string | undefined;
  private readyResolvers: Array<() => void> = [];

  /** Raised whenever the sidecar's availability changes, so the UI can say something useful. */
  onStatusChange: ((status: SidecarStatus, detail?: string) => void) | null = null;

  /**
   * The current availability, for a caller that arrived after the change was announced.
   *
   * The event alone is not enough: a sidecar that fails to spawn is `down` within milliseconds of
   * `whenReady`, long before the renderer has loaded a listener. Without something to ask, the one
   * failure the user most needs told about is the one guaranteed to be missed.
   */
  get state(): { status: SidecarStatus; detail?: string } {
    return this.statusDetail === undefined
      ? { status: this.status }
      : { status: this.status, detail: this.statusDetail };
  }

  readonly token = randomUUID();

  async connect(endpoint: string, timeoutMs = 15_000): Promise<void> {
    const [rpc, stream] = await Promise.all([
      this.open(endpoint, "rpc", timeoutMs),
      this.open(endpoint, "stream", timeoutMs),
    ]);

    this.rpc = rpc;
    this.stream = stream;
    this.setStatus("ready");
  }

  /**
   * Resolves once both channels are up.
   *
   * Every `invoke` awaits this, which is the concrete mechanism behind "the renderer must not
   * deadlock": a call placed before the sidecar is listening waits for readiness rather than
   * being written into a socket that does not exist yet.
   */
  whenReady(timeoutMs = 15_000): Promise<void> {
    if (this.status === "ready") return Promise.resolve();
    if (this.status === "down") return Promise.reject(new Error("the CodeFlow core is not running"));

    return new Promise((resolve, reject) => {
      const timer = setTimeout(
        () => reject(new Error(`the CodeFlow core did not start within ${timeoutMs / 1000}s`)),
        timeoutMs,
      );
      this.readyResolvers.push(() => {
        clearTimeout(timer);
        resolve();
      });
    });
  }

  async invoke<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
    await this.whenReady();

    const socket = this.rpc;
    if (!socket) throw new Error("the CodeFlow core is not running");

    const id = this.nextId++;
    return new Promise<T>((resolve, reject) => {
      this.pending.set(id, { resolve: resolve as (value: unknown) => void, reject });
      try {
        writeFrame(socket, { id, method, params });
      } catch (error) {
        this.pending.delete(id);
        reject(error instanceof Error ? error : new Error(String(error)));
      }
    });
  }

  on(event: string, handler: EventHandler): () => void {
    let handlers = this.listeners.get(event);
    if (!handlers) {
      handlers = new Set();
      this.listeners.set(event, handlers);
    }
    handlers.add(handler);
    return () => handlers.delete(handler);
  }

  /**
   * Marks the sidecar as gone and fails everything waiting on it.
   *
   * Deliberately no silent respawn. A restart would risk losing or duplicating in-flight AI runs,
   * terminals and half-written transactions; the shell surfaces the failure and the user decides.
   */
  markDown(detail: string): void {
    this.rpc?.destroy();
    this.stream?.destroy();
    this.rpc = null;
    this.stream = null;

    for (const [, pending] of this.pending) {
      pending.reject(new Error(`the CodeFlow core stopped: ${detail}`));
    }
    this.pending.clear();

    this.setStatus("down", detail);
  }

  private setStatus(status: SidecarStatus, detail?: string): void {
    this.status = status;
    this.statusDetail = detail;
    if (status === "ready") {
      for (const resolve of this.readyResolvers) resolve();
      this.readyResolvers = [];
    }
    this.onStatusChange?.(status, detail);
  }

  private open(endpoint: string, channel: ChannelKind, timeoutMs: number): Promise<Socket> {
    return new Promise((resolve, reject) => {
      const deadline = Date.now() + timeoutMs;

      const attempt = () => {
        const socket = connect(endpoint);

        socket.once("connect", () => {
          writeFrame(socket, { channel, token: this.token });
          this.attach(socket, channel);
          resolve(socket);
        });

        socket.once("error", (error) => {
          socket.destroy();
          // The sidecar binds its endpoint a moment after spawn; retrying beats a fixed sleep,
          // which would either be too short on a cold start or waste time on a warm one.
          if (Date.now() < deadline) {
            setTimeout(attempt, 50);
          } else {
            reject(new Error(`could not connect the ${channel} channel: ${error.message}`));
          }
        });
      };

      attempt();
    });
  }

  private attach(socket: Socket, channel: ChannelKind): void {
    const reader = new FrameReader((frame) => this.dispatch(frame, channel));

    socket.on("data", (chunk) => {
      try {
        reader.push(chunk);
      } catch (error) {
        this.markDown(error instanceof Error ? error.message : String(error));
      }
    });

    socket.on("close", () => {
      if (this.status === "ready") this.markDown(`the ${channel} channel closed`);
    });
  }

  private dispatch(frame: Buffer, channel: ChannelKind): void {
    const message = JSON.parse(frame.toString("utf8")) as Record<string, unknown>;

    if (channel === "rpc") {
      const pending = this.pending.get(message.id as number);
      if (!pending) return;
      this.pending.delete(message.id as number);

      // CodeFlow 1.7.2 surfaces failures as Err(String), which the renderer sees as a rejected
      // promise. Those strings are a contract — the frontend keys off prefixes such as
      // "CHECKOUT_CONFLICT: " — so they are passed through unchanged.
      if (typeof message.error === "string") {
        pending.reject(new Error(message.error));
      } else {
        pending.resolve(message.result);
      }
      return;
    }

    const handlers = this.listeners.get(message.event as string);
    if (!handlers) return;
    for (const handler of handlers) handler(message.payload);
  }
}
