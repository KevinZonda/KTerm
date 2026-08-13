import { DEFAULT_SETTINGS } from './bridge';
import type { AppSettings, BridgeEvent, NativeBridge, SessionCreated } from './bridge';
import { TerminalController } from './terminal-controller';
import type { TerminalCallbacks } from './terminal-controller';
import { applyTerminalThemeToDocument } from './themes';

type SplitDirection = 'columns' | 'rows';
type SidebarMode = 'hidden' | 'peek' | 'expanded';

type LayoutNode =
  | { type: 'pane'; paneId: string }
  | {
      type: 'split';
      direction: SplitDirection;
      ratio: number;
      first: LayoutNode;
      second: LayoutNode;
    };

interface TerminalTabState {
  sessionId: string;
  title: string;
}

interface PaneState {
  id: string;
  tabs: TerminalTabState[];
  activeSessionId: string;
}

interface WorkspaceState {
  id: string;
  name: string;
  panes: Map<string, PaneState>;
  root?: LayoutNode;
  focusedPaneId?: string;
}

export class Workspace implements TerminalCallbacks {
  private static readonly EDGE_TRIGGER_WIDTH = 4;
  private static readonly MAX_WORKSPACE_NAME_LENGTH = 64;
  private static readonly PEEK_OPEN_DELAY = 100;
  private static readonly PEEK_CLOSE_DELAY = 250;

  private readonly bridge: NativeBridge;
  private readonly app: HTMLElement;
  private readonly workspace: HTMLElement;
  private readonly sidebar: HTMLElement;
  private readonly workspaceList: HTMLElement;
  private readonly status: HTMLElement;
  private readonly terminals = new Map<string, TerminalController>();
  private readonly earlyOutput = new Map<string, string[]>();
  private readonly closedSessionIds = new Set<string>();
  private readonly workspaces: WorkspaceState[] = [];
  private readonly paneElements = new Map<string, HTMLElement>();
  private activeWorkspaceId?: string;
  private editingWorkspaceId?: string;
  private nextWorkspaceNumber = 1;
  private sidebarMode: SidebarMode = 'hidden';
  private settings: AppSettings = structuredClone(DEFAULT_SETTINGS);
  private operationPending = false;
  private fontSaveTimer?: number;
  private peekOpenTimer?: number;
  private peekCloseTimer?: number;

  public constructor(bridge: NativeBridge) {
    this.bridge = bridge;
    this.app = this.requireElement('app');
    this.workspace = this.requireElement('workspace');
    this.sidebar = this.requireElement('workspace-sidebar');
    this.workspaceList = this.requireElement('workspace-list');
    this.status = this.requireElement('status');
    this.requireElement('new-workspace').addEventListener('click', () => void this.createWorkspace());
    this.sidebar.addEventListener('pointerenter', () => this.cancelPeekClose());
    this.sidebar.addEventListener('pointerleave', () => this.schedulePeekClose());
    this.sidebar.addEventListener('click', this.handleSidebarBackgroundClick);

    this.bridge.on('session.output', event => this.handleOutput(event));
    this.bridge.on('session.exited', event => this.handleExit(event));
    this.bridge.on('workspace.command', event => this.executeCommand(this.payloadString(event, 'command')));
    this.bridge.on('app.settingsChanged', event => this.applySettings(this.bridge.settingsFrom(event)));
    this.bridge.on('app.runtimeFailed', event => {
      this.setStatus(`WebView2 process failed: ${this.payloadString(event, 'kind')}`, true);
    });

    window.addEventListener('keydown', this.handleKeyboard, { capture: true });
    window.addEventListener('pointermove', this.handleEdgePointerMove, { passive: true });
    window.addEventListener('blur', this.handleWindowBlur);
  }

  public async initialize(): Promise<void> {
    this.setStatus('Starting KTerm…');
    this.applySettings(await this.bridge.ready());
    await this.createWorkspace();
    this.setStatus('');
  }

  public async createWorkspace(): Promise<void> {
    await this.runExclusive(() => this.createWorkspaceCore());
  }

  public async createTab(paneId = this.focusedPaneId): Promise<void> {
    await this.runExclusive(() => this.createTabInPane(paneId));
  }

  public async splitFocused(direction: SplitDirection): Promise<void> {
    const pane = this.focusedPane;
    const root = this.root;
    if (!pane || !root) {
      return;
    }

    await this.runExclusive(async () => {
      this.setStatus('Starting split shell…');
      const current = this.terminals.get(pane.activeSessionId);
      const session = await this.bridge.createSession(
        current?.element.clientWidth ? 40 : 80,
        24
      );
      this.addTerminal(session);

      const newPane: PaneState = {
        id: crypto.randomUUID(),
        tabs: [this.createTerminalTab(session)],
        activeSessionId: session.sessionId
      };
      this.panes.set(newPane.id, newPane);
      this.root = this.replacePaneLeaf(root, pane.id, {
        type: 'split',
        direction,
        ratio: 0.5,
        first: { type: 'pane', paneId: pane.id },
        second: { type: 'pane', paneId: newPane.id }
      });
      this.focusedPaneId = newPane.id;
      this.render();
      this.focusSession(session.sessionId);
      this.setStatus('');
    });
  }

  public onFocus(sessionId: string): void {
    const match = this.findWorkspacePaneBySession(sessionId);
    if (!match || match.workspace.id !== this.activeWorkspaceId) {
      return;
    }

    const { pane, workspace } = match;
    pane.activeSessionId = sessionId;
    workspace.focusedPaneId = pane.id;
    this.updateFocusState();
  }

  public onFontSizeChanged(sessionId: string, fontSize: number): void {
    if (this.fontSaveTimer !== undefined) {
      window.clearTimeout(this.fontSaveTimer);
      this.fontSaveTimer = undefined;
    }
    if (!this.isOnlyTerminal(sessionId)) {
      return;
    }

    this.fontSaveTimer = window.setTimeout(() => {
      this.fontSaveTimer = undefined;
      if (!this.isOnlyTerminal(sessionId)) {
        return;
      }

      void this.bridge.saveFontSize(fontSize)
        .then(settings => { this.settings = settings; })
        .catch(error => this.setStatus(`Unable to save font size: ${String(error)}`, true));
    }, 400);
  }

  public onTitle(sessionId: string, title: string): void {
    const match = this.findWorkspacePaneBySession(sessionId);
    const pane = match?.pane;
    const tab = pane?.tabs.find(candidate => candidate.sessionId === sessionId);
    if (!pane || !tab || !title.trim()) {
      return;
    }

    tab.title = title.trim();
    this.refreshPaneTabs(pane);
  }

  private readonly handleKeyboard = (event: KeyboardEvent): void => {
    if (event.repeat) {
      return;
    }

    if (event.target instanceof HTMLInputElement) {
      return;
    }

    if (event.code === 'F2' && !event.altKey && !event.ctrlKey &&
        !event.shiftKey && !event.metaKey && this.sidebarMode === 'expanded' &&
        this.activeWorkspaceId) {
      event.preventDefault();
      event.stopImmediatePropagation();
      this.startWorkspaceRename(this.activeWorkspaceId);
      return;
    }

    if (event.altKey && !event.ctrlKey && !event.shiftKey && !event.metaKey) {
      let handled = true;
      switch (event.code) {
        case 'KeyT':
          this.executeCommand('newTab');
          break;
        case 'Backslash':
          this.executeCommand('splitColumns');
          break;
        case 'Minus':
          this.executeCommand('splitRows');
          break;
        case 'KeyS':
          this.bridge.openSettings();
          break;
        case 'KeyB':
          this.executeCommand('toggleSidebar');
          break;
        case 'KeyN':
          this.executeCommand('newWorkspace');
          break;
        default:
          handled = false;
      }

      if (handled) {
        event.preventDefault();
        event.stopImmediatePropagation();
        return;
      }
    }

    if (event.ctrlKey && event.shiftKey && !event.altKey && !event.metaKey) {
      const terminal = this.focusedTerminal;
      if (event.code === 'KeyC' && terminal?.copySelection()) {
        event.preventDefault();
        event.stopImmediatePropagation();
      } else if (event.code === 'KeyV' && terminal) {
        event.preventDefault();
        event.stopImmediatePropagation();
        void this.bridge.readClipboard().then(text => terminal.paste(text));
      }
    }
  };

  private executeCommand(command: string): void {
    switch (command) {
      case 'newTab':
        void this.createTab();
        break;
      case 'splitColumns':
        void this.splitFocused('columns');
        break;
      case 'splitRows':
        void this.splitFocused('rows');
        break;
      case 'toggleSidebar':
        this.setSidebarMode(this.sidebarMode === 'expanded' ? 'hidden' : 'expanded');
        break;
      case 'newWorkspace':
        void this.createWorkspace();
        break;
    }
  }

  private async createWorkspaceCore(): Promise<void> {
    this.setStatus('Starting workspace…');
    const session = await this.bridge.createSession();
    this.addTerminal(session);

    const pane: PaneState = {
      id: crypto.randomUUID(),
      tabs: [this.createTerminalTab(session)],
      activeSessionId: session.sessionId
    };
    const workspace: WorkspaceState = {
      id: crypto.randomUUID(),
      name: `Workspace ${this.nextWorkspaceNumber++}`,
      panes: new Map([[pane.id, pane]]),
      root: { type: 'pane', paneId: pane.id },
      focusedPaneId: pane.id
    };

    this.workspaces.push(workspace);
    this.activeWorkspaceId = workspace.id;
    this.renderSidebar();
    this.render();
    this.focusSession(session.sessionId);
    this.setStatus('');
  }

  private activateWorkspace(workspaceId: string): void {
    if (this.operationPending || workspaceId === this.activeWorkspaceId ||
        !this.workspaces.some(workspace => workspace.id === workspaceId)) {
      return;
    }

    this.activeWorkspaceId = workspaceId;
    this.updateSidebarSelection();
    this.render();
    const focused = this.focusedPane;
    if (focused) {
      this.focusSession(focused.activeSessionId);
    }
  }

  private closeWorkspace(workspaceId: string): void {
    void this.runExclusive(async () => {
      const index = this.workspaces.findIndex(workspace => workspace.id === workspaceId);
      if (index < 0) {
        return;
      }

      const workspace = this.workspaces[index]!;
      if (this.editingWorkspaceId === workspace.id) {
        this.editingWorkspaceId = undefined;
      }
      const wasActive = workspace.id === this.activeWorkspaceId;
      for (const pane of workspace.panes.values()) {
        for (const tab of pane.tabs) {
          this.destroyTerminal(tab.sessionId);
          this.bridge.closeSession(tab.sessionId);
        }
      }

      this.workspaces.splice(index, 1);
      if (wasActive) {
        this.activeWorkspaceId = this.workspaces[Math.min(index, this.workspaces.length - 1)]?.id;
      }

      if (this.workspaces.length === 0) {
        this.renderSidebar();
        this.render();
        await this.createWorkspaceCore();
        return;
      }

      this.renderSidebar();
      if (wasActive) {
        this.render();
        const focused = this.focusedPane;
        if (focused) {
          this.focusSession(focused.activeSessionId);
        }
      }
    });
  }

  private setSidebarMode(mode: SidebarMode): void {
    if (mode === this.sidebarMode) {
      return;
    }

    const layoutChanged = mode === 'expanded' || this.sidebarMode === 'expanded';
    this.cancelPeekOpen();
    this.cancelPeekClose();
    this.sidebarMode = mode;
    this.app.classList.toggle('sidebar-peek', mode === 'peek');
    this.app.classList.toggle('sidebar-visible', mode === 'expanded');
    this.sidebar.setAttribute('aria-hidden', String(mode === 'hidden'));
    this.renderSidebar();
    if (layoutChanged) {
      window.requestAnimationFrame(() => this.fitVisibleTerminals());
    }
  }

  private renderSidebar(): void {
    const fragment = document.createDocumentFragment();
    for (const workspace of this.workspaces) {
      const item = document.createElement('div');
      item.className = 'workspace-item';
      item.dataset.workspaceId = workspace.id;
      item.classList.toggle('active', workspace.id === this.activeWorkspaceId);

      if (this.sidebarMode === 'expanded' && workspace.id === this.editingWorkspaceId) {
        item.append(this.createWorkspaceNameEditor(workspace));
      } else {
        const activate = document.createElement('button');
        activate.type = 'button';
        activate.className = 'workspace-activate';
        activate.textContent = this.sidebarMode === 'peek' ? '' : workspace.name;
        activate.title = workspace.name;
        activate.setAttribute('aria-label', workspace.name);
        activate.addEventListener('click', () => this.activateWorkspace(workspace.id));
        if (this.sidebarMode === 'expanded') {
          activate.addEventListener('dblclick', event => {
            event.preventDefault();
            this.startWorkspaceRename(workspace.id);
          });
        }
        item.append(activate);
      }

      if (this.sidebarMode === 'expanded') {
        const close = document.createElement('button');
        close.type = 'button';
        close.className = 'workspace-close';
        close.textContent = '×';
        close.title = `Close ${workspace.name}`;
        close.setAttribute('aria-label', `Close ${workspace.name}`);
        close.addEventListener('click', event => {
          event.stopPropagation();
          this.closeWorkspace(workspace.id);
        });
        item.append(close);
      }
      fragment.append(item);
    }
    this.workspaceList.replaceChildren(fragment);
  }

  private updateSidebarSelection(): void {
    this.workspaceList.querySelectorAll<HTMLElement>('.workspace-item').forEach(item => {
      item.classList.toggle('active', item.dataset.workspaceId === this.activeWorkspaceId);
    });
  }

  private startWorkspaceRename(workspaceId: string): void {
    if (this.sidebarMode !== 'expanded' ||
        !this.workspaces.some(workspace => workspace.id === workspaceId)) {
      return;
    }

    this.editingWorkspaceId = workspaceId;
    this.renderSidebar();
    window.requestAnimationFrame(() => {
      const editor = this.workspaceList.querySelector<HTMLInputElement>(
        `.workspace-name-editor[data-workspace-id="${workspaceId}"]`
      );
      editor?.focus();
      editor?.select();
    });
  }

  private createWorkspaceNameEditor(workspace: WorkspaceState): HTMLInputElement {
    const editor = document.createElement('input');
    editor.type = 'text';
    editor.className = 'workspace-name-editor';
    editor.dataset.workspaceId = workspace.id;
    editor.value = workspace.name;
    editor.maxLength = Workspace.MAX_WORKSPACE_NAME_LENGTH;
    editor.setAttribute('aria-label', `Rename ${workspace.name}`);

    let finished = false;
    const finish = (save: boolean): void => {
      if (finished) {
        return;
      }
      finished = true;

      if (save) {
        const name = editor.value.trim();
        if (name) {
          workspace.name = name;
        }
      }

      if (this.editingWorkspaceId === workspace.id) {
        this.editingWorkspaceId = undefined;
      }
      this.renderSidebar();
    };

    editor.addEventListener('keydown', event => {
      event.stopPropagation();
      if (event.key === 'Enter') {
        event.preventDefault();
        finish(true);
      } else if (event.key === 'Escape') {
        event.preventDefault();
        finish(false);
      }
    });
    editor.addEventListener('blur', () => window.setTimeout(() => finish(true), 0));
    return editor;
  }

  private readonly handleEdgePointerMove = (event: PointerEvent): void => {
    if (this.sidebarMode !== 'hidden') {
      return;
    }

    if (event.buttons !== 0 || event.clientX > Workspace.EDGE_TRIGGER_WIDTH) {
      this.cancelPeekOpen();
      return;
    }

    if (this.peekOpenTimer === undefined) {
      this.peekOpenTimer = window.setTimeout(() => {
        this.peekOpenTimer = undefined;
        if (this.sidebarMode === 'hidden') {
          this.setSidebarMode('peek');
        }
      }, Workspace.PEEK_OPEN_DELAY);
    }
  };

  private readonly handleSidebarBackgroundClick = (event: MouseEvent): void => {
    if (this.sidebarMode === 'peek' &&
        (event.target === this.sidebar || event.target === this.workspaceList)) {
      this.setSidebarMode('expanded');
    }
  };

  private readonly handleWindowBlur = (): void => {
    this.cancelPeekOpen();
    if (this.sidebarMode === 'peek') {
      this.setSidebarMode('hidden');
    }
  };

  private schedulePeekClose(): void {
    if (this.sidebarMode !== 'peek' || this.peekCloseTimer !== undefined) {
      return;
    }

    this.peekCloseTimer = window.setTimeout(() => {
      this.peekCloseTimer = undefined;
      if (this.sidebarMode === 'peek') {
        this.setSidebarMode('hidden');
      }
    }, Workspace.PEEK_CLOSE_DELAY);
  }

  private cancelPeekOpen(): void {
    if (this.peekOpenTimer !== undefined) {
      window.clearTimeout(this.peekOpenTimer);
      this.peekOpenTimer = undefined;
    }
  }

  private cancelPeekClose(): void {
    if (this.peekCloseTimer !== undefined) {
      window.clearTimeout(this.peekCloseTimer);
      this.peekCloseTimer = undefined;
    }
  }

  private async createTabInPane(paneId?: string): Promise<void> {
    this.setStatus('Starting shell…');
    const session = await this.bridge.createSession();
    this.addTerminal(session);

    let pane = paneId ? this.panes.get(paneId) : this.focusedPane;
    if (!pane) {
      pane = {
        id: crypto.randomUUID(),
        tabs: [],
        activeSessionId: session.sessionId
      };
      this.panes.set(pane.id, pane);
      this.root = { type: 'pane', paneId: pane.id };
    }

    pane.tabs.push(this.createTerminalTab(session));
    pane.activeSessionId = session.sessionId;
    this.focusedPaneId = pane.id;
    this.render();
    this.focusSession(session.sessionId);
    this.setStatus('');
  }

  private createTerminalTab(session: SessionCreated): TerminalTabState {
    return {
      sessionId: session.sessionId,
      title: session.shellName
    };
  }

  private addTerminal(session: SessionCreated): void {
    const terminal = new TerminalController(
      session,
      this.bridge,
      this,
      this.settings.font,
      this.settings.theme
    );
    this.closedSessionIds.delete(session.sessionId);
    this.terminals.set(session.sessionId, terminal);

    const pending = this.earlyOutput.get(session.sessionId);
    if (pending) {
      pending.forEach(data => terminal.write(data));
      this.earlyOutput.delete(session.sessionId);
    }
  }

  private isOnlyTerminal(sessionId: string): boolean {
    if (this.terminals.size !== 1 || this.panes.size !== 1) {
      return false;
    }

    const pane = this.panes.values().next().value as PaneState | undefined;
    return pane?.tabs.length === 1 && pane.activeSessionId === sessionId;
  }

  private applySettings(settings: AppSettings): void {
    this.settings = settings;
    applyTerminalThemeToDocument(settings.theme.name);
    this.terminals.forEach(terminal => {
      terminal.applyFontSettings(settings.font);
      terminal.applyThemeSettings(settings.theme);
    });
  }

  private handleOutput(event: BridgeEvent): void {
    if (!event.sessionId || this.closedSessionIds.has(event.sessionId)) {
      return;
    }

    const data = this.payloadString(event, 'data');
    const terminal = this.terminals.get(event.sessionId);
    if (terminal) {
      terminal.write(data);
    } else {
      const pending = this.earlyOutput.get(event.sessionId) ?? [];
      pending.push(data);
      this.earlyOutput.set(event.sessionId, pending);
    }
  }

  private handleExit(event: BridgeEvent): void {
    if (event.sessionId) {
      this.terminals.get(event.sessionId)?.markExited(this.payloadNumber(event, 'exitCode'));
    }
  }

  private render(): void {
    this.paneElements.clear();
    if (!this.root) {
      this.workspace.replaceChildren(this.emptyState());
      return;
    }

    this.workspace.replaceChildren(this.renderNode(this.root));
    this.updateFocusState();

    for (const paneId of this.collectPaneIds(this.root)) {
      const pane = this.panes.get(paneId);
      if (pane) {
        this.terminals.get(pane.activeSessionId)?.mount();
      }
    }
  }

  private renderNode(node: LayoutNode): HTMLElement {
    if (node.type === 'pane') {
      const pane = this.panes.get(node.paneId);
      return pane ? this.renderPane(pane) : this.missingPane();
    }

    const split = document.createElement('div');
    split.className = `split split-${node.direction}`;
    const first = this.renderNode(node.first);
    const divider = document.createElement('div');
    divider.className = 'split-divider';
    divider.setAttribute('role', 'separator');
    const second = this.renderNode(node.second);
    split.append(first, divider, second);

    const applyRatio = (): void => {
      if (node.direction === 'columns') {
        split.style.gridTemplateColumns = `${node.ratio}fr 5px ${1 - node.ratio}fr`;
      } else {
        split.style.gridTemplateRows = `${node.ratio}fr 5px ${1 - node.ratio}fr`;
      }
    };
    applyRatio();

    divider.addEventListener('pointerdown', event => {
      event.preventDefault();
      divider.setPointerCapture(event.pointerId);
      divider.classList.add('dragging');
    });
    divider.addEventListener('pointermove', event => {
      if (!divider.hasPointerCapture(event.pointerId)) {
        return;
      }
      const rect = split.getBoundingClientRect();
      const ratio = node.direction === 'columns'
        ? (event.clientX - rect.left) / rect.width
        : (event.clientY - rect.top) / rect.height;
      node.ratio = Math.min(0.9, Math.max(0.1, ratio));
      applyRatio();
    });
    const finishDrag = (event: PointerEvent): void => {
      if (divider.hasPointerCapture(event.pointerId)) {
        divider.releasePointerCapture(event.pointerId);
      }
      divider.classList.remove('dragging');
      this.fitVisibleTerminals();
    };
    divider.addEventListener('pointerup', finishDrag);
    divider.addEventListener('pointercancel', finishDrag);
    return split;
  }

  private renderPane(pane: PaneState): HTMLElement {
    const element = document.createElement('section');
    element.className = 'pane';
    element.classList.toggle('compact', this.panes.size === 1 && pane.tabs.length === 1);
    element.dataset.paneId = pane.id;
    element.addEventListener('pointerdown', () => {
      this.focusedPaneId = pane.id;
      this.updateFocusState();
    });

    const tabStrip = document.createElement('header');
    tabStrip.className = 'pane-tab-strip';
    tabStrip.setAttribute('aria-label', 'Pane terminal tabs');
    element.append(tabStrip);
    this.renderPaneTabs(pane, tabStrip);

    const content = document.createElement('div');
    content.className = 'pane-content';
    const terminal = this.terminals.get(pane.activeSessionId);
    content.append(terminal?.element ?? this.missingTerminal());
    element.append(content);

    this.paneElements.set(pane.id, element);
    return element;
  }

  private renderPaneTabs(pane: PaneState, tabStrip: HTMLElement): void {
    const fragment = document.createDocumentFragment();
    for (const tab of pane.tabs) {
      const tabElement = document.createElement('div');
      tabElement.className = 'pane-tab';
      tabElement.classList.toggle('active', tab.sessionId === pane.activeSessionId);
      tabElement.addEventListener('pointerdown', event => {
        if (event.button === 1) {
          event.preventDefault();
        }
      });
      tabElement.addEventListener('auxclick', event => {
        if (event.button !== 1) {
          return;
        }

        event.preventDefault();
        event.stopPropagation();
        this.closeTerminalTab(pane.id, tab.sessionId);
      });

      const activate = document.createElement('button');
      activate.type = 'button';
      activate.className = 'pane-tab-activate';
      activate.title = tab.title;
      activate.textContent = tab.title || 'Terminal';
      activate.addEventListener('click', () => this.activateTab(pane.id, tab.sessionId));

      const close = document.createElement('button');
      close.type = 'button';
      close.className = 'pane-tab-close';
      close.title = 'Close tab';
      close.setAttribute('aria-label', `Close ${tab.title}`);
      close.textContent = '×';
      close.addEventListener('click', event => {
        event.stopPropagation();
        this.closeTerminalTab(pane.id, tab.sessionId);
      });
      tabElement.append(activate, close);
      fragment.append(tabElement);
    }

    const add = document.createElement('button');
    add.type = 'button';
    add.className = 'pane-new-tab';
    add.title = 'New tab in this pane (Alt+T)';
    add.setAttribute('aria-label', 'New terminal tab in this pane');
    add.textContent = '+';
    add.addEventListener('click', () => void this.createTab(pane.id));
    fragment.append(add);
    tabStrip.replaceChildren(fragment);
  }

  private refreshPaneTabs(pane: PaneState): void {
    const tabStrip = this.paneElements.get(pane.id)?.querySelector<HTMLElement>('.pane-tab-strip');
    if (tabStrip) {
      this.renderPaneTabs(pane, tabStrip);
    }
  }

  private activateTab(paneId: string, sessionId: string): void {
    const pane = this.panes.get(paneId);
    if (!pane || !pane.tabs.some(tab => tab.sessionId === sessionId)) {
      return;
    }

    pane.activeSessionId = sessionId;
    this.focusedPaneId = pane.id;
    this.render();
    this.focusSession(sessionId);
  }

  private closeTerminalTab(paneId: string, sessionId: string): void {
    void this.runExclusive(async () => {
      const pane = this.panes.get(paneId);
      const index = pane?.tabs.findIndex(tab => tab.sessionId === sessionId) ?? -1;
      if (!pane || index < 0) {
        return;
      }

      const wasActive = pane.activeSessionId === sessionId;
      pane.tabs.splice(index, 1);
      this.destroyTerminal(sessionId);
      this.bridge.closeSession(sessionId);

      if (pane.tabs.length > 0) {
        if (wasActive) {
          pane.activeSessionId = pane.tabs[Math.min(index, pane.tabs.length - 1)]!.sessionId;
        }
        this.focusedPaneId = pane.id;
        this.render();
        this.focusSession(pane.activeSessionId);
        return;
      }

      const nextPaneId = this.root
        ? this.findClosestSiblingPaneId(this.root, pane.id)
        : undefined;
      this.panes.delete(pane.id);
      this.root = this.root ? this.removePaneLeaf(this.root, pane.id) ?? undefined : undefined;
      this.focusedPaneId = nextPaneId ?? (this.root ? this.firstPaneId(this.root) : undefined);
      if (!this.root) {
        await this.createTabInPane();
        return;
      }

      this.render();
      const focused = this.focusedPane;
      if (focused) {
        this.focusSession(focused.activeSessionId);
      }
    });
  }

  private destroyTerminal(sessionId: string): void {
    this.terminals.get(sessionId)?.dispose();
    this.terminals.delete(sessionId);
    this.earlyOutput.delete(sessionId);
    this.closedSessionIds.add(sessionId);
  }

  private focusSession(sessionId: string): void {
    this.terminals.get(sessionId)?.focus();
  }

  private updateFocusState(): void {
    this.paneElements.forEach((element, paneId) => {
      element.classList.toggle('focused', paneId === this.focusedPaneId);
    });

    const focusedSessionId = this.focusedPane?.activeSessionId;
    this.terminals.forEach((terminal, sessionId) => {
      terminal.setFocused(sessionId === focusedSessionId);
    });
  }

  private fitVisibleTerminals(): void {
    this.activeWorkspace?.panes.forEach(pane => {
      this.terminals.get(pane.activeSessionId)?.scheduleFit();
    });
  }

  private replacePaneLeaf(node: LayoutNode, paneId: string, replacement: LayoutNode): LayoutNode {
    if (node.type === 'pane') {
      return node.paneId === paneId ? replacement : node;
    }
    return {
      ...node,
      first: this.replacePaneLeaf(node.first, paneId, replacement),
      second: this.replacePaneLeaf(node.second, paneId, replacement)
    };
  }

  private removePaneLeaf(node: LayoutNode, paneId: string): LayoutNode | null {
    if (node.type === 'pane') {
      return node.paneId === paneId ? null : node;
    }

    const first = this.removePaneLeaf(node.first, paneId);
    const second = this.removePaneLeaf(node.second, paneId);
    if (!first) {
      return second;
    }
    if (!second) {
      return first;
    }
    return { ...node, first, second };
  }

  private collectPaneIds(node: LayoutNode): string[] {
    return node.type === 'pane'
      ? [node.paneId]
      : [...this.collectPaneIds(node.first), ...this.collectPaneIds(node.second)];
  }

  private firstPaneId(node: LayoutNode): string {
    return node.type === 'pane' ? node.paneId : this.firstPaneId(node.first);
  }

  private findClosestSiblingPaneId(node: LayoutNode, paneId: string): string | undefined {
    if (node.type === 'pane') {
      return undefined;
    }

    if (this.containsPane(node.first, paneId)) {
      return this.findClosestSiblingPaneId(node.first, paneId) ?? this.firstPaneId(node.second);
    }
    if (this.containsPane(node.second, paneId)) {
      return this.findClosestSiblingPaneId(node.second, paneId) ?? this.firstPaneId(node.first);
    }
    return undefined;
  }

  private containsPane(node: LayoutNode, paneId: string): boolean {
    return node.type === 'pane'
      ? node.paneId === paneId
      : this.containsPane(node.first, paneId) || this.containsPane(node.second, paneId);
  }

  private findWorkspacePaneBySession(
    sessionId: string
  ): { workspace: WorkspaceState; pane: PaneState } | undefined {
    for (const workspace of this.workspaces) {
      for (const pane of workspace.panes.values()) {
        if (pane.tabs.some(tab => tab.sessionId === sessionId)) {
          return { workspace, pane };
        }
      }
    }
    return undefined;
  }

  private async runExclusive(operation: () => Promise<void>): Promise<void> {
    if (this.operationPending) {
      return;
    }
    this.operationPending = true;
    try {
      await operation();
    } catch (error) {
      this.setStatus(error instanceof Error ? error.message : String(error), true);
    } finally {
      this.operationPending = false;
    }
  }

  private emptyState(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'empty-state';
    element.textContent = 'No terminal panes are open.';
    return element;
  }

  private missingPane(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'pane-missing';
    element.textContent = 'Terminal pane is unavailable.';
    return element;
  }

  private missingTerminal(): HTMLElement {
    const element = document.createElement('div');
    element.className = 'terminal-missing';
    element.textContent = 'Terminal session is unavailable.';
    return element;
  }

  private requireElement(id: string): HTMLElement {
    const element = document.getElementById(id);
    if (!element) {
      throw new Error(`Missing application element '#${id}'.`);
    }
    return element;
  }

  private setStatus(message: string, error = false): void {
    this.status.textContent = message;
    this.status.classList.toggle('visible', Boolean(message));
    this.status.classList.toggle('error', error);
  }

  private payloadString(event: BridgeEvent, name: string): string {
    const value = event.payload[name];
    return typeof value === 'string' ? value : '';
  }

  private payloadNumber(event: BridgeEvent, name: string): number {
    const value = event.payload[name];
    return typeof value === 'number' ? value : 0;
  }

  private get focusedPane(): PaneState | undefined {
    return this.focusedPaneId ? this.panes.get(this.focusedPaneId) : undefined;
  }

  private get focusedTerminal(): TerminalController | undefined {
    const pane = this.focusedPane;
    return pane ? this.terminals.get(pane.activeSessionId) : undefined;
  }

  private get activeWorkspace(): WorkspaceState | undefined {
    return this.activeWorkspaceId
      ? this.workspaces.find(workspace => workspace.id === this.activeWorkspaceId)
      : undefined;
  }

  private get panes(): Map<string, PaneState> {
    const workspace = this.activeWorkspace;
    if (!workspace) {
      throw new Error('No active workspace is available.');
    }
    return workspace.panes;
  }

  private get root(): LayoutNode | undefined {
    return this.activeWorkspace?.root;
  }

  private set root(root: LayoutNode | undefined) {
    const workspace = this.activeWorkspace;
    if (!workspace) {
      throw new Error('No active workspace is available.');
    }
    workspace.root = root;
  }

  private get focusedPaneId(): string | undefined {
    return this.activeWorkspace?.focusedPaneId;
  }

  private set focusedPaneId(paneId: string | undefined) {
    const workspace = this.activeWorkspace;
    if (!workspace) {
      throw new Error('No active workspace is available.');
    }
    workspace.focusedPaneId = paneId;
  }
}
