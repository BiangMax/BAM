"""A bounded protocol fixture. Only synthetic identities and HeartBeat are used."""
import http.client
import json
import threading
import unittest
from http.server import BaseHTTPRequestHandler, HTTPServer


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_POST(self):
        self.connection.settimeout(2)
        try:
            size = int(self.headers.get("Content-Length", "-1"))
            if not 0 <= size <= 4096:
                return self.send_error(413)
            if self.headers.get_content_type() != "application/json":
                return self.send_error(415)
            data = json.loads(self.rfile.read(size))
            if not isinstance(data, dict):
                return self.send_error(400)
        except (ValueError, OSError):
            return self.send_error(400)
        if (data.get("user"), data.get("device")) != ("fixture", "virtual"):
            return self.send_error(403)
        if self.path == "/getTask":
            tasks = [] if self.server.result else [{"id": "probe", "type": "HeartBeat"}]
            body = json.dumps({"tasks": tasks}).encode()
        elif self.path == "/reportStatus":
            if data.get("task") != "probe" or data.get("status") not in ("SUCCESS", "FAILED") or not isinstance(data.get("payload"), str):
                return self.send_error(400)
            report = (data["status"], data["payload"])
            if self.server.result and self.server.result != report:
                return self.send_error(409)
            self.server.result = report
            body = b"{}"
        else:
            return self.send_error(404)
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class ProtocolTest(unittest.TestCase):
    def test_poll_report_and_replay(self):
        with HTTPServer(("127.0.0.1", 0), Handler) as server:
            server.result = None
            thread = threading.Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                identity = {"user": "fixture", "device": "virtual"}

                def post(path, data):
                    client = http.client.HTTPConnection(*server.server_address, timeout=3)
                    try:
                        client.request("POST", path, json.dumps(data), {"Content-Type": "application/json"})
                        response = client.getresponse()
                        return response.status, response.read()
                    finally:
                        client.close()

                self.assertEqual(post("/getTask", {})[0], 403)
                status, body = post("/getTask", identity)
                self.assertEqual(status, 200)
                self.assertEqual(json.loads(body)["tasks"], [{"id": "probe", "type": "HeartBeat"}])
                self.assertEqual(post("/getTask", identity), (status, body))
                report = dict(identity, task="probe", status="SUCCESS", payload="")
                self.assertEqual(post("/reportStatus", dict(report, task="unknown"))[0], 400)
                for _ in range(2):
                    self.assertEqual(post("/reportStatus", report)[0], 200)
                self.assertEqual(post("/reportStatus", dict(report, status="FAILED"))[0], 409)
                self.assertEqual(json.loads(post("/getTask", identity)[1]), {"tasks": []})
            finally:
                server.shutdown()
                thread.join(3)


if __name__ == "__main__":
    unittest.main()
