using System.IO;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

namespace QPGL.Desktop.Services;

/// <summary>Excel 解析结果（表头 + 数据行 + 工作表名，qp11 ExcelParseResult 同构）。</summary>
public sealed class ExcelParseResult
{
    public List<string> Headers { get; set; } = [];
    public List<List<string?>> Rows { get; set; } = [];
    public int TotalRows { get; set; }
    public List<string> SheetNames { get; set; } = [];
}

/// <summary>
/// Excel 解析服务（qp11 ExcelParserService 同构）：xls=NPOI HSSF / xlsx=XSSF，
/// 取单元格原文（数值整数去小数点防漂移、日期转 yyyy-MM-dd、公式取缓存值），空行跳过。
/// </summary>
public sealed class ExcelParserService
{
    public ExcelParseResult Parse(string filePath, int sheetIndex = 0, int headerRowIndex = 0, int startDataRow = 1)
    {
        var result = new ExcelParseResult();

        using var stream = OpenFileForRead(filePath);
        IWorkbook workbook;

        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext == ".xls")
            workbook = new HSSFWorkbook(stream);
        else if (ext == ".xlsx")
            workbook = new XSSFWorkbook(stream);
        else
            throw new NotSupportedException($"不支持的文件格式: {ext}");

        for (var i = 0; i < workbook.NumberOfSheets; i++)
            result.SheetNames.Add(workbook.GetSheetName(i));

        if (sheetIndex >= workbook.NumberOfSheets)
            sheetIndex = 0;

        var sheet = workbook.GetSheetAt(sheetIndex);
        if (sheet is null || sheet.LastRowNum < 0)
            return result;

        var headerRow = sheet.GetRow(headerRowIndex);
        if (headerRow != null)
        {
            for (var c = 0; c <= headerRow.LastCellNum; c++)
                result.Headers.Add(GetCellText(headerRow.GetCell(c)));
        }

        for (var r = Math.Max(startDataRow, headerRowIndex + 1); r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row is null) continue;

            var isEmpty = true;
            var rowData = new List<string?>();
            for (var c = 0; c < result.Headers.Count; c++)
            {
                var text = GetCellText(row.GetCell(c));
                if (!string.IsNullOrWhiteSpace(text)) isEmpty = false;
                rowData.Add(text);
            }
            if (!isEmpty)
            {
                result.Rows.Add(rowData);
                result.TotalRows++;
            }
        }

        return result;
    }

    private static string GetCellText(ICell? cell)
    {
        if (cell is null) return "";
        switch (cell.CellType)
        {
            case CellType.String:
                return cell.StringCellValue?.Trim() ?? "";
            case CellType.Numeric:
                if (DateUtil.IsCellDateFormatted(cell))
                    return cell.DateCellValue?.ToString("yyyy-MM-dd") ?? "";
                var d = cell.NumericCellValue;
                return d == Math.Floor(d) ? ((long)d).ToString() : d.ToString();
            case CellType.Boolean:
                return cell.BooleanCellValue ? "1" : "0";
            case CellType.Formula:
                try { return cell.StringCellValue?.Trim() ?? ""; }
                catch
                {
                    try
                    {
                        var fVal = cell.NumericCellValue;
                        return fVal == Math.Floor(fVal) ? ((long)fVal).ToString() : fVal.ToString();
                    }
                    catch { return ""; }
                }
            default:
                return "";
        }
    }

    /// <summary>打开文件，文件被 Excel 占用时抛友好异常。</summary>
    private static FileStream OpenFileForRead(string filePath)
    {
        try
        {
            return File.OpenRead(filePath);
        }
        catch (IOException ex) when (ex.HResult == -2147024864)
        {
            throw new InvalidOperationException(
                $"文件被占用，无法读取。\n\n请关闭正在使用该文件的程序（如 Excel）后重新导入。\n\n文件: {filePath}", ex);
        }
    }
}
