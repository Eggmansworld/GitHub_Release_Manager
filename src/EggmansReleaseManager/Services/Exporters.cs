using System.Globalization;
using System.Security;
using System.Text;
using System.Text.Json;
using EggmansReleaseManager.Models;

namespace EggmansReleaseManager.Services;

public sealed record MetadataDirs(string Root, string Json, string Csv, string Dat, string Pdf, string Logs);

/// <summary>One downloaded-asset record, used for the per-run JSON/CSV log (same fields as v3).</summary>
public sealed class AssetRecord
{
    public string Repo { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Name { get; set; } = "";
    public long SizeBytes { get; set; }
    public string Url { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Sha1 { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; } = "";
}

public static class Exporters
{
    public static MetadataDirs EnsureMetadataDirs(string rootFolder)
    {
        var md = Path.Combine(rootFolder, "_metadata");
        var d = new MetadataDirs(md, Path.Combine(md, "json"), Path.Combine(md, "csv"), Path.Combine(md, "dat"),
            Path.Combine(md, "pdf"), Path.Combine(md, "logs"));
        foreach (var p in new[] { d.Root, d.Json, d.Csv, d.Dat, d.Pdf, d.Logs }) Directory.CreateDirectory(p);
        return d;
    }

    /// <summary>Move stray JSON/CSV/DAT/PDF/TXT/LOG files from the download root into _metadata (v3 behaviour).</summary>
    public static int MigrateLegacyMetadata(string rootFolder)
    {
        if (string.IsNullOrWhiteSpace(rootFolder) || !Directory.Exists(rootFolder)) return 0;
        var d = EnsureMetadataDirs(rootFolder);
        var moved = 0;
        foreach (var file in Directory.EnumerateFiles(rootFolder))
        {
            var ext = Path.GetExtension(file).ToLowerInvariant();
            var target = ext switch
            {
                ".json" => d.Json,
                ".csv" => d.Csv,
                ".dat" => d.Dat,
                ".pdf" => d.Pdf,
                ".txt" or ".log" => d.Logs,
                _ => null,
            };
            if (target is null) continue;
            try { File.Move(file, Path.Combine(target, Path.GetFileName(file)), true); moved++; }
            catch { /* ignore */ }
        }
        return moved;
    }

    public static (string JsonPath, string CsvPath) WriteRunLog(string rootFolder, string repoKey, IReadOnlyList<AssetRecord> records)
    {
        var ts = Stamp.Now();
        var safe = AppPaths.RepoFolderName(repoKey);
        var d = EnsureMetadataDirs(rootFolder);
        var jsonPath = Path.Combine(d.Json, $"{safe}_assets_{ts}.json");
        var csvPath = Path.Combine(d.Csv, $"{safe}_assets_{ts}.csv");

        Json.SaveAtomic(jsonPath, new { GeneratedAtUtc = ts, Repo = repoKey, Assets = records });

        var sb = new StringBuilder();
        sb.AppendLine("repo,tag,name,size_bytes,url,path,sha1,success,error");
        foreach (var r in records)
            sb.AppendLine(string.Join(',', Csv(r.Repo), Csv(r.Tag), Csv(r.Name), r.SizeBytes.ToString(CultureInfo.InvariantCulture),
                Csv(r.Url), Csv(r.Path), Csv(r.Sha1 ?? ""), r.Success ? "1" : "0", Csv(r.Error)));
        File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(false));
        return (jsonPath, csvPath);
    }

    private static string Csv(string s) =>
        s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;

    /// <summary>
    /// RomVault-compatible XML DAT. Game = release tag; files below the tag folder keep their relative path.
    /// CRCs are computed (streamed) when not cached and cached back into the DB.
    /// </summary>
    public static string GenerateDat(string repoKey, RepoInfo info, string rootFolder, CancellationToken ct = default)
    {
        var today = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var safe = AppPaths.RepoFolderName(repoKey);
        var d = EnsureMetadataDirs(rootFolder);
        var datPath = Path.Combine(d.Dat, $"{safe}.dat");

        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<datafile>\n\t<header>\n");
        sb.Append($"\t\t<name>{X(repoKey)}</name>\n");
        sb.Append("\t\t<description>retrieved from GitHub</description>\n");
        sb.Append($"\t\t<version>{today}</version>\n");
        sb.Append("\t\t<author>Eggman</author>\n\t\t<romvault/>\n\t</header>\n");

        foreach (var (tag, files) in info.Assets)
        {
            var tagFolder = Path.Combine(rootFolder, safe, AppPaths.SafeTagFolder(tag));
            var roms = new List<string>();
            foreach (var (name, meta) in files)
            {
                ct.ThrowIfCancellationRequested();
                var full = meta.Path;
                if (string.IsNullOrEmpty(full) || !File.Exists(full)) continue;

                var crc = meta.Crc;
                if (string.IsNullOrEmpty(crc))
                {
                    try { crc = Hashing.Crc32File(full, ct); meta.Crc = crc; }
                    catch (OperationCanceledException) { throw; }
                    catch { crc = "00000000"; }
                }

                string rel;
                try { rel = Path.GetRelativePath(tagFolder, full); }
                catch { rel = name; }
                if (rel.StartsWith("..", StringComparison.Ordinal)) rel = name;
                rel = rel.Replace('\\', '/');

                var sha1 = (meta.Sha1 ?? "").ToLowerInvariant();
                roms.Add($"\t\t<rom name=\"{X(rel)}\" size=\"{meta.SizeBytes}\" crc=\"{crc}\" sha1=\"{sha1}\"/>\n");
            }
            if (roms.Count == 0) continue;
            sb.Append($"\t<game name=\"{X(tag)}\">\n");
            foreach (var r in roms) sb.Append(r);
            sb.Append("\t</game>\n");
        }
        sb.Append("</datafile>\n");
        File.WriteAllText(datPath, sb.ToString(), new UTF8Encoding(false));
        return datPath;
    }

    private static string X(string s) => SecurityElement.Escape(s) ?? "";

    public static string GenerateSummaryPdf(string repoKey, RepoInfo info, string rootFolder)
    {
        var ts = Stamp.Now();
        var safe = AppPaths.RepoFolderName(repoKey);
        var d = EnsureMetadataDirs(rootFolder);
        var path = Path.Combine(d.Pdf, $"{safe}_summary_{ts}.pdf");

        var lines = new List<string>();
        foreach (var (tag, files) in info.Assets.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var (name, meta) in files.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
                lines.Add($"{tag} / {name}  size={meta.SizeBytes}  sha1={meta.Sha1}");
        if (lines.Count == 0) lines.Add("(no assets recorded)");

        var subtitle = $"Generated {ts}  -  {info.FileCount} files, {Fmt.Bytes(info.TotalBytes)}";
        MiniPdf.WriteTextReport(path, $"GitHub Release Summary: {repoKey}", subtitle, lines);
        return path;
    }

    /// <summary>After a local scan, recreate {repo}_assets.json/.csv when missing (v3 behaviour).</summary>
    public static (bool Json, bool Csv) RebuildMetadataIfMissing(string repoKey, RepoInfo info, string rootFolder)
    {
        var d = EnsureMetadataDirs(rootFolder);
        var safe = AppPaths.RepoFolderName(repoKey);
        var jsonPath = Path.Combine(d.Json, $"{safe}_assets.json");
        var csvPath = Path.Combine(d.Csv, $"{safe}_assets.csv");
        var jsonMissing = !File.Exists(jsonPath);
        var csvMissing = !File.Exists(csvPath);

        if (jsonMissing || csvMissing)
        {
            Json.SaveAtomic(jsonPath, new { Repo = repoKey, LastRebuild = Stamp.Now(), info.Assets });
            var sb = new StringBuilder("tag,filename,size_bytes,sha1,path\n");
            foreach (var (tag, files) in info.Assets)
                foreach (var (fn, meta) in files)
                    sb.Append(string.Join(',', Csv(tag), Csv(fn), meta.SizeBytes, Csv(meta.Sha1 ?? ""), Csv(meta.Path ?? ""))).Append('\n');
            File.WriteAllText(csvPath, sb.ToString(), new UTF8Encoding(false));
        }
        return (jsonMissing, csvMissing);
    }

    public static void ExportDb(RepoDb db, string path) => Json.SaveAtomic(path, db);
}
