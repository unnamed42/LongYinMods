using System;
using System.IO;
using System.Security.Cryptography;

namespace ForceOverflowDividend
{
    /// <summary>
    /// 算程序集文件的 md5，启动日志里打印。
    ///
    /// <para>用途：部署后「跑的是不是新产物」这个问题，
    /// 现在可以用**日志里的 md5 vs <c>md5sum</c> 输出**直接对账，
    /// 不必再去翻文件（见 AGENTS.md §3.2）。</para>
    ///
    /// <para>⚠️ 这之所以成立，是因为 csproj 里 <c>Deterministic</c> 真正生效了：
    /// 同一份源码两次构建逐字节相同 → md5 稳定 → 能用来比较。
    /// 之前 csproj 把构建时刻写进 <c>InformationalVersion</c>，
    /// 每次都变，md5 根本没法比 —— 已移除（那是程序集内容）。</para>
    ///
    /// <para>失败绝不影响启动（见 AGENTS.md §3.4：诊断代码不得抛异常）。</para>
    /// </summary>
    internal static class AssemblyVersionHash
    {
        /// <summary>文件 md5（小写十六进制）；失败返回 "?"。</summary>
        internal static string OfFile(string path)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(path))
                {
                    byte[] hash = md5.ComputeHash(stream);
                    var sb = new System.Text.StringBuilder(hash.Length * 2);

                    foreach (byte b in hash)
                    {
                        sb.Append(b.ToString("x2"));
                    }

                    return sb.ToString();
                }
            }
            catch
            {
                return "?";
            }
        }
    }
}
