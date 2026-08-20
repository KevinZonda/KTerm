import { FitAddon } from '@xterm/addon-fit';
import { WebLinksAddon } from '@xterm/addon-web-links';
import { WebglAddon } from '@xterm/addon-webgl';
import { Terminal } from '@xterm/xterm';
import type { IDisposable } from '@xterm/xterm';
import type { FontSettings, NativeBridge, SessionCreated, ThemeSettings } from './bridge';
import { resolveTerminalTheme } from './themes';

export interface TerminalCallbacks {
  onFocus(sessionId: string): void;
  onFontSizeChanged(sessionId: string, fontSize: number): void;
  onTitle(sessionId: string, title: string): void;
}

export class TerminalController {
  private static readonly MIN_FONT_SIZE = 8;
  private static readonly MAX_FONT_SIZE = 72;
  // Chromium reports pixel wheel deltas; 40px per line matches the normal
  // buffer scroll feel (a typical 120px notch scrolls 3 lines).
  private static readonly ALT_SCROLL_PIXELS_PER_LINE = 40;
  // Grace period before a hidden pane's WebGL context is reclaimed.
  private static readonly WEBGL_RECLAIM_DELAY_MS = 30_000;

  public readonly sessionId: string;
  public readonly element: HTMLDivElement;

  private readonly bridge: NativeBridge;
  private readonly callbacks: TerminalCallbacks;
  private readonly terminal: Terminal;
  private readonly fitAddon = new FitAddon();
  private readonly host = document.createElement('div');
  private readonly resizeObserver: ResizeObserver;
  private readonly disposables: IDisposable[] = [];
  // Writes go straight to the parser even before open(): query answers (DA,
  // DSR) must return while the app is still waiting, or they land at the shell
  // prompt as garbage when the answer finally arrives. xterm.js parses fine
  // headless; only OSC color reports are skipped pre-open (its theme service
  // does not exist yet), which apps handle with a timeout fallback.
  private webglAddon?: WebglAddon;
  private webglFailed = false;
  private webglReclaimTimer?: number;
  private opened = false;
  private exited = false;
  private fitTimer?: number;
  private lastCols = 0;
  private lastRows = 0;
  private altScrollRemainder = 0;
  private altScrollWasAltBuffer = false;

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
      linkHandler: {
        activate: (_event, uri) => this.bridge.openExternal(uri)
      },
      scrollback: 5000,
      theme: resolveTerminalTheme(theme.name),
      // We always sit behind ConPTY (OpenConsole passthrough), so adopt its
      // buffer semantics on resize instead of vanilla xterm behavior:
      // growing rows pads empty lines at the bottom of the viewport rather
      // than pulling scrollback back in, and a buildNumber below 21376
      // disables xterm's own reflow so the screen always follows the pty's
      // repaint instead of a second, diverging reflow.
      windowsPty: { backend: 'conpty', buildNumber: 19045 }
    });
    this.terminal.loadAddon(this.fitAddon);
    this.terminal.loadAddon(new WebLinksAddon((_event, uri) => this.bridge.openExternal(uri)));

    this.disposables.push(
      this.terminal.onData(data => this.bridge.sendInput(this.sessionId, data)),
      this.terminal.onBinary(data => this.bridge.sendBinaryInput(this.sessionId, data)),
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

  // Keeps the GPU renderer aligned with visibility: panes that stay hidden for
  // a while release their WebGL context (xterm.js seamlessly uses its built-in
  // renderer), visible panes get one back. Reclaiming is deferred so quick tab
  // switches don't churn context creation.
  public setVisible(visible: boolean): void {
    if (!this.opened) {
      return;
    }

    if (visible) {
      this.cancelWebglReclaim();
      if (!this.webglAddon && !this.webglFailed) {
        this.enableWebgl();
      }
      return;
    }

    if (this.webglAddon && this.webglReclaimTimer === undefined) {
      this.webglReclaimTimer = window.setTimeout(() => {
        this.webglReclaimTimer = undefined;
        this.disposeWebgl();
      }, TerminalController.WEBGL_RECLAIM_DELAY_MS);
    }
  }

  private cancelWebglReclaim(): void {
    if (this.webglReclaimTimer !== undefined) {
      window.clearTimeout(this.webglReclaimTimer);
      this.webglReclaimTimer = undefined;
    }
  }

  private disposeWebgl(): void {
    if (!this.webglAddon) {
      return;
    }

    this.webglAddon.dispose();
    this.webglAddon = undefined;
    this.element.classList.remove('renderer-webgl');
    this.element.classList.add('renderer-fallback');
  }

  public write(data: string): void {
    this.terminal.write(data);
  }

  public markExited(exitCode: number, failure?: string): void {
    if (this.exited) {
      return;
    }

    this.exited = true;
    this.element.classList.add('exited');
    const message = failure ?? `process exited with code ${exitCode}`;
    const color = failure ? '\x1b[91m' : '\x1b[90m';
    this.write(`\r\n${color}[${message}]\x1b[0m\r\n`);
  }

  public focus(): void {
    this.callbacks.onFocus(this.sessionId);
    this.terminal.focus();
  }

  public setFocused(focused: boolean): void {
    this.element.classList.toggle('focused', focused);
  }

  public get cols(): number {
    return this.terminal.cols;
  }

  public get rows(): number {
    return this.terminal.rows;
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

  public fitImmediately(): void {
    this.fitNow();
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
    this.cancelWebglReclaim();
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
      this.webglFailed = true;
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
    if (event.deltaY === 0) {
      return;
    }

    if (event.ctrlKey) {
      this.zoomFont(event);
      return;
    }

    // The alternate buffer has no scrollback and xterm.js does not translate
    // the wheel into input, so without mouse reporting the wheel would be a
    // no-op. Send arrow keys like Windows Terminal does, letting fullscreen
    // apps such as codex scroll their own transcript.
    const isAltBuffer = this.terminal.buffer.active.type === 'alternate';
    if (isAltBuffer !== this.altScrollWasAltBuffer) {
      // Don't leak a fractional remainder across a buffer switch.
      this.altScrollWasAltBuffer = isAltBuffer;
      this.altScrollRemainder = 0;
    }
    if (this.terminal.modes.mouseTrackingMode !== 'none' || !isAltBuffer) {
      return;
    }

    const lines = this.consumeAltScrollDelta(event);
    if (lines === 0) {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();

    const applicationMode = this.terminal.modes.applicationCursorKeysMode;
    const sequence = lines < 0
      ? (applicationMode ? '\x1bOA' : '\x1b[A')
      : (applicationMode ? '\x1bOB' : '\x1b[B');
    this.bridge.sendInput(this.sessionId, sequence.repeat(Math.abs(lines)));
  };

  private consumeAltScrollDelta(event: WheelEvent): number {
    let delta: number;
    if (event.deltaMode === WheelEvent.DOM_DELTA_LINE) {
      delta = event.deltaY;
    } else if (event.deltaMode === WheelEvent.DOM_DELTA_PAGE) {
      delta = event.deltaY * this.terminal.rows;
    } else {
      delta = event.deltaY / TerminalController.ALT_SCROLL_PIXELS_PER_LINE;
    }

    if (Math.sign(delta) !== Math.sign(this.altScrollRemainder)) {
      this.altScrollRemainder = 0;
    }
    this.altScrollRemainder += delta;

    const lines = Math.trunc(this.altScrollRemainder);
    this.altScrollRemainder -= lines;
    return lines;
  }

  private zoomFont(event: WheelEvent): void {
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
    this.callbacks.onFontSizeChanged(this.sessionId, nextSize);
  }

}
