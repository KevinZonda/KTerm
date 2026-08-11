import { FitAddon } from '@xterm/addon-fit';
import { WebglAddon } from '@xterm/addon-webgl';
import { Terminal } from '@xterm/xterm';
import type { IDisposable } from '@xterm/xterm';
import type { FontSettings, NativeBridge, SessionCreated, ThemeSettings } from './bridge';
import { resolveTerminalTheme } from './themes';

export interface TerminalCallbacks {
  onFocus(sessionId: string): void;
  onTitle(sessionId: string, title: string): void;
}

export class TerminalController {
  private static readonly MIN_FONT_SIZE = 8;
  private static readonly MAX_FONT_SIZE = 72;

  public readonly sessionId: string;
  public readonly element: HTMLDivElement;

  private readonly bridge: NativeBridge;
  private readonly callbacks: TerminalCallbacks;
  private readonly terminal: Terminal;
  private readonly fitAddon = new FitAddon();
  private readonly host = document.createElement('div');
  private readonly resizeObserver: ResizeObserver;
  private readonly disposables: IDisposable[] = [];
  private webglAddon?: WebglAddon;
  private opened = false;
  private exited = false;
  private fitTimer?: number;
  private lastCols = 0;
  private lastRows = 0;

  public constructor(
    session: SessionCreated,
    bridge: NativeBridge,
    callbacks: TerminalCallbacks,
    font: FontSettings,
    theme: ThemeSettings
  ) {
    this.sessionId = session.sessionId;
    this.bridge = bridge;
    this.callbacks = callbacks;
    this.element = document.createElement('div');
    this.element.className = 'terminal-pane';
    this.element.dataset.sessionId = session.sessionId;
    this.element.title = `${session.shellName} · PID ${session.processId}`;

    this.host.className = 'terminal-host';
    this.element.append(this.host);

    this.terminal = new Terminal({
      allowProposedApi: false,
      convertEol: false,
      cursorBlink: true,
      cursorStyle: 'bar',
      fontFamily: font.family,
      fontSize: font.size,
      lineHeight: font.lineHeight,
      scrollback: 5000,
      theme: resolveTerminalTheme(theme.name)
    });
    this.terminal.loadAddon(this.fitAddon);

    this.disposables.push(
      this.terminal.onData(data => this.bridge.sendInput(this.sessionId, data)),
      this.terminal.onTitleChange(title => this.callbacks.onTitle(this.sessionId, title)),
      this.terminal.onResize(size => {
        if (size.cols === this.lastCols && size.rows === this.lastRows) {
          return;
        }

        this.lastCols = size.cols;
        this.lastRows = size.rows;
        this.bridge.resize(this.sessionId, size.cols, size.rows);
      })
    );

    this.element.addEventListener('pointerdown', () => this.focus());
    this.element.addEventListener('focusin', () => this.callbacks.onFocus(this.sessionId));
    this.host.addEventListener('contextmenu', this.handleContextMenu, { capture: true });
    this.host.addEventListener('wheel', this.handleWheel, { capture: true, passive: false });
    this.resizeObserver = new ResizeObserver(() => this.scheduleFit());
    this.resizeObserver.observe(this.element);
  }

  public mount(): void {
    if (!this.element.isConnected) {
      return;
    }

    if (!this.opened) {
      this.terminal.open(this.host);
      this.opened = true;
      if (!this.fitNow()) {
        this.scheduleFit();
      }
      this.enableWebgl();
      return;
    }

    this.scheduleFit();
  }

  public write(data: string): void {
    this.terminal.write(data);
  }

  public markExited(exitCode: number): void {
    if (this.exited) {
      return;
    }

    this.exited = true;
    this.element.classList.add('exited');
    this.terminal.write(`\r\n\x1b[90m[process exited with code ${exitCode}]\x1b[0m\r\n`);
  }

  public focus(): void {
    this.callbacks.onFocus(this.sessionId);
    this.terminal.focus();
  }

  public setFocused(focused: boolean): void {
    this.element.classList.toggle('focused', focused);
  }

  public applyFontSettings(font: FontSettings): void {
    this.terminal.options.fontFamily = font.family;
    this.terminal.options.fontSize = font.size;
    this.terminal.options.lineHeight = font.lineHeight;
    if (this.opened) {
      this.scheduleFit();
    }
  }

  public applyThemeSettings(theme: ThemeSettings): void {
    this.terminal.options.theme = resolveTerminalTheme(theme.name);
  }

  public scheduleFit(): void {
    if (this.fitTimer !== undefined) {
      window.clearTimeout(this.fitTimer);
    }

    this.fitTimer = window.setTimeout(() => {
      this.fitNow();
    }, 40);
  }

  public copySelection(): boolean {
    if (!this.terminal.hasSelection()) {
      return false;
    }

    this.bridge.writeClipboard(this.terminal.getSelection());
    return true;
  }

  public paste(text: string): void {
    if (text) {
      this.terminal.paste(text);
    }
  }

  public dispose(): void {
    if (this.fitTimer !== undefined) {
      window.clearTimeout(this.fitTimer);
    }
    this.host.removeEventListener('wheel', this.handleWheel, { capture: true });
    this.resizeObserver.disconnect();
    this.disposables.forEach(disposable => disposable.dispose());
    this.webglAddon?.dispose();
    this.terminal.dispose();
    this.element.remove();
  }

  private enableWebgl(): void {
    try {
      const addon = new WebglAddon();
      addon.onContextLoss(() => {
        addon.dispose();
        if (this.webglAddon === addon) {
          this.webglAddon = undefined;
        }
        this.element.classList.add('renderer-fallback');
      });
      this.terminal.loadAddon(addon);
      this.webglAddon = addon;
      this.element.classList.add('renderer-webgl');
    } catch {
      this.element.classList.add('renderer-fallback');
    }
  }

  private fitNow(): boolean {
    if (this.fitTimer !== undefined) {
      window.clearTimeout(this.fitTimer);
      this.fitTimer = undefined;
    }
    if (!this.opened || !this.element.isConnected ||
        this.element.clientWidth < 20 || this.element.clientHeight < 20) {
      return false;
    }

    try {
      this.fitAddon.fit();
      return true;
    } catch {
      // A detached/transitioning pane will be fitted on its next ResizeObserver event.
      return false;
    }
  }

  private readonly handleContextMenu = (event: MouseEvent): void => {
    if (this.copySelection()) {
      event.preventDefault();
      event.stopImmediatePropagation();
      this.terminal.clearSelection();
      return;
    }

    if (this.terminal.modes.mouseTrackingMode !== 'none') {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    this.focus();
    void this.bridge.readClipboard()
      .then(text => this.paste(text))
      .catch(error => console.error('Unable to paste clipboard text.', error));
  };

  private readonly handleWheel = (event: WheelEvent): void => {
    if (!event.ctrlKey || event.deltaY === 0) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();

    const currentSize = this.terminal.options.fontSize ?? 14;
    const nextSize = Math.min(
      TerminalController.MAX_FONT_SIZE,
      Math.max(
        TerminalController.MIN_FONT_SIZE,
        currentSize + (event.deltaY < 0 ? 1 : -1)
      )
    );
    if (nextSize === currentSize) {
      return;
    }

    this.terminal.options.fontSize = nextSize;
    this.scheduleFit();
    this.focus();
  };

}
