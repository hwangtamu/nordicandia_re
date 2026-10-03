using System.Text;
using System.Text.Json;

/// <summary>
/// B05: diff report over the B04 sample library. Consumes
/// <see cref="FidelityReplayTests.SampleResult"/> and emits a machine-readable JSON report
/// plus a human-readable Markdown report under <c>fidelity-reports/</c> in the working
/// directory. Failing samples link back to their B03 difference ID (<c>diffId</c>) when the
/// sample carries one, otherwise to the recovery <c>source</c> document.
/// </summary>
static class FidelityReport
{
    public static void Generate(List<FidelityReplayTests.SampleResult> results)
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "fidelity-reports");
        Directory.CreateDirectory(dir);
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var pass = results.Count(r => r.Ok);

        var jsonPath = Path.Combine(dir, "fidelity_report.json");
        using (var json = new FileStream(jsonPath, FileMode.Create, FileAccess.Write))
        using (var writer = new Utf8JsonWriter(json, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("generatedAt", stamp);
            writer.WriteString("baseline", "Android 1.9.3 (507033)");
            writer.WriteNumber("total", results.Count);
            writer.WriteNumber("pass", pass);
            writer.WriteNumber("fail", results.Count - pass);
            writer.WriteStartArray("samples");
            foreach (var r in results)
            {
                writer.WriteStartObject();
                writer.WriteString("id", r.Id);
                writer.WriteString("kind", r.Kind);
                writer.WriteString("status", r.Ok ? "pass" : (r.Error is null ? "fail" : "error"));
                writer.WriteString("actual", r.Actual);
                if (r.Error is not null) writer.WriteString("error", r.Error);
                writer.WriteString("source", r.Source);
                if (r.DiffId is not null) writer.WriteString("diffId", r.DiffId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var md = new StringBuilder();
        md.AppendLine("# B05 差异报告");
        md.AppendLine();
        md.AppendLine($"- 生成时间：{stamp}");
        md.AppendLine("- 基线：Android 1.9.3（507033）");
        md.AppendLine($"- 结果：{pass}/{results.Count} 通过");
        md.AppendLine();
        md.AppendLine("| 样本 | 类型 | 状态 | 实际值 | 证据 |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var r in results)
        {
            var status = r.Ok ? "✅" : (r.Error is null ? "❌" : "💥");
            var evidence = r.DiffId is not null ? $"B03 {r.DiffId}" : r.Source;
            var actual = string.IsNullOrEmpty(r.Actual) ? (r.Error ?? "") : r.Actual;
            md.AppendLine($"| {r.Id} | {r.Kind} | {status} | `{actual}` | {evidence} |");
        }
        var failures = results.Where(r => !r.Ok).ToList();
        if (failures.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("## 失败样本与 B03 关联");
            md.AppendLine();
            foreach (var r in failures)
            {
                var link = r.DiffId is not null
                    ? $"对应 B03 台账 {r.DiffId}"
                    : $"证据文档：{r.Source}";
                md.AppendLine($"- **{r.Id}**（{r.Kind}）：{link}。实际值 `{r.Actual}`。{(r.Error is null ? "" : "错误：" + r.Error)}");
            }
        }
        else
        {
            md.AppendLine();
            md.AppendLine("无失败样本。");
        }
        File.WriteAllText(Path.Combine(dir, "fidelity_report.md"), md.ToString(), new UTF8Encoding(false));
        Console.WriteLine($"fidelity report: {jsonPath}");
    }
}
