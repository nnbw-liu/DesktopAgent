using System.IO;
using System.Text;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace SDemo.DesktopAgent.Sandbox.Tests;

/// <summary>
/// 测试用 PDF 样本：用 PdfPig 的 writer + Standard 14 字体生成，避免把二进制样本提交进仓库。
/// 中文字形不在 Standard 14 里，因此样本只用 ASCII 文本。
/// </summary>
internal static class PdfFixture
{
    public static byte[] Create(params string[] pageTexts)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var text in pageTexts)
        {
            var page = builder.AddPage(PageSize.A4);
            page.AddText(text, 12, new PdfPoint(50, 750), font);
        }

        return builder.Build();
    }

    public static string CreateFile(string directory, string name, params string[] pageTexts)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllBytes(path, Create(pageTexts));
        return path;
    }

    /// <summary>没有文本层的样本（等价于扫描件：只有空白页）。</summary>
    public static byte[] CreateBlank(int pages)
    {
        var builder = new PdfDocumentBuilder();

        for (var index = 0; index < pages; index++)
        {
            builder.AddPage(PageSize.A4);
        }

        return builder.Build();
    }

    /// <summary>
    /// 带 <c>/Encrypt</c> 但无法用空密码解开的 PDF：只用于验证“加密”分支被识别，
    /// 不是真正由加密工具生成的文件。
    /// </summary>
    public static byte[] CreateEncrypted()
    {
        // 最小结构：Catalog / Pages / Encrypt 字典；没有内容流，因此不需要真实加密数据。
        const string pdf =
            "%PDF-1.4\n"
            + "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n"
            + "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n"
            + "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>\nendobj\n"
            + "4 0 obj\n<< /Filter /Standard /V 1 /R 2 /O <"
            + "0000000000000000000000000000000000000000000000000000000000000000"
            + "> /U <"
            + "1111111111111111111111111111111111111111111111111111111111111111"
            + "> /P -1 >>\nendobj\n"
            + "trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R /ID [<00><00>] >>\n%%EOF\n";

        return Encoding.ASCII.GetBytes(pdf);
    }
}
