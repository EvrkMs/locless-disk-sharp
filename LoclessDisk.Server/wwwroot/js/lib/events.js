export function watchEvents(url, eventName, onEvent, { active = () => true, onError = () => {} } = {}) {
  let stream;

  function close() {
    stream?.close();
    stream = undefined;
  }

  function sync() {
    if (!active()) {
      close();
      return;
    }

    if (!stream) {
      const connection = new EventSource(url);
      stream = connection;
      connection.addEventListener(eventName, event => {
        if (stream === connection) onEvent(event.data);
      });
      connection.onerror = () => {
        if (stream === connection) onError();
      };
    }
  }

  window.addEventListener("hashchange", sync);
  window.addEventListener("pageshow", sync);
  window.addEventListener("pagehide", close);
  sync();
}
