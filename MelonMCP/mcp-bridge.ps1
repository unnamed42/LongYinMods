<#
    MelonMCP bridge - zero-dependency Windows version.

    WHY THIS EXISTS: MelonMCP listens on a TCP socket, but MCP clients launch a child process
    and talk stdio. This script is that child process: it relays stdin -> TCP and TCP -> stdout.

    WHY POWERSHELL: Windows PowerShell 5.1 is preinstalled on Windows 10/11, so this needs
    NOTHING installed - unlike the Node and Python bridges, which need a runtime the user may
    not have (and which is often installed but missing from PATH, the single most common
    MCP setup failure).

    USAGE (in claude_desktop_config.json or .mcp.json):

        {
          "mcpServers": {
            "melonmcp": {
              "command": "powershell",
              "args": [
                "-NoProfile", "-ExecutionPolicy", "Bypass",
                "-File", "C:/full/path/mcp-bridge.ps1",
                "-Port", "27015"
              ]
            }
          }
        }

    NOTES
    - "-NoProfile" matters: a user profile can print a banner to stdout, which would corrupt
      the JSON-RPC stream. Same reason this script never writes to stdout except protocol data.
    - "-ExecutionPolicy Bypass" avoids the "running scripts is disabled" error without
      requiring the user to change a machine-wide setting.
    - Use forward slashes or escaped backslashes in the path.

    DESIGN NOTE - why this script compiles C# instead of using PowerShell scriptblocks.
    The relay must pump two directions at once. The obvious PowerShell way is a background
    runspace or a BeginRead callback, but BOTH fail here:
      - Runspace.Open() throws "Index was out of range" on some PowerShell 7 builds.
      - A scriptblock used as an AsyncCallback throws "There is no Runspace available to run
        scripts in this thread" - callbacks fire on an I/O thread with no runspace.
    Add-Type with a small C# relay sidesteps both, uses only what .NET already provides, and
    behaves identically on Windows PowerShell 5.1 and PowerShell 7.x. Compilation takes a
    moment on first run; there is no external tooling involved.

    The relay is byte-transparent: it never parses JSON. The Node/Python bridges parse each
    line so they can return a friendly JSON-RPC error when the game is not running. That
    nicety is not required by the protocol, and dropping it removes the JSON dependency
    entirely. The tradeoff: when the game is not running, the client reports a connection
    failure instead of a custom message.
#>

[CmdletBinding()]
param(
    # Not named $Host (a PowerShell automatic variable) or $HostName.
    [string]$Server = "localhost",
    [int]$Port = 27015
)

$ErrorActionPreference = "Stop"

function Write-Diag([string]$Message) {
    # stderr only: stdout carries protocol bytes and nothing else.
    [Console]::Error.WriteLine("[MCP Bridge] $Message")
}

$source = @'
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

public static class McpTcpRelay
{
    // Runs until either side closes. Returns 0 on a clean EOF, 1 on error.
    public static int Run(string host, int port)
    {
        TcpClient client;
        try
        {
            client = new TcpClient();
            client.Connect(host, port);
            client.NoDelay = true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[MCP Bridge] Failed to connect: " + ex.Message);
            Console.Error.WriteLine("[MCP Bridge] Is the game running with MelonMCP loaded?");
            return 1;
        }

        Console.Error.WriteLine("[MCP Bridge] Connected to MelonMCP server at " + host + ":" + port);

        using (client)
        using (NetworkStream stream = client.GetStream())
        using (Stream stdin = Console.OpenStandardInput())
        using (Stream stdout = Console.OpenStandardOutput())
        {
            Thread pump = new Thread(delegate()
            {
                byte[] buf = new byte[65536];
                try
                {
                    while (true)
                    {
                        int n = stdin.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        stream.Write(buf, 0, n);
                        stream.Flush();
                    }
                }
                catch { /* client closed stdin, or the socket died */ }

                // NetworkStream has no Shutdown; signal the half-close on the socket itself so
                // the server sees EOF on its read side. Best-effort: the socket may already be
                // torn down if the disconnect came from that direction.
                try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
            });

            pump.IsBackground = true;
            pump.Start();

            byte[] outBuf = new byte[65536];
            try
            {
                while (true)
                {
                    int n = stream.Read(outBuf, 0, outBuf.Length);
                    if (n <= 0) break;
                    stdout.Write(outBuf, 0, n);
                    stdout.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[MCP Bridge] Read error: " + ex.Message);
                return 1;
            }
        }

        Console.Error.WriteLine("[MCP Bridge] Connection closed");
        return 0;
    }
}
'@

try {
    Add-Type -TypeDefinition $source -Language CSharp | Out-Null
}
catch {
    Write-Diag "Failed to compile the relay: $($_.Exception.Message)"
    exit 1
}

exit [McpTcpRelay]::Run($Server, $Port)
