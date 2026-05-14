using System.Globalization;
using System.Text;
using NPOI.SS.UserModel;

namespace Server.Services.Ai;

/// <summary>
/// Converts Excel-style spreadsheets (.xls / .xlsx / .xlsm) to plain text so
/// they can be fed to LLMs that only accept images, PDFs or text. We emit a
/// TSV-like dump with one section per sheet — readable enough for the model
/// to interpret it as tabular data without quoting heuristics.
/// </summary>
public static class SpreadsheetConverter
{
    /// <summary>Cap on the produced text. Big workbooks would otherwise blow the
    /// context window. Truncation is signalled at the end of the output.</summary>
    public const int MaxOutputBytes = 512 * 1024;

    private static readonly HashSet<string> SpreadsheetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".xls", ".xlsx", ".xlsm",
    };

    private static readonly HashSet<string> SpreadsheetContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.ms-excel",
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        "application/vnd.ms-excel.sheet.macroenabled.12",
        "application/x-excel",
        "application/x-msexcel",
    };

    public static bool IsSpreadsheet(string? fileName, string? contentType, byte[] bytes)
    {
        if (!string.IsNullOrEmpty(fileName))
        {
            var ext = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(ext) && SpreadsheetExtensions.Contains(ext))
                return true;
        }

        if (!string.IsNullOrEmpty(contentType) && SpreadsheetContentTypes.Contains(contentType.Trim()))
            return true;

        // OLE2 header (legacy .xls). ZIP container alone is not enough to claim
        // .xlsx because .docx/.pptx share it — we rely on the extension or MIME
        // for OOXML formats.
        if (bytes.Length >= 8
            && bytes[0] == 0xD0 && bytes[1] == 0xCF && bytes[2] == 0x11 && bytes[3] == 0xE0
            && bytes[4] == 0xA1 && bytes[5] == 0xB1 && bytes[6] == 0x1A && bytes[7] == 0xE1)
        {
            return true;
        }

        return false;
    }

    public static string ConvertToText(byte[] bytes, string? fileName = null)
    {
        using var ms = new MemoryStream(bytes, writable: false);
        using var workbook = WorkbookFactory.Create(ms);

        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(fileName))
            sb.Append("# Archivo: ").AppendLine(fileName);
        sb.Append("# Hojas: ").Append(workbook.NumberOfSheets).AppendLine();
        sb.AppendLine();

        bool truncated = false;
        for (int s = 0; s < workbook.NumberOfSheets; s++)
        {
            if (sb.Length >= MaxOutputBytes) { truncated = true; break; }

            var sheet = workbook.GetSheetAt(s);
            if (sheet is null) continue;

            sb.Append("## Hoja: ").AppendLine(sheet.SheetName);

            int lastRow = sheet.LastRowNum;
            for (int r = sheet.FirstRowNum; r <= lastRow; r++)
            {
                if (sb.Length >= MaxOutputBytes) { truncated = true; break; }

                var row = sheet.GetRow(r);
                if (row is null)
                {
                    sb.AppendLine();
                    continue;
                }

                int lastCell = row.LastCellNum; // exclusive
                if (lastCell <= 0)
                {
                    sb.AppendLine();
                    continue;
                }

                for (int c = 0; c < lastCell; c++)
                {
                    if (c > 0) sb.Append('\t');
                    sb.Append(FormatCell(row.GetCell(c)));
                }
                sb.AppendLine();
            }

            sb.AppendLine();
        }

        if (truncated)
            sb.AppendLine($"[...] Salida truncada en {MaxOutputBytes} bytes para mantener el contexto manejable.");

        return sb.ToString();
    }

    private static string FormatCell(ICell? cell)
    {
        if (cell is null) return "";
        try
        {
            return cell.CellType switch
            {
                CellType.String => Sanitize(cell.StringCellValue),
                CellType.Numeric => FormatNumericCell(cell),
                CellType.Boolean => cell.BooleanCellValue ? "true" : "false",
                CellType.Formula => FormatFormulaCell(cell),
                CellType.Blank => "",
                CellType.Error => "#ERROR",
                _ => Sanitize(cell.ToString() ?? ""),
            };
        }
        catch
        {
            return Sanitize(cell.ToString() ?? "");
        }
    }

    private static string FormatNumericCell(ICell cell)
    {
        if (DateUtil.IsCellDateFormatted(cell))
        {
            try
            {
                var dt = cell.DateCellValue;
                if (dt.HasValue)
                    return dt.Value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            catch
            {
                // fall through to numeric formatting
            }
        }
        return cell.NumericCellValue.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatFormulaCell(ICell cell)
    {
        try
        {
            return cell.CachedFormulaResultType switch
            {
                CellType.String => Sanitize(cell.StringCellValue),
                CellType.Numeric => FormatNumericCell(cell),
                CellType.Boolean => cell.BooleanCellValue ? "true" : "false",
                CellType.Error => "#ERROR",
                _ => "",
            };
        }
        catch
        {
            return cell.CellFormula ?? "";
        }
    }

    /// <summary>Strip tabs / newlines so a single cell never breaks the row layout.</summary>
    private static string Sanitize(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { '\t', '\n', '\r' }) < 0) return s;
        return s.Replace('\t', ' ').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');
    }
}
