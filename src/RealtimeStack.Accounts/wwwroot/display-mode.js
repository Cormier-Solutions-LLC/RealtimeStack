/* Bundled from Cormier Common.UI v0.2.1. Update from the matching package release. */
/* Load after the theme stylesheets, before first paint. */
(() => {
  const key = 'cui-display-mode';
  const root = document.documentElement;
  const query = window.matchMedia?.('(prefers-color-scheme: dark)');
  const valid = mode => ['system', 'light', 'dark'].includes(mode);
  let temporaryMode;
  let previousState;

  function readMode() {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  function getState() {
    const styles = getComputedStyle(root);
    const cssMode = styles.getPropertyValue('--cui-default-mode').trim();
    const defaultMode = valid(cssMode) ? cssMode : 'system';
    const locked = styles.getPropertyValue('--cui-mode-locked').trim() === '1';
    const storedMode = temporaryMode ?? readMode();
    const markupMode = root.getAttribute('data-cui-mode');
    const mode = locked ? defaultMode : valid(storedMode) ? storedMode : valid(markupMode) ? markupMode : defaultMode;
    const resolved = mode === 'system' ? (query?.matches ? 'dark' : 'light') : mode;
    return { mode, resolved, locked };
  }

  function apply() {
    const state = getState();
    root.dataset.theme = state.resolved;
    root.style.colorScheme = state.resolved;
    for (const button of document.querySelectorAll('button[data-cui-mode]')) {
      button.setAttribute('aria-pressed', String(button.dataset.cuiMode === state.mode));
      button.hidden = state.locked;
    }
    if (Object.keys(state).some(key => state[key] !== previousState?.[key])) {
      previousState = state;
      root.dispatchEvent(new CustomEvent('cui:modechange', { detail: state }));
    }
    return state;
  }

  function setMode(mode, { persist = true } = {}) {
    if (!valid(mode) || getState().locked) return;
    temporaryMode = mode;
    if (persist) {
      try {
        localStorage.setItem(key, mode);
        temporaryMode = undefined;
      } catch {
        // Keep the selection available for the current document.
      }
    }
    apply();
  }

  window.CommonUI = { apply, getState, getMode: () => getState().mode, setMode };
  query?.addEventListener('change', apply);
  window.addEventListener('storage', event => {
    if (event.key !== key && event.key !== null) return;
    temporaryMode = undefined;
    apply();
  });
  window.addEventListener('pageshow', apply);
  document.addEventListener('click', event => {
    const target = event.target?.closest?.('button[data-cui-mode]');
    if (target) setMode(target.dataset.cuiMode);
  });
  document.addEventListener('DOMContentLoaded', apply);
  apply();
})();
