// Browser WebSocket for Hibira's WebGL transport (WebGLWebSocketTransport.cs).
//
// Each socket keeps a queue of what happened; C# reads it once a frame with
// HibiraWS_Poll, so nothing calls back into the game and the plugin needs no
// dynCall (it works the same on every Unity version that has WebGL).
// Queue entries: "o" open, "m<text>" message, "c<code>|<reason>" close.
var HibiraWebSocket = {
  $HibiraWS: {
    next: 1,
    sockets: {},
    closed: function (s, code, reason) {
      if (s.closed) return;
      s.closed = true;
      s.queue.push('c' + (code || 1006) + '|' + (reason || ''));
    },
  },

  HibiraWS_Open: function (urlPtr) {
    var url = UTF8ToString(urlPtr);
    var id = HibiraWS.next++;
    var s = { ws: null, queue: [], opened: false, closed: false };
    HibiraWS.sockets[id] = s;
    try {
      s.ws = new WebSocket(url);
    } catch (e) {
      HibiraWS.closed(s, 1006, String((e && e.message) || e));
      return id;
    }
    s.ws.onopen = function () {
      s.opened = true;
      if (!s.closed) s.queue.push('o');
    };
    s.ws.onmessage = function (e) {
      if (s.closed || typeof e.data !== 'string') return;
      // Answer server pings here: browsers pause a background tab's frames,
      // and the game would otherwise miss them and be disconnected.
      if (e.data.indexOf('"pusher:ping"') !== -1) {
        try {
          if (JSON.parse(e.data).event === 'pusher:ping') {
            s.ws.send('{"event":"pusher:pong","data":{}}');
            return;
          }
        } catch (err) {}
      }
      s.queue.push('m' + e.data);
    };
    // The browser hides why a connection failed, and onclose (1006) normally
    // follows; report a failed connect here in case it does not. On an open
    // socket, wait for onclose and its real code.
    s.ws.onerror = function () {
      if (!s.opened) HibiraWS.closed(s, 1006, '');
    };
    s.ws.onclose = function (e) {
      HibiraWS.closed(s, e.code, e.reason);
    };
    return id;
  },

  HibiraWS_Send: function (id, textPtr) {
    var s = HibiraWS.sockets[id];
    if (!s || s.closed || !s.ws || s.ws.readyState !== 1) return 0;
    try {
      s.ws.send(UTF8ToString(textPtr));
      return 1;
    } catch (e) {
      return 0;
    }
  },

  HibiraWS_Close: function (id, code, reasonPtr) {
    var s = HibiraWS.sockets[id];
    if (!s) return;
    var reason = UTF8ToString(reasonPtr);
    var wasClosed = s.closed;
    HibiraWS.closed(s, code, reason);
    if (wasClosed || !s.ws) return;
    try {
      s.ws.close(code, reason);
    } catch (e) {
      try {
        s.ws.close();
      } catch (e2) {}
    }
  },

  // Returns the next queue entry, or null when there is none. IL2CPP frees
  // returned strings.
  HibiraWS_Poll: function (id) {
    var s = HibiraWS.sockets[id];
    if (!s || s.queue.length === 0) return 0;
    var str = s.queue.shift();
    var size = lengthBytesUTF8(str) + 1;
    var buf = _malloc(size);
    stringToUTF8(str, buf, size);
    return buf;
  },

  HibiraWS_Free: function (id) {
    var s = HibiraWS.sockets[id];
    if (!s) return;
    delete HibiraWS.sockets[id];
    if (s.ws) {
      s.ws.onopen = s.ws.onmessage = s.ws.onerror = s.ws.onclose = null;
      if (s.ws.readyState === 0 || s.ws.readyState === 1) {
        try {
          s.ws.close(1000, '');
        } catch (e) {}
      }
    }
  },
};

autoAddDeps(HibiraWebSocket, '$HibiraWS');
mergeInto(LibraryManager.library, HibiraWebSocket);
