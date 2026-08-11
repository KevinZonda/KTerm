import type { ITheme } from '@xterm/xterm';

export const DEFAULT_THEME_NAME = 'KTerm Dark';

interface TerminalThemePreset {
  name: string;
  theme: ITheme;
}

const KTERM_DARK_THEME: TerminalThemePreset = {
  name: DEFAULT_THEME_NAME,
  theme: {
    background: '#0c0f14', foreground: '#d8dee9', cursor: '#8fbcbb',
    cursorAccent: '#0c0f14', selectionBackground: '#3b5268',
    black: '#1b2028', red: '#e06c75', green: '#98c379', yellow: '#e5c07b',
    blue: '#61afef', magenta: '#c678dd', cyan: '#56b6c2', white: '#abb2bf',
    brightBlack: '#5c6370', brightRed: '#e06c75', brightGreen: '#98c379',
    brightYellow: '#e5c07b', brightBlue: '#61afef', brightMagenta: '#c678dd',
    brightCyan: '#56b6c2', brightWhite: '#ffffff'
  }
};

const TERMINAL_THEMES: TerminalThemePreset[] = [
  KTERM_DARK_THEME,
  {
    name: 'Pro',
    theme: {
      background: '#000000', foreground: '#f2f2f2', cursor: '#4d4d4d',
      cursorAccent: '#000000', selectionBackground: '#414141',
      black: '#000000', red: '#990000', green: '#00a600', yellow: '#999900',
      blue: '#2009db', magenta: '#b200b2', cyan: '#00a6b2', white: '#bfbfbf',
      brightBlack: '#666666', brightRed: '#e50000', brightGreen: '#00d900',
      brightYellow: '#e5e500', brightBlue: '#0000ff', brightMagenta: '#e500e5',
      brightCyan: '#00e5e5', brightWhite: '#e5e5e5'
    }
  },
  {
    name: 'Ubuntu',
    theme: {
      background: '#300a24', foreground: '#eeeeec', cursor: '#bbbbbb',
      cursorAccent: '#300a24', selectionBackground: '#b5d5ff',
      black: '#2e3436', red: '#cc0000', green: '#4e9a06', yellow: '#c4a000',
      blue: '#3465a4', magenta: '#75507b', cyan: '#06989a', white: '#d3d7cf',
      brightBlack: '#555753', brightRed: '#ef2929', brightGreen: '#8ae234',
      brightYellow: '#fce94f', brightBlue: '#729fcf', brightMagenta: '#ad7fa8',
      brightCyan: '#34e2e2', brightWhite: '#eeeeec'
    }
  }
];

export function normalizeTerminalThemeName(name: unknown): string {
  if (typeof name !== 'string') {
    return DEFAULT_THEME_NAME;
  }

  return TERMINAL_THEMES.find(theme => theme.name.toLowerCase() === name.toLowerCase())?.name
    ?? DEFAULT_THEME_NAME;
}

export function resolveTerminalTheme(name: string): ITheme {
  const normalized = normalizeTerminalThemeName(name);
  const preset = TERMINAL_THEMES.find(theme => theme.name === normalized) ?? KTERM_DARK_THEME;
  return { ...preset.theme };
}

export function applyTerminalThemeToDocument(name: string): void {
  const theme = resolveTerminalTheme(name);
  document.documentElement.style.setProperty(
    '--terminal-background',
    theme.background ?? '#0c0f14'
  );
  document.documentElement.style.setProperty(
    '--terminal-foreground',
    theme.foreground ?? '#d8dee9'
  );
}
