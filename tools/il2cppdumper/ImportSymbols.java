// 用 Il2CppDumper 的 script.json 把「类名.方法名」写回 Ghidra，让反汇编可读。
//
// 为什么用 .java 而不是 .py：
//   Ghidra 12.x 把 .py 路由给 PyGhidra；即使装了 PyGhidra，Jython 时代的
//   脚本（ghidra_with_struct.py）也有 Python2/3 语法与 API 差异。
//   Java 版走 GhidraScript 原生 API，最稳。
//
// 用法（analyzeHeadless）：
//   -postScript ImportSymbols.java <script.json 路径>
//
// 与 ghidra_with_struct.py 的差异：
//   - 不调用 askFile()（headless 下会卡住），改为从 getScriptArgs() 取路径
//   - 只做「命名」，不做签名解析（签名解析要 CParser，失败率高且慢）
//   - 同名时自动加后缀，避免 CodeUnitInsertionException 刷屏

import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.ArrayList;
import java.util.List;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.symbol.SourceType;

public class ImportSymbols extends GhidraScript {

    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        if (args.length < 1) {
            println("ImportSymbols: 需要参数 <script.json 路径>");
            return;
        }

        File f = new File(args[0]);
        if (!f.isFile()) {
            println("ImportSymbols: 找不到 " + args[0]);
            return;
        }

        String json = new String(Files.readAllBytes(f.toPath()), StandardCharsets.UTF_8);

        // 极简 JSON 抽取：不引入 JSON 库，直接正则抓 ScriptMethod 数组里的对象。
        // script.json 的条目形如：
        //   {"Address":2185888,"Name":"GetText","Signature":"...","TypeSignature":"Locale"}
        // Address 是 RVA（相对 imageBase）。
        int idx = json.indexOf("\"ScriptMethod\"");
        if (idx < 0) {
            println("ImportSymbols: script.json 里没有 ScriptMethod");
            return;
        }
        int end = json.indexOf("\"ScriptString\"", idx);
        String block = end > 0 ? json.substring(idx, end) : json.substring(idx);

        Pattern p = Pattern.compile(
            "\\{\\s*\"Address\"\\s*:\\s*(\\d+)\\s*,\\s*\"Name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""
            + "\\s*,\\s*\"Signature\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""
            + "\\s*,\\s*\"TypeSignature\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"\\s*\\}");

        Matcher m = p.matcher(block);

        int total = 0, named = 0, skipped = 0;
        long imageBase = currentProgram.getImageBase().getOffset();

        List<String> failures = new ArrayList<>();

        while (m.find()) {
            total++;
            long rva = Long.parseLong(m.group(1));
            String name = unescape(m.group(2));
            String type = unescape(m.group(4));

            if (name == null || name.isEmpty()) {
                skipped++;
                continue;
            }

            String full = (type == null || type.isEmpty()) ? name : type + "." + name;
            // Ghidra 符号名不允许空格/尖括号等；做一次保守清洗。
            full = full.replace(' ', '-')
                       .replace('<', '_').replace('>', '_')
                       .replace(',', '_').replace('[', '_').replace(']', '_');
            if (full.length() > 200) {
                full = full.substring(0, 200);
            }

            try {
                Address a = toAddr(imageBase + rva);
                if (a == null) {
                    skipped++;
                    continue;
                }

                // createLabel(addr, name, makePrimary, sourceType)
                // makePrimary=false：不抢已有的（如函数入口名），避免冲突。
                try {
                    createLabel(a, full, false, SourceType.USER_DEFINED);
                    named++;
                }
                catch (Exception dup) {
                    // 同名已存在 —— 加地址后缀，保证不刷异常。
                    try {
                        createLabel(a, full + "_" + Long.toHexString(rva), false, SourceType.USER_DEFINED);
                        named++;
                    }
                    catch (Exception e2) {
                        skipped++;
                    }
                }
            }
            catch (Exception ex) {
                skipped++;
                if (failures.size() < 10) {
                    failures.add(full + " @ " + Long.toHexString(rva) + " : " + ex.getMessage());
                }
            }

            if ((total % 5000) == 0) {
                println("  ...已处理 " + total + " 条（命名 " + named + "）");
            }
        }

        println("ImportSymbols 完成：共 " + total + " 条，命名 " + named + "，跳过 " + skipped);
        for (String s : failures) {
            println("  失败样例: " + s);
        }
    }

    /** 还原 JSON 字符串里的转义（只处理常见的几种）。 */
    private static String unescape(String s) {
        if (s == null) {
            return null;
        }
        StringBuilder sb = new StringBuilder(s.length());
        for (int i = 0; i < s.length(); i++) {
            char c = s.charAt(i);
            if (c == '\\' && i + 1 < s.length()) {
                char n = s.charAt(++i);
                switch (n) {
                    case 'n': sb.append('\n'); break;
                    case 't': sb.append('\t'); break;
                    case 'r': sb.append('\r'); break;
                    case '"': sb.append('"'); break;
                    case '\\': sb.append('\\'); break;
                    case '/': sb.append('/'); break;
                    default: sb.append(n); break;
                }
            } else {
                sb.append(c);
            }
        }
        return sb.toString();
    }
}
