using System.Globalization;
using System.Text;

namespace EggmansReleaseManager.Services;

/// <summary>
/// Tiny dependency-free PDF writer: Letter pages of monospaced text (Courier), with a bold title.
/// Enough for the per-repo summary report that v3 produced with reportlab.
/// </summary>
public static class MiniPdf
{
    public static void WriteTextReport(string path, string title, string subtitle, IReadOnlyList<string> lines)
    {
        const double pageW = 612, pageH = 792, margin = 40, lineH = 10, fontSize = 7.5;
        var firstPageStart = pageH - margin - 50;
        var perFirst = (int)((firstPageStart - margin) / lineH);
        var perPage = (int)((pageH - 2 * margin) / lineH);

        var pages = new List<string>();
        var idx = 0;
        var first = true;
        do
        {
            var sb = new StringBuilder();
            sb.Append("BT\n");
            double y;
            int count;
            if (first)
            {
                sb.Append(F($"/F2 14 Tf {margin} {pageH - margin - 14} Td ({Esc(title)}) Tj\n"));
                sb.Append(F($"/F1 9 Tf 0 -18 Td ({Esc(subtitle)}) Tj\n"));
                sb.Append("ET\nBT\n");
                y = firstPageStart;
                count = perFirst;
            }
            else
            {
                y = pageH - margin;
                count = perPage;
            }
            sb.Append(F($"/F1 {fontSize} Tf {lineH} TL {margin} {y} Td\n"));
            for (var i = 0; i < count && idx < lines.Count; i++, idx++)
                sb.Append('(').Append(Esc(lines[idx])).Append(") Tj T*\n");
            sb.Append("ET\n");
            pages.Add(sb.ToString());
            first = false;
        } while (idx < lines.Count);

        // Objects: 1 catalog, 2 pages, 3 font F1, 4 font F2, then (content, page) pairs.
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "", // pages placeholder
            "<< /Type /Font /Subtype /Type1 /BaseFont /Courier /Encoding /WinAnsiEncoding >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>",
        };
        var kids = new List<string>();
        foreach (var content in pages)
        {
            var bytes = Latin1(content);
            objs.Add($"<< /Length {bytes.Length} >>\nstream\n{content}endstream");
            var contentId = objs.Count;
            objs.Add(F($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {pageW} {pageH}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentId} 0 R >>"));
            kids.Add($"{objs.Count} 0 R");
        }
        objs[1] = $"<< /Type /Pages /Kids [{string.Join(' ', kids)}] /Count {kids.Count} >>";

        using var ms = new MemoryStream();
        void W(string s) { var b = Latin1(s); ms.Write(b, 0, b.Length); }
        W("%PDF-1.4\n");
        var offsets = new List<long>();
        for (var i = 0; i < objs.Count; i++)
        {
            offsets.Add(ms.Position);
            W($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        var xref = ms.Position;
        W($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static string F(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);

    private static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    private static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c is '(' or ')' or '\\') sb.Append('\\').Append(c);
            else if (c < 32 || c > 255) sb.Append('?');
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
