#!/usr/bin/env python3
"""
MCP Bridge for MelonMCP

This script bridges stdio (used by MCP clients like Claude Code) to the TCP
socket used by the MelonMCP server running in the game.

Usage:
    python mcp-bridge.py [--host localhost] [--port 27015]

Configure in Claude Code's MCP settings (claude_desktop_config.json):
{
    "mcpServers": {
        "melonmcp": {
            "command": "python",
            "args": ["path/to/mcp-bridge.py", "--host", "localhost", "--port", "27015"]
        }
    }
}
"""

import argparse
import json
import socket
import sys
import threading
import time
from typing import Optional


class MCPBridge:
    def __init__(self, host: str, port: int):
        self.host = host
        self.port = port
        self.socket: Optional[socket.socket] = None
        self.connected = False
        self.running = True
        self.reconnect_delay = 1
        self.max_reconnect_delay = 30

    def connect(self) -> bool:
        """Attempt to connect to the MelonMCP server."""
        try:
            self.socket = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
            self.socket.settimeout(5)
            self.socket.connect((self.host, self.port))
            self.socket.settimeout(None)
            self.connected = True
            self.reconnect_delay = 1
            self.log(f"Connected to MelonMCP server at {self.host}:{self.port}")
            return True
        except Exception as e:
            self.log(f"Failed to connect: {e}")
            self.connected = False
            return False

    def disconnect(self):
        """Disconnect from the server."""
        if self.socket:
            try:
                self.socket.close()
            except:
                pass
            self.socket = None
        self.connected = False

    def log(self, message: str):
        """Log a message to stderr (not stdout, which is for MCP protocol)."""
        print(f"[MCP Bridge] {message}", file=sys.stderr)

    def send_error_response(self, request_id, code: int, message: str):
        """Send an error response to the MCP client."""
        response = {
            "jsonrpc": "2.0",
            "id": request_id,
            "error": {
                "code": code,
                "message": message
            }
        }
        print(json.dumps(response), flush=True)

    def handle_stdin(self):
        """Read from stdin and forward to the server."""
        buffer = ""
        while self.running:
            try:
                # Read character by character to handle line-delimited JSON
                char = sys.stdin.read(1)
                if not char:
                    # EOF
                    self.running = False
                    break

                buffer += char

                # Check if we have a complete JSON message (newline-delimited)
                if char == '\n':
                    message = buffer.strip()
                    buffer = ""

                    if not message:
                        continue

                    # Parse to get the request ID for error handling
                    request_id = None
                    try:
                        parsed = json.loads(message)
                        request_id = parsed.get("id")
                    except:
                        pass

                    # If not connected, try to connect
                    if not self.connected:
                        if not self.connect():
                            if request_id is not None:
                                self.send_error_response(
                                    request_id,
                                    -32000,
                                    f"Game not running or MelonMCP not loaded. Please start the game first."
                                )
                            continue

                    # Forward to server
                    try:
                        self.socket.sendall((message + "\n").encode('utf-8'))
                    except Exception as e:
                        self.log(f"Send error: {e}")
                        self.disconnect()
                        if request_id is not None:
                            self.send_error_response(
                                request_id,
                                -32000,
                                f"Connection lost: {e}"
                            )

            except Exception as e:
                self.log(f"Stdin handler error: {e}")
                break

    def handle_socket(self):
        """Read from the server and forward to stdout."""
        buffer = ""
        while self.running:
            if not self.connected:
                time.sleep(0.1)
                continue

            try:
                # Read data from server
                data = self.socket.recv(4096)
                if not data:
                    self.log("Server disconnected")
                    self.disconnect()
                    continue

                buffer += data.decode('utf-8')

                # Process complete lines
                while '\n' in buffer:
                    line, buffer = buffer.split('\n', 1)
                    if line.strip():
                        # Forward to stdout
                        print(line, flush=True)

            except socket.timeout:
                continue
            except Exception as e:
                self.log(f"Socket read error: {e}")
                self.disconnect()

    def run(self):
        """Main run loop."""
        # Start threads
        stdin_thread = threading.Thread(target=self.handle_stdin, daemon=True)
        socket_thread = threading.Thread(target=self.handle_socket, daemon=True)

        stdin_thread.start()
        socket_thread.start()

        # Wait for threads
        try:
            while self.running:
                time.sleep(0.1)
        except KeyboardInterrupt:
            self.running = False

        self.disconnect()


def main():
    parser = argparse.ArgumentParser(description='MCP Bridge for MelonMCP')
    parser.add_argument('--host', default='localhost', help='MelonMCP server host')
    parser.add_argument('--port', type=int, default=27015, help='MelonMCP server port')
    args = parser.parse_args()

    bridge = MCPBridge(args.host, args.port)
    bridge.run()


if __name__ == '__main__':
    main()
