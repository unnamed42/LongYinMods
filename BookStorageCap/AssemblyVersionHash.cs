using System.IO;
using System.Security.Cryptography;

namespace Unnamed42.BookStorageCap
{
    /// <summary>
    /// 算程序集文件的 md5，启动日志里打印 —— 部署后「跑的是不是新产物」，
    /// 用日志里的 md5 与 <c>md5sum</c> 输出直接对账（AGENTS.md §3.2）。
    ///
    /// <para>⚠️ 这之所以成立，是因为 csproj 里 <c>Deterministic</c> 真生效了：
    /// 源码里没有构建时刻/机器名之类的程序集内容。别往
    /// Version / InformationalVersion / AssemblyMetadata 里塞时间戳（§3.2.0）。</para>
    ///
    /// <para>失败绝不影响启动（§3.4：诊断代码不得抛异常）。</para>
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
