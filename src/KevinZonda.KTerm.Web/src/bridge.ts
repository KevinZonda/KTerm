import { DEFAULT_THEME_NAME, normalizeTerminalThemeName } from './themes';

export interface SessionCreated {
  sessionId: string;
  shellName: string;
  processId: number;
}

export interface FontSettings {
  family: string;
  size: number;
  lineHeight: number;
}

export interface AppSettings {
  font: FontSettings;
  theme: ThemeSettings;
}

export interface ThemeSettings {
  name: string;
}

export const DEFAULT_SETTINGS: AppSettings = {
  font: {
    family: 'Cascadia Mono, Cascadia Code, Consolas, monospace',
    size: 14,
    lineHeight: 1.12
  },
  theme: {
    name: DEFAULT_THEME_NAME
  }
};

export interface BridgeEvent {
  version: number;
  type: string;
  requestId?: string;
  sessionId?: string;
  payload: Record<string, unknown>;
}

type BridgeEventHandler = (event: BridgeEvent) => void;

interface PendingRequest {
  resolve: (event: BridgeEvent) => void;
  reject: (error: Error) => void;
}

export class NativeBridge {
  private readonly handlers = new Map<string, Set<BridgeEventHandler>>();
  private readonly pending = new Map<string, PendingRequest>();

  public constructor() {
    window.chrome.webview.addEventListener('message', this.handleMessage);
  }

  public async ready(): Promise<AppSettings> {
    const event = await this.request('app.ready', {});
    return this.settingsFrom(event);
  }

  public async createSession(cols = 80, rows = 24): Promise<SessionCreated> {
    const event = await this.request('session.create', { cols, rows });
    if (!event.sessionId) {
      throw new Error('The native host did not return a session ID.');
    }

    return {
      sessionId: event.sessionId,
      shellName: this.payloadString(event, 'shellName') || 'shell',
      processId: this.payloadNumber(event, 'processId')
    };
  }

  public sendInput(sessionId: string, data: string): void {
    this.send('session.input', { data }, sessionId);
  }

  public resize(sessionId: string, cols: number, rows: number): void {
    this.send('session.resize', { cols, rows }, sessionId);
  }

  public closeSession(sessionId: string): void {
    this.send('session.close', {}, sessionId);
  }

  public beginWindowResize(edge: string): void {
    this.send('window.resize', { edge });
  }

  public openSettings(): void {
    this.send('window.settings', {});
  }

  public settingsFrom(event: BridgeEvent): AppSettings {
    const settings = event.payload.settings;
    if (typeof settings !== 'object' || settings === null) {
      return structuredClone(DEFAULT_SETTINGS);
    }

    const partialSettings = settings as Partial<AppSettings>;
    const font = typeof partialSettings.font === 'object' && partialSettings.font !== null
      ? partialSettings.font
      : DEFAULT_SETTINGS.font;
    const theme = typeof partialSettings.theme === 'object' && partialSettings.theme !== null
      ? partialSettings.theme
      : DEFAULT_SETTINGS.theme;

    const family = typeof font.family === 'string' && font.family.trim()
      ? font.family.trim()
      : DEFAULT_SETTINGS.font.family;
    const size = typeof font.size === 'number' && Number.isFinite(font.size)
      ? Math.min(72, Math.max(8, font.size))
      : DEFAULT_SETTINGS.font.size;
    const lineHeight = typeof font.lineHeight === 'number' && Number.isFinite(font.lineHeight)
      ? Math.min(2, Math.max(0.8, font.lineHeight))
      : DEFAULT_SETTINGS.font.lineHeight;

    return {
      font: { family, size, lineHeight },
      theme: { name: normalizeTerminalThemeName(theme.name) }
    };
  }

  public writeClipboard(text: string): void {
    this.send('clipboard.write', { text });
  }

  public async readClipboard(): Promise<string> {
    const event = await this.request('clipboard.read', {});
    return this.payloadString(event, 'text');
  }

  public on(type: string, handler: BridgeEventHandler): () => void {
    const handlers = this.handlers.get(type) ?? new Set<BridgeEventHandler>();
    handlers.add(handler);
    this.handlers.set(type, handlers);
    return () => handlers.delete(handler);
  }

  private readonly handleMessage = (messageEvent: MessageEvent<unknown>): void => {
    if (!this.isBridgeEvent(messageEvent.data)) {
      return;
    }

    const event = messageEvent.data;
    if (event.requestId) {
      const request = this.pending.get(event.requestId);
      if (request) {
        this.pending.delete(event.requestId);
        if (event.type === 'session.error') {
          request.reject(new Error(this.payloadString(event, 'message') || 'Native operation failed.'));
        } else {
          request.resolve(event);
        }
      }
    }

    this.handlers.get(event.type)?.forEach(handler => handler(event));
  };

  private request(type: string, payload: Record<string, unknown>): Promise<BridgeEvent> {
    const requestId = crypto.randomUUID();
    return new Promise<BridgeEvent>((resolve, reject) => {
      this.pending.set(requestId, { resolve, reject });
      this.send(type, payload, undefined, requestId);

      window.setTimeout(() => {
        if (this.pending.delete(requestId)) {
          reject(new Error(`Native request '${type}' timed out.`));
        }
      }, 15_000);
    });
  }

  private send(
    type: string,
    payload: Record<string, unknown>,
    sessionId?: string,
    requestId?: string
  ): void {
    window.chrome.webview.postMessage({
      version: 1,
      type,
      requestId,
      sessionId,
      payload
    });
  }

  private isBridgeEvent(value: unknown): value is BridgeEvent {
    if (typeof value !== 'object' || value === null) {
      return false;
    }

    const candidate = value as Partial<BridgeEvent>;
    return candidate.version === 1 &&
      typeof candidate.type === 'string' &&
      typeof candidate.payload === 'object' &&
      candidate.payload !== null;
  }

  private payloadString(event: BridgeEvent, name: string): string {
    const value = event.payload[name];
    return typeof value === 'string' ? value : '';
  }

  private payloadNumber(event: BridgeEvent, name: string): number {
    const value = event.payload[name];
    return typeof value === 'number' ? value : 0;
  }
}
