#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""MelonMCP 自检 —— 一条命令跑完 MCP 传输与 execute_csharp 语义回归。

    python3 tools/mcp_selftest.py                    # 默认 http://127.0.0.1:27015/mcp
    python3 tools/mcp_selftest.py -v                 # 附上每条响应的原文
    python3 tools/mcp_selftest.py --only 副作用       # 只跑名字含该子串的用例
    python3 tools/mcp_selftest.py --url … --timeout 60

退出码：0 = 全过，1 = 有失败，2 = 连不上（游戏没跑 / 27015 没监听）。

⚠️ 会清空脚本会话状态：第一条用例带 reset=true，跑完再 reset 一次。
   也就是说它会丢掉你自己在 execute_csharp 里定义的变量和类。

为什么要有这个脚本：`ScriptSession` 的执行路径被整体换过一次（一个 snippet 现在可以
含多个「提交」，见 docs/runtime-probing.md §7.1）。其中两类回归**肉眼看不出来**：

  * 「无值语句被执行两次」—— 同一个 snippet 内自增看不出来，必须跨调用读；
  * 「返回 null」被当成「无值」—— 要靠一个显式回 null 的 snippet 才探得到。

所以别只跑「定义 class + 调用」，普通语句才最容易漏。

依赖：只用标准库。
"""

import argparse
import http.client
import json
import re
import sys
from urllib.parse import urlparse

DEFAULT_URL = "http://127.0.0.1:27015/mcp"

VERBOSE = False
_next_id = [0]

BAD = "FAIL"


def next_id():
    _next_id[0] += 1
    return _next_id[0]


class Failure(Exception):
    """一条用例没达到预期。消息里写清「期望 vs 实际」。"""


# ── HTTP / JSON-RPC 底层 ──────────────────────────────────────────────────────

def http_request(url, method, body, headers, timeout):
    """发一次请求，返回 (状态码, 响应头 dict, 响应体 bytes)。每次新建连接 —— 服务端总是 Connection: close。"""
    parsed = urlparse(url)
    conn = http.client.HTTPConnection(parsed.hostname, parsed.port or 80, timeout=timeout)
    try:
        conn.request(method, parsed.path or "/", body=body, headers=headers or {})
        response = conn.getresponse()
        return response.status, dict(response.getheaders()), response.read()
    finally:
        conn.close()


def post_message(url, message, timeout, extra_headers=None):
    """POST 一条 JSON-RPC 消息，按规范带上 Content-Type 与 Accept。返回 (状态码, bytes)。"""
    headers = {
        "Content-Type": "application/json",
        "Accept": "application/json, text/event-stream",
    }
    if extra_headers:
        headers.update(extra_headers)

    body = json.dumps(message).encode("utf-8")
    status, _, payload = http_request(url, "POST", body, headers, timeout)

    if VERBOSE:
        print("        << HTTP {} {}".format(status, payload[:400].decode("utf-8", "replace")))
    return status, payload


def rpc(url, method, params, timeout):
    """发一条 JSON-RPC 请求并返回 result；HTTP 或 JSON-RPC 层出错就抛 Failure。"""
    message = {"jsonrpc": "2.0", "id": next_id(), "method": method}
    if params is not None:
        message["params"] = params

    status, payload = post_message(url, message, timeout)
    if status != 200:
        raise Failure("HTTP {}（期望 200）：{}".format(status, payload[:300].decode("utf-8", "replace")))

    try:
        reply = json.loads(payload)
    except ValueError as error:
        raise Failure("响应不是 JSON（{}）：{}".format(error, repr(payload[:300])))

    if "error" in reply:
        raise Failure("JSON-RPC error：{}".format(json.dumps(reply["error"], ensure_ascii=False)))
    return reply.get("result")


def execute_csharp(url, arguments, timeout):
    """调一次 execute_csharp，返回 (isError, 文本)。"""
    result = rpc(url, "tools/call", {"name": "execute_csharp", "arguments": arguments}, timeout) or {}

    text = "".join(
        part.get("text", "")
        for part in result.get("content", [])
        if part.get("type") == "text"
    )
    return bool(result.get("isError")), text


# ── 传输层用例 ────────────────────────────────────────────────────────────────

def transport_checks(url, timeout):
    checks = []

    def get_is_405():
        status, _, _ = http_request(url, "GET", None, {"Accept": "*/*"}, timeout)
        if status != 405:
            raise Failure("HTTP {}（期望 405 —— 本服务不提供 GET 流）".format(status))

    def notification_is_202():
        status, payload = post_message(
            url, {"jsonrpc": "2.0", "method": "notifications/initialized", "params": {}}, timeout
        )
        if status != 202:
            raise Failure("HTTP {}（期望 202）：{}".format(status, repr(payload[:200])))
        if payload.strip():
            raise Failure("202 应当没有 body，实际：{}".format(repr(payload[:200])))

    def bad_version_is_400():
        status, payload = post_message(
            url,
            {
                "jsonrpc": "2.0",
                "id": next_id(),
                "method": "initialize",
                "params": {
                    "protocolVersion": "1999-01-01",
                    "capabilities": {},
                    "clientInfo": {"name": "mcp_selftest", "version": "1"},
                },
            },
            timeout,
            {"MCP-Protocol-Version": "1999-01-01"},
        )
        if status != 400:
            raise Failure("HTTP {}（期望 400）：{}".format(status, repr(payload[:200])))

        kind = (
            json.loads(payload)
            .get("error", {})
            .get("data", {})
            .get("type")
        )
        if kind != "UnsupportedProtocolVersion":
            raise Failure("错误类型是 {!r}（期望 UnsupportedProtocolVersion）：{}".format(kind, repr(payload[:200])))

    def bad_origin_is_403():
        status, payload = post_message(
            url,
            {"jsonrpc": "2.0", "id": next_id(), "method": "tools/list", "params": {}},
            timeout,
            {"Origin": "http://evil.example"},
        )
        if status != 403:
            raise Failure("HTTP {}（期望 403 —— 非 loopback Origin）".format(status))

    checks.append(("GET 被拒 405（不提供 GET 流）", get_is_405))
    checks.append(("通知回 202 且无 body", notification_is_202))
    checks.append(("非法 MCP-Protocol-Version 被拒 400", bad_version_is_400))
    checks.append(("非 loopback Origin 被拒 403", bad_origin_is_403))
    return checks


# ── execute_csharp 语义用例 ───────────────────────────────────────────────────
#
# 每个用例是一串 step，全部 step 都要过。step 的键：
#   code      要执行的 C#
#   reset     这次调用前先清会话状态
#   text      与返回文本**完全相等**（去空白）
#   contains  返回文本必须含的子串
#   regex     返回文本必须匹配的正则
#   is_error  期望的 isError（默认 False）
#
# ⚠️ 用例之间共享会话状态，**顺序有意义**（前面的 a/b/l 后面要用）。

CASES = [
    {
        "name": "表达式",
        "steps": [{"code": "1 + 1", "reset": True, "text": "2"}],
    },
    {
        "name": "语句 + 裸尾表达式",
        "steps": [{"code": "int a = 1; int b = 2; a + b", "text": "3"}],
    },
    {
        "name": "集合 + 裸尾表达式",
        "steps": [{"code": "var l = new List<int> { 1, 2, 3 }; l.Count", "text": "3"}],
    },
    {
        "name": "LINQ 扩展方法",
        "steps": [{"code": "l.Where(x => x > 1).Count()", "text": "2"}],
    },
    {
        "name": "for 循环",
        "steps": [{"code": "int c = 0; for (int i = 0; i < 4; i++) { c += i; } c", "text": "6"}],
    },
    {
        "name": "if/else",
        "steps": [{"code": "int d = 0; if (true) { d = 7; } else { d = 9; } d", "text": "7"}],
    },
    {
        "name": "无值语句",
        "steps": [{"code": "int x = 5;", "contains": "no value returned"}],
    },
    {
        # 探针：返回 null 与「无值」必须是两件事。哨兵判断错了这里就会变成「无值」。
        "name": "返回 null ≠ 无值",
        "steps": [{"code": "string s = null; s", "text": "null"}],
    },
    {
        "name": "跨调用变量仍在",
        "steps": [{"code": "a + b + l.Count", "text": "6"}],
    },
    {
        "name": "定义 class",
        "steps": [
            {
                "code": "class Selftest9 { public static int N; public static void Inc() { N++; } }",
                "contains": "no value returned",
            }
        ],
    },
    {
        # 本次改动的头条：声明与语句可以在同一次调用里。
        "name": "声明 + 语句（同一次调用）",
        "steps": [
            {"code": "class Multi9 { public static int V() { return 42; } }\nMulti9.V()", "text": "42"}
        ],
    },
    {
        "name": "using + 语句（同一次调用）",
        "steps": [
            {"code": 'using System.Text;\nnew StringBuilder("ok").Append("!").ToString()', "text": "ok!"}
        ],
    },
    {
        # 4 段交替，练 RunAsSubmissions 的循环第二、三轮。
        "name": "交替 4 段",
        "steps": [
            {
                "code": "class P9 { public static int V = 1; }\n"
                        "int t9 = 1;\n"
                        "class Q9 { public static int V = 2; }\n"
                        "P9.V + Q9.V + t9",
                "text": "4",
            }
        ],
    },
    {
        # 只声明类型时编译器不返回可执行方法（compiled == null）。把它当失败就会误报。
        "name": "单独声明（不是报错）",
        "steps": [{"code": "class Solo9 { }", "contains": "no value returned"}],
    },
    {
        "name": "void 调用（不带分号）",
        "steps": [{"code": "Selftest9.Inc()", "contains": "no value returned"}],
    },
    {
        "name": "void 调用（带分号）",
        "steps": [{"code": "Selftest9.Inc();", "contains": "no value returned"}],
    },
    {
        # 探针：副作用只发生一次。必须跨调用 —— 同一次 snippet 内自增，跑两遍也是同一个数。
        "name": "副作用只发生一次",
        "steps": [
            {"code": "Selftest9.N = 0;", "contains": "no value returned"},
            {"code": "Selftest9.N = Selftest9.N + 1;", "contains": "no value returned"},
            {"code": "Selftest9.N", "text": "1"},
        ],
    },
    {
        # 报错行号必须是**原始行号**（第 2 行），不是被切出来的那一段的行号。
        "name": "编译错误（混合，原始行号）",
        "steps": [
            {
                "code": "class Err9 { }\nint bad = ;",
                "is_error": True,
                "contains": "Compilation failed:",
                "regex": r"\(2,\d+\): error CS1525",
            }
        ],
    },
    {
        "name": "运行时异常",
        "steps": [
            {
                "code": "int z = 0; int w = 1 / z; w",
                "is_error": True,
                "contains": "Execution failed:",
            }
        ],
    },
    {
        "name": "输入不完整",
        "steps": [
            {"code": "int incomplete9 = ", "is_error": True, "contains": "Incomplete input"}
        ],
    },
]


def check_expectation(expect, is_error, text):
    """返回「哪里不符」的说明列表；空列表表示通过。"""
    problems = []

    want_error = expect.get("is_error", False)
    if is_error != want_error:
        problems.append("isError={}（期望 {}）".format(is_error, want_error))

    stripped = text.strip()

    if "text" in expect and stripped != expect["text"]:
        problems.append("文本={!r}（期望 {!r}）".format(stripped, expect["text"]))

    if "contains" in expect and expect["contains"] not in text:
        problems.append("文本不含 {!r}：{!r}".format(expect["contains"], stripped))

    if "regex" in expect and not re.search(expect["regex"], text):
        problems.append("文本不匹配 /{}/：{!r}".format(expect["regex"], stripped))

    return problems


def run_case(url, case, timeout):
    """跑一个用例；返回 (是否通过, 失败说明)。"""
    for index, step in enumerate(case["steps"], start=1):
        arguments = {"code": step["code"]}
        if step.get("reset"):
            arguments["reset"] = True

        try:
            is_error, text = execute_csharp(url, arguments, timeout)
        except Failure as error:
            return False, "step {} 调用失败：{}".format(index, error)

        problems = check_expectation(step, is_error, text)
        if problems:
            label = "step {}".format(index) if len(case["steps"]) > 1 else ""
            return False, "{} {}\n          实际：isError={} text={!r}".format(
                label, "; ".join(problems), is_error, text.strip()
            )

    return True, ""


# ── 主流程 ────────────────────────────────────────────────────────────────────

def main():
    try:
        sys.stdout.reconfigure(encoding="utf-8")
    except Exception:
        pass

    parser = argparse.ArgumentParser(description="MelonMCP 自检（传输 + execute_csharp 语义回归）")
    parser.add_argument("--url", default=DEFAULT_URL, help="MCP streamable-http 端点（默认 %(default)s）")
    parser.add_argument("--timeout", type=float, default=30.0, help="单次 HTTP 超时秒数（默认 %(default)s）")
    parser.add_argument("-v", "--verbose", action="store_true", help="打印每条响应的原文")
    parser.add_argument("--only", default=None, help="只跑名字含该子串的用例")
    args = parser.parse_args()

    global VERBOSE
    VERBOSE = args.verbose

    print("== MelonMCP 自检 == {}".format(args.url))
    print()

    # 0. 存活探测。GET 回 405 就说明在监听，连不上说明游戏没跑。
    try:
        http_request(args.url, "GET", None, {"Accept": "*/*"}, min(args.timeout, 10))
    except OSError as error:
        print("[{}] 连不上 {}：{}".format(BAD, args.url, error))
        print("      → 游戏没在运行，或 MelonMCP 的 27015 端口没监听")
        return 2

    passed_total = 0
    failed_total = 0
    failures = []

    def report(name, ok, detail=""):
        nonlocal passed_total, failed_total
        mark = "PASS" if ok else "FAIL"
        print("  [{}] {}".format(mark, name))
        if not ok:
            failed_total += 1
            failures.append((name, detail))
            for line in detail.splitlines():
                print("         {}".format(line))
        else:
            passed_total += 1

    # 1. 传输层
    print("[传输层]")
    for name, fn in transport_checks(args.url, args.timeout):
        try:
            fn()
            report(name, True)
        except Failure as error:
            report(name, False, str(error))
        except OSError as error:
            report(name, False, "连接异常：{}".format(error))

    # 2. 握手
    print()
    print("[握手]")
    try:
        result = rpc(
            args.url,
            "initialize",
            {
                "protocolVersion": "2025-03-26",
                "capabilities": {},
                "clientInfo": {"name": "mcp_selftest", "version": "1"},
            },
            args.timeout,
        ) or {}
        report("initialize（协商到 {}）".format(result.get("protocolVersion")), bool(result.get("protocolVersion")),
               "" if result.get("protocolVersion") else "响应里没有 protocolVersion")
    except Failure as error:
        report("initialize", False, str(error))

    try:
        listing = rpc(args.url, "tools/list", {}, args.timeout) or {}
        names = [tool.get("name") for tool in listing.get("tools", [])]
        has_target = "execute_csharp" in names
        report("tools/list（{} 个工具，{}）".format(len(names), "含 execute_csharp" if has_target else "缺 execute_csharp"),
               has_target, "" if has_target else "返回的工具名里没有 execute_csharp：{}".format(names[:10]))
    except Failure as error:
        report("tools/list", False, str(error))

    # 3. 语义用例
    print()
    print("[execute_csharp 语义]")
    selected = [case for case in CASES if not args.only or args.only in case["name"]]
    if not selected:
        print("  （--only {} 没匹配到任何用例）".format(args.only))

    for case in selected:
        ok, detail = run_case(args.url, case, args.timeout)
        report(case["name"], ok, detail)

    # 4. 收尾：把测试留下的变量/类清掉，尽量别污染后续会话。
    try:
        execute_csharp(args.url, {"code": "0", "reset": True}, args.timeout)
    except Exception:
        pass

    print()
    total = passed_total + failed_total
    if failed_total == 0:
        print("RESULT: {}/{} 通过".format(passed_total, total))
        return 0

    print("RESULT: {}/{} 通过，{} 失败".format(passed_total, total, failed_total))
    for name, detail in failures:
        print("  - {}：{}".format(name, detail.splitlines()[0] if detail else ""))
    return 1


if __name__ == "__main__":
    sys.exit(main())
