#!/usr/bin/env node
/**
 * MCP Bridge for MelonMCP (Node.js version)
 *
 * This script bridges stdio (used by MCP clients like Claude Code) to the TCP
 * socket used by the MelonMCP server running in the game.
 *
 * Usage:
 *     node mcp-bridge.js [--host localhost] [--port 27015]
 *
 * Configure in Claude Code's MCP settings (claude_desktop_config.json):
 * {
 *     "mcpServers": {
 *         "melonmcp": {
 *             "command": "node",
 *             "args": ["path/to/mcp-bridge.js", "--host", "localhost", "--port", "27015"]
 *         }
 *     }
 * }
 */

const net = require('net');
const readline = require('readline');

class MCPBridge {
    constructor(host, port) {
        this.host = host;
        this.port = port;
        this.socket = null;
        this.connected = false;
        this.pendingMessages = [];
        this.buffer = '';
    }

    log(message) {
        // Log to stderr to not interfere with MCP protocol on stdout
        console.error(`[MCP Bridge] ${message}`);
    }

    sendErrorResponse(requestId, code, message) {
        const response = {
            jsonrpc: '2.0',
            id: requestId,
            error: { code, message }
        };
        console.log(JSON.stringify(response));
    }

    connect() {
        return new Promise((resolve) => {
            this.socket = new net.Socket();

            this.socket.setTimeout(5000);

            this.socket.on('connect', () => {
                this.connected = true;
                this.socket.setTimeout(0);
                this.log(`Connected to MelonMCP server at ${this.host}:${this.port}`);

                // Send any pending messages
                while (this.pendingMessages.length > 0) {
                    const msg = this.pendingMessages.shift();
                    this.socket.write(msg.data);
                }

                resolve(true);
            });

            this.socket.on('data', (data) => {
                this.buffer += data.toString('utf-8');

                // Process complete lines
                while (this.buffer.includes('\n')) {
                    const newlineIndex = this.buffer.indexOf('\n');
                    const line = this.buffer.substring(0, newlineIndex);
                    this.buffer = this.buffer.substring(newlineIndex + 1);

                    if (line.trim()) {
                        // Forward to stdout
                        console.log(line);
                    }
                }
            });

            this.socket.on('error', (err) => {
                this.log(`Socket error: ${err.message}`);
                this.connected = false;
                resolve(false);
            });

            this.socket.on('close', () => {
                this.log('Connection closed');
                this.connected = false;
            });

            this.socket.on('timeout', () => {
                this.log('Connection timeout');
                this.socket.destroy();
                resolve(false);
            });

            this.socket.connect(this.port, this.host);
        });
    }

    async sendMessage(message) {
        const data = message + '\n';

        // Parse to get request ID for error handling
        let requestId = null;
        try {
            const parsed = JSON.parse(message);
            requestId = parsed.id;
        } catch (e) {}

        if (!this.connected) {
            const connected = await this.connect();
            if (!connected) {
                if (requestId !== null) {
                    this.sendErrorResponse(
                        requestId,
                        -32000,
                        'Game not running or MelonMCP not loaded. Please start the game first.'
                    );
                }
                return;
            }
        }

        try {
            this.socket.write(data);
        } catch (err) {
            this.log(`Send error: ${err.message}`);
            this.connected = false;
            if (requestId !== null) {
                this.sendErrorResponse(requestId, -32000, `Connection lost: ${err.message}`);
            }
        }
    }

    run() {
        const rl = readline.createInterface({
            input: process.stdin,
            output: process.stdout,
            terminal: false
        });

        rl.on('line', async (line) => {
            if (line.trim()) {
                await this.sendMessage(line);
            }
        });

        rl.on('close', () => {
            if (this.socket) {
                this.socket.destroy();
            }
            process.exit(0);
        });

        // Handle process signals
        process.on('SIGINT', () => {
            if (this.socket) {
                this.socket.destroy();
            }
            process.exit(0);
        });

        process.on('SIGTERM', () => {
            if (this.socket) {
                this.socket.destroy();
            }
            process.exit(0);
        });
    }
}

// Parse command line arguments
function parseArgs() {
    const args = process.argv.slice(2);
    const options = {
        host: 'localhost',
        port: 27015
    };

    for (let i = 0; i < args.length; i++) {
        if (args[i] === '--host' && args[i + 1]) {
            options.host = args[++i];
        } else if (args[i] === '--port' && args[i + 1]) {
            options.port = parseInt(args[++i], 10);
        }
    }

    return options;
}

// Main
const options = parseArgs();
const bridge = new MCPBridge(options.host, options.port);
bridge.run();
