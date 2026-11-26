export function watchEvents(url, eventName, onEvent, { active = () => true, onError = () => {} } = {}) {
  let stream;

  function close() {
    stream?.close();
    stream = undefined;
  }

  function sync() {
    if (document.hidden || !active()) {
      close();
      return;
    }

    if (!stream) {
      stream = new EventSource(url);
      stream.addEventListener(eventName, event => onEvent(event.data));
      stream.onerror = onError;
    }
  }

  window.addEventListener("hashchange", sync);
  window.addEventListener("pageshow", sync);
  window.addEventListener("pagehide", close);
  document.addEventListener("visibilitychange", sync);
  sync();
}
