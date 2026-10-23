#!/usr/bin/env python3
"""VRisingVR passthrough server for the Steam Frame.

Serves the headset's camera view, SteamVR's virtual camera /dev/video99, to the VRisingVR mod on the PC as
JPEG frames over TCP. The camera is read only while an authenticated mod is connected, and only one mod is
served at a time: a new connection replaces the previous one.

Protocol (integers are big-endian):
    mod -> server: one JSON line: {"token": "...", "width": 960, "fps": 30, "quality": 5}
    server -> mod: one text line, "OK <width> <height>", or "ERR <reason>" before the server closes
    server -> mod, repeated: a uint32 byte count, then one JPEG frame of that size; a count of 0 is a
                             keep-alive sent every 5 s while no new pictures arrive (headset standby)
Frames the mod is too slow to receive are dropped, so it always gets the newest one.

In a web browser, http://<headset>:47816/ is a test page: enter the token to see the camera live. The raw MJPEG
stream is /stream?token=<token> (optionally &width=&fps=), and /status?token=<token> says whether new pictures
are arriving. Browser views don't replace the mod's connection.

While nobody wears the headset, SteamVR's standby pauses the cameras and /dev/video99 keeps repeating the last
picture. Repeated pictures are never sent, so the mod hides its camera view instead of showing a frozen one.
"""

import html
import hmac
import json
import os
import select
import socket
import struct
import subprocess
import threading
import time
import urllib.parse

VERSION = "1.1.0"
PORT = int(os.environ.get("VRISINGVR_PT_PORT", "47816"))
DEVICE = os.environ.get("VRISINGVR_PT_DEVICE", "/dev/video99")
TOKEN_FILE = os.path.expanduser(os.environ.get("VRISINGVR_PT_TOKEN_FILE", "~/.config/vrisingvr-passthrough/token"))
SOURCE_WIDTH, SOURCE_HEIGHT = 1920, 1080  # what SteamVR's v4l2cam forwarder writes (RGB3)
MAX_FRAME = 8 << 20

_active_lock = threading.Lock()
_active_conn = None

# For /status: when any stream last saw a new picture. While the headset is in standby SteamVR pauses the
# cameras and its virtual camera repeats the last picture, so repeats don't count (and aren't sent).
_status_lock = threading.Lock()
_running_streams = 0
_stream_started = 0.0
_last_new_frame = 0.0


def log(message):
    print(message, flush=True)


def clamp(value, low, high):
    return max(low, min(high, value))


class Stream:
    """One ffmpeg process turning the camera feed into JPEG frames. Keeps only the newest frame."""

    def __init__(self, width, height, fps, quality):
        global _running_streams, _stream_started
        self._cond = threading.Condition()
        self._frame = None
        self._seq = 0
        self._closed = False
        self._stopped = False
        with _status_lock:
            _running_streams += 1
            _stream_started = time.monotonic()
        # mpjpeg writes a Content-length header before every frame, so frames split without scanning JPEG data.
        cmd = ["ffmpeg", "-nostdin", "-hide_banner", "-loglevel", "error",
               "-f", "v4l2", "-input_format", "rgb24", "-video_size", f"{SOURCE_WIDTH}x{SOURCE_HEIGHT}", "-i", DEVICE,
               "-vf", f"fps={fps},scale={width}:{height}", "-q:v", str(quality),
               "-f", "mpjpeg", "pipe:1"]
        self._proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        threading.Thread(target=self._read_frames, daemon=True).start()
        threading.Thread(target=self._log_errors, daemon=True).start()

    def _read_frames(self):
        global _last_new_frame
        out = self._proc.stdout
        previous = None
        try:
            while True:
                line = out.readline()
                if not line:
                    return
                if not line.startswith(b"--"):
                    continue  # the CRLF after each frame, or anything before the first boundary
                length = None
                while True:
                    header = out.readline()
                    if not header:
                        return
                    header = header.strip()
                    if not header:
                        break
                    name, _, value = header.partition(b":")
                    if name.strip().lower() == b"content-length":
                        length = int(value)
                if length is None or not 0 < length <= MAX_FRAME:
                    log(f"unexpected frame header from ffmpeg (length {length}); stopping the stream")
                    return
                frame = out.read(length)
                if len(frame) < length:
                    return
                if frame == previous:
                    continue  # a paused camera (headset standby): the same picture again
                if previous is not None:
                    # The first picture of a stream may be an old one repeated, so it doesn't count as live.
                    with _status_lock:
                        _last_new_frame = time.monotonic()
                previous = frame
                with self._cond:
                    self._frame = frame
                    self._seq += 1
                    self._cond.notify_all()
        finally:
            with self._cond:
                self._closed = True
                self._cond.notify_all()

    def _log_errors(self):
        for line in self._proc.stderr:
            log("ffmpeg: " + line.decode(errors="replace").rstrip())

    def next_frame(self, after_seq, timeout):
        """Wait for a frame newer than after_seq. Returns (seq, frame or None, closed)."""
        with self._cond:
            self._cond.wait_for(lambda: self._seq != after_seq or self._closed, timeout)
            fresh = self._seq != after_seq
            return self._seq, (self._frame if fresh else None), self._closed

    def stop(self):
        global _running_streams
        if not self._stopped:
            self._stopped = True
            with _status_lock:
                _running_streams -= 1
        if self._proc.poll() is None:
            self._proc.terminate()
            try:
                self._proc.wait(3)
            except subprocess.TimeoutExpired:
                log("ffmpeg didn't stop within 3 s; killing it")
                self._proc.kill()
                try:
                    self._proc.wait(5)  # reap it, or it lingers as a zombie
                except subprocess.TimeoutExpired:
                    log("ffmpeg didn't exit after being killed")


def client_gone(conn):
    """True if the other side closed the connection; checked without blocking (it sends nothing after its request)."""
    try:
        readable, _, _ = select.select([conn], [], [], 0)
        return bool(readable) and conn.recv(1, socket.MSG_PEEK) == b""
    except OSError:
        return True


def reply(conn, text):
    try:
        conn.sendall((text + "\n").encode())
    except OSError:
        pass


def stream_params(get):
    """Clamp the requested stream size and rate; get(name, default) reads one request value."""
    width = clamp(int(get("width", 960)), 160, SOURCE_WIDTH) // 2 * 2
    height = round(width * SOURCE_HEIGHT / SOURCE_WIDTH / 2) * 2
    fps = clamp(int(get("fps", 30)), 1, 60)
    quality = clamp(int(get("quality", 5)), 2, 31)
    return width, height, fps, quality


def token_ok(given, token):
    return hmac.compare_digest(str(given).encode(), token.encode())


def handle(conn, addr, token):
    peer = f"{addr[0]}:{addr[1]}"
    try:
        conn.settimeout(5)
        reader = conn.makefile("rb")
        line = reader.readline(4096)
        if line.startswith(b"GET "):
            serve_browser(conn, reader, line, peer, token)
        else:
            serve_mod(conn, line, peer, token)
    except OSError as e:
        log(f"{peer}: disconnected ({e.__class__.__name__})")
    finally:
        try:
            conn.close()
        except OSError:
            pass


PAGE_STYLE = """
body { margin: 0; background: #14121a; color: #e8e4ef; font: 16px/1.5 system-ui, sans-serif; }
main { max-width: 980px; margin: 0 auto; padding: 24px 16px; }
h1 { margin: 0 0 8px; font-size: 26px; color: #ff5c6c; }
code { background: #2a2633; padding: 1px 5px; border-radius: 4px; word-break: break-all; }
form { display: flex; gap: 8px; flex-wrap: wrap; margin: 16px 0; }
input { flex: 1 1 320px; padding: 10px; border-radius: 6px; border: 1px solid #4a4458; background: #1e1b26;
        color: inherit; font: 14px monospace; }
button { padding: 10px 18px; border: 0; border-radius: 6px; background: #c8323f; color: #fff; font-size: 15px; cursor: pointer; }
img { display: block; width: 100%; border-radius: 8px; background: #000; margin: 16px 0; }
.error { color: #ff8a8a; }
.live { color: #6ee7a0; font-weight: 600; }
.muted { color: #9a93a8; font-size: 14px; }
"""

VIEWER_SCRIPT = """
const statusUrl = "/status?token=" + encodeURIComponent(TOKEN);
const statusLine = document.getElementById("status");
function show(text, cls) { statusLine.textContent = text; statusLine.className = cls; }
async function poll() {
  try {
    const s = await (await fetch(statusUrl, { cache: "no-store" })).json();
    if (s.new_frame_ms_ago !== null && s.new_frame_ms_ago < 2000) show("Live", "live");
    else if (s.streams > 0 && s.stream_age_ms > 3000)
      show("Paused: no new pictures from the headset's cameras. The headset is probably in standby because " +
           "nobody is wearing it, so SteamVR has paused the cameras. Put it on to wake them.", "error");
    else show("Starting the camera…", "muted");
  } catch (e) {
    show("Can't reach the server.", "error");
  }
  setTimeout(poll, 1000);
}
poll();
"""


def page(body):
    return ("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">"
            "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            f"<title>VRisingVR passthrough</title><style>{PAGE_STYLE}</style></head>"
            f"<body><main><h1>VRisingVR passthrough</h1>{body}</main></body></html>").encode()


def index_page(error=""):
    camera = ("found" if os.path.exists(DEVICE)
              else "not found: is SteamVR running on the headset?")
    return page(
        "<p>This Steam Frame runs the VRisingVR passthrough server. It sends the headset's camera view to the "
        "VRisingVR mod on your PC, so you can see your keyboard and mouse while you play in VR.</p>"
        "<p>Enter the server's token to check that the camera works.</p>"
        "<form method=\"get\" action=\"/\"><input name=\"token\" placeholder=\"Token\" autocomplete=\"off\" "
        "spellcheck=\"false\" required><button>Show camera</button></form>"
        + (f"<p class=\"error\">{error}</p>" if error else "") +
        "<p>Where's the token? The server created it when it was installed. It's in "
        "<code>~/.config/vrisingvr-passthrough/token</code> on the headset; from your PC, run "
        "<code>ssh steamos@frame \"cat ~/.config/vrisingvr-passthrough/token\"</code>.</p>"
        f"<p class=\"muted\">Server {VERSION}, port {PORT}. SteamVR's camera device {DEVICE}: {camera}.</p>")


def viewer_page(given):
    shown = html.escape(given)
    stream_url = "/stream?token=" + urllib.parse.quote(given, safe="")
    script_token = json.dumps(given).replace("<", "\\u003c")
    return page(
        "<p>The headset's camera. If you can see your surroundings, the server works.</p>"
        "<p id=\"status\" class=\"muted\">Starting the camera…</p>"
        f"<img src=\"{stream_url}\" alt=\"The headset's camera view\">"
        f"<script>const TOKEN = {script_token};{VIEWER_SCRIPT}</script>"
        "<p>To use it in VR, open the mod's settings file (<code>vrisingvr.tabletop.cfg</code>) and, in the "
        "<code>[Passthrough]</code> section, set <code>Enabled = true</code> and paste this token after "
        "<code>Token = </code>:</p>"
        f"<form onsubmit=\"return false\"><input value=\"{shown}\" readonly onclick=\"this.select()\"></form>"
        "<p class=\"muted\">Click the token to select it, then copy it. Closing this page stops the camera. "
        "The mod and this page can run at the same time.</p>"
        "<p class=\"muted\"><a href=\"/\" style=\"color:inherit\">Back</a></p>")


def status_json():
    """Whether new pictures are arriving: during headset standby the cameras pause and none do."""
    now = time.monotonic()
    with _status_lock:
        running, started, last = _running_streams, _stream_started, _last_new_frame
    return json.dumps({
        "streams": running,
        "stream_age_ms": int((now - started) * 1000) if running else None,
        "new_frame_ms_ago": int((now - last) * 1000) if last else None,
        "camera_device": os.path.exists(DEVICE),
    }).encode()


def http_send(conn, status, content_type, body):
    conn.sendall(f"HTTP/1.1 {status}\r\nContent-Type: {content_type}\r\nContent-Length: {len(body)}\r\n"
                 "Cache-Control: no-store\r\nConnection: close\r\n\r\n".encode() + body)


def serve_browser(conn, reader, request_line, peer, token):
    """The browser test page (/), the live viewer (/?token=...) and the raw MJPEG stream (/stream?token=...)."""
    parts = request_line.split()
    target = parts[1].decode(errors="replace") if len(parts) > 1 else "/"
    for _ in range(100):  # skip the request headers
        if reader.readline(8192) in (b"\r\n", b"\n", b""):
            break
    url = urllib.parse.urlsplit(target)
    query = urllib.parse.parse_qs(url.query)
    given = query.get("token", [None])[0]

    if url.path == "/favicon.ico":
        http_send(conn, "404 Not Found", "text/plain", b"")
        return
    if url.path in ("/", "/index.html"):
        if given is None:
            http_send(conn, "200 OK", "text/html; charset=utf-8", index_page())
        elif token_ok(given, token):
            http_send(conn, "200 OK", "text/html; charset=utf-8", viewer_page(given))
        else:
            log(f"{peer}: test page with a wrong token, rejected")
            time.sleep(1)
            http_send(conn, "403 Forbidden", "text/html; charset=utf-8",
                      index_page("That token isn't right. Check that you copied all of it."))
        return
    if url.path == "/status":
        if given is None or not token_ok(given, token):
            time.sleep(1)
            http_send(conn, "403 Forbidden", "application/json", b'{"error": "wrong or missing token"}')
        else:
            http_send(conn, "200 OK", "application/json", status_json())
        return
    if url.path != "/stream":
        http_send(conn, "404 Not Found", "text/plain", b"Not found. The test page is at /\n")
        return
    if given is None or not token_ok(given, token):
        log(f"{peer}: stream request with a wrong or missing token, rejected")
        time.sleep(1)
        http_send(conn, "403 Forbidden", "text/plain", b"Wrong or missing token. The test page is at /\n")
        return
    try:
        width, height, fps, quality = stream_params(lambda name, default: query.get(name, [default])[0])
    except ValueError:
        http_send(conn, "400 Bad Request", "text/plain", b"Bad width, fps or quality.\n")
        return

    stream = Stream(width, height, fps, quality)
    try:
        conn.sendall(b"HTTP/1.1 200 OK\r\nContent-Type: multipart/x-mixed-replace; boundary=vrisingvr\r\n"
                     b"Cache-Control: no-store\r\nConnection: close\r\n\r\n")
        log(f"{peer}: browser preview, {width}x{height} at up to {fps} fps")
        conn.settimeout(10)
        seq = 0
        while True:
            seq, frame, closed = stream.next_frame(seq, 5)
            if frame is not None:
                conn.sendall(b"--vrisingvr\r\nContent-Type: image/jpeg\r\nContent-Length: %d\r\n\r\n" % len(frame)
                             + frame + b"\r\n")
            elif closed or client_gone(conn):
                break
    finally:
        stream.stop()


def serve_mod(conn, line, peer, token):
    global _active_conn
    stream = None
    try:
        try:
            request = json.loads(line)
            given = request.get("token", "")
            width, height, fps, quality = stream_params(request.get)
        except (ValueError, AttributeError, TypeError):
            reply(conn, "ERR bad request")
            return
        if not token_ok(given, token):
            log(f"{peer}: wrong token, rejected")
            time.sleep(1)
            reply(conn, "ERR wrong token")
            return

        # One mod at a time: a reconnect (or a second PC) replaces the previous connection.
        with _active_lock:
            previous, _active_conn = _active_conn, conn
        if previous is not None:
            try:
                previous.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass

        stream = Stream(width, height, fps, quality)
        reply(conn, f"OK {width} {height}")
        log(f"{peer}: streaming {width}x{height} at up to {fps} fps, quality {quality}")
        conn.settimeout(10)
        seq = 0
        sent = 0
        while True:
            seq, frame, closed = stream.next_frame(seq, 5)
            if frame is not None:
                conn.sendall(struct.pack(">I", len(frame)) + frame)
                sent += 1
            elif closed:
                log(f"{peer}: the camera stream ended")
                break
            elif client_gone(conn) or _active_conn is not conn:
                break
            else:
                conn.sendall(struct.pack(">I", 0))  # keep-alive: no new pictures (headset standby)
        log(f"{peer}: sent {sent} frames")
    finally:
        if stream is not None:
            stream.stop()
        with _active_lock:
            if _active_conn is conn:
                _active_conn = None


def main():
    with open(TOKEN_FILE) as f:
        token = f.read().strip()
    if len(token) < 16:
        raise SystemExit(f"{TOKEN_FILE} must hold a token of at least 16 characters")
    server = socket.create_server(("", PORT))
    log(f"VRisingVR passthrough server listening on port {PORT}, camera {DEVICE}")
    while True:
        conn, addr = server.accept()
        threading.Thread(target=handle, args=(conn, addr, token), daemon=True).start()


if __name__ == "__main__":
    main()
