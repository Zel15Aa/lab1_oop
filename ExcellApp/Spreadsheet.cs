// =====================================================================
//  Модель електронної таблиці (без залежності від UI).
//  Обробники кнопок у MainPage лише викликають методи цього класу
// =====================================================================

using System.Text.Encodings.Web;
using System.Text.Json;

namespace ExcellApp;

public enum CellResultKind { Empty, Value, Error }

/// Результат обчислення клітинки: порожня, значення або помилка.
public sealed class CellResult
{
    private CellResult(CellResultKind kind, ExprValue value, string error)
    {
        Kind = kind;
        Value = value;
        Error = error;
    }

    public CellResultKind Kind { get; }
    public ExprValue Value { get; }
    public string Error { get; }

    public static readonly CellResult Empty = new(CellResultKind.Empty, default, string.Empty);
    public static CellResult FromValue(ExprValue value) => new(CellResultKind.Value, value, string.Empty);
    public static CellResult FromError(string message) => new(CellResultKind.Error, default, message);
}

public sealed class Spreadsheet
{
    public const int MaxRows = 1000;
    public const int MaxColumns = 200;

    private sealed class CellData
    {
        public string Expression = string.Empty;
        public ExprNode? Ast;                 // дерево (якщо синтаксис правильний)
        public ExpressionException? ParseError; // помилка синтаксису
    }

    private readonly Dictionary<(int Row, int Column), CellData> _cells = new();
    private readonly Dictionary<(int Row, int Column), CellResult> _results = new();
    private readonly HashSet<(int Row, int Column)> _inProgress = new();

    public Spreadsheet(int rows, int columns)
    {
        if (rows < 1 || rows > MaxRows) throw new ArgumentOutOfRangeException(nameof(rows));
        if (columns < 1 || columns > MaxColumns) throw new ArgumentOutOfRangeException(nameof(columns));
        Rows = rows;
        Columns = columns;
    }

    public int Rows { get; private set; }
    public int Columns { get; private set; }
    public int NonEmptyCellCount => _cells.Count;

    // ---------------- Доступ до клітинок ----------------

    public string GetExpression(int row, int column) =>
        _cells.TryGetValue((row, column), out CellData? cell) ? cell.Expression : string.Empty;

    /// Записує вираз у клітинку та виконує ЕТАП 1 (синтаксичну перевірку).
    public void SetExpression(int row, int column, string? text)
    {
        if (!InRange(row, column)) throw new ArgumentOutOfRangeException(nameof(row));

        string trimmed = (text ?? string.Empty).Trim();
        _results.Clear();

        if (trimmed.Length == 0)
        {
            _cells.Remove((row, column));
            return;
        }

        var cell = new CellData { Expression = trimmed };
        try
        {
            cell.Ast = ExpressionParser.Parse(trimmed);
        }
        catch (ExpressionException ex)
        {
            cell.ParseError = ex;
        }

        _cells[(row, column)] = cell;
    }

    public IEnumerable<(CellAddress Address, string Expression)> GetNonEmptyCells() =>
        _cells.OrderBy(p => p.Key.Row).ThenBy(p => p.Key.Column)
              .Select(p => (new CellAddress(p.Key.Row, p.Key.Column), p.Value.Expression));

    // ---------------- Обчислення (ЕТАП 2) ----------------

    /// Скидає кеш результатів. Значення обчислюються при зверненні.
    public void Recalculate() => _results.Clear();

    public CellResult GetResult(int row, int column)
    {
        if (!InRange(row, column)) return CellResult.Empty;
        return Evaluate(row, column);
    }

    public IEnumerable<(CellAddress Address, string Message)> GetErrors()
    {
        foreach (var key in _cells.Keys.OrderBy(k => k.Row).ThenBy(k => k.Column).ToList())
        {
            CellResult result = Evaluate(key.Row, key.Column);
            if (result.Kind == CellResultKind.Error)
                yield return (new CellAddress(key.Row, key.Column), result.Error);
        }
    }

    private CellResult Evaluate(int row, int column)
    {
        var key = (row, column);

        if (_results.TryGetValue(key, out CellResult? cached)) return cached;
        if (!_cells.TryGetValue(key, out CellData? cell)) return CellResult.Empty;

        if (!_inProgress.Add(key))
        {
            throw new ExpressionException(
                $"циклічне посилання: клітинка {new CellAddress(row, column)} залежить сама від себе");
        }

        CellResult result;
        try
        {
            if (cell.ParseError != null)
            {
                result = CellResult.FromError("синтаксична помилка, " + cell.ParseError.FullMessage);
            }
            else
            {
                ExprValue value = ExpressionEvaluator.Evaluate(cell.Ast!, ResolveCell);
                result = value.IsBoolean
                    ? CellResult.FromValue(value)
                    : CellResult.FromError(
                        "вираз має бути логічним (для варіанта 73), а його результат — число");
            }
        }
        catch (ExpressionException ex)
        {
            result = CellResult.FromError("помилка обчислення, " + ex.FullMessage);
        }
        finally
        {
            _inProgress.Remove(key);
        }

        _results[key] = result;
        return result;
    }

    ///Значення клітинки для посилань у виразах. Порожня клітинка = FALSE.
    private ExprValue ResolveCell(CellAddress address)
    {
        if (!InRange(address.Row, address.Column))
        {
            throw new ExpressionException(
                $"клітинки {address} не існує в таблиці (розмір {Rows}×{Columns})");
        }

        CellResult result = Evaluate(address.Row, address.Column);

        switch (result.Kind)
        {
            case CellResultKind.Empty:
                return ExprValue.FromBoolean(false);
            case CellResultKind.Error:
                throw new ExpressionException($"клітинка {address} містить помилку");
            default:
                return result.Value;
        }
    }

    // ---------------- Зміна розміру таблиці ----------------

    public void AddRow()
    {
        if (Rows >= MaxRows) throw new InvalidOperationException($"Максимум рядків: {MaxRows}.");
        Rows++;
        _results.Clear();
    }

    public void AddColumn()
    {
        if (Columns >= MaxColumns) throw new InvalidOperationException($"Максимум стовпців: {MaxColumns}.");
        Columns++;
        _results.Clear();
    }

    public void RemoveLastRow()
    {
        if (Rows <= 1) throw new InvalidOperationException("Має залишитися хоча б один рядок.");
        int last = Rows - 1;
        foreach (var key in _cells.Keys.Where(k => k.Row == last).ToList()) _cells.Remove(key);
        Rows--;
        _results.Clear();
    }

    public void RemoveLastColumn()
    {
        if (Columns <= 1) throw new InvalidOperationException("Має залишитися хоча б один стовпець.");
        int last = Columns - 1;
        foreach (var key in _cells.Keys.Where(k => k.Column == last).ToList()) _cells.Remove(key);
        Columns--;
        _results.Clear();
    }

    public bool IsRowEmpty(int row) => !_cells.Keys.Any(k => k.Row == row);
    public bool IsColumnEmpty(int column) => !_cells.Keys.Any(k => k.Column == column);

    private bool InRange(int row, int column) =>
        row >= 0 && row < Rows && column >= 0 && column < Columns;
}

// ------------------------------------------------------------------
//  Збереження / читання таблиці у форматі JSON
// ------------------------------------------------------------------
public sealed class SpreadsheetDto
{
    public int Rows { get; set; }
    public int Columns { get; set; }
    public Dictionary<string, string>? Cells { get; set; }
}

public static class SpreadsheetSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Serialize(Spreadsheet sheet)
    {
        var dto = new SpreadsheetDto
        {
            Rows = sheet.Rows,
            Columns = sheet.Columns,
            Cells = new Dictionary<string, string>()
        };

        foreach (var (address, expression) in sheet.GetNonEmptyCells())
            dto.Cells[address.ToString()] = expression;

        return JsonSerializer.Serialize(dto, Options);
    }

    /// <exception cref="InvalidDataException">Файл має некоректну структуру.</exception>
    public static Spreadsheet Deserialize(string json)
    {
        SpreadsheetDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<SpreadsheetDto>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Файл має некоректний формат: " + ex.Message, ex);
        }

        if (dto == null) throw new InvalidDataException("Файл порожній.");

        if (dto.Rows < 1 || dto.Rows > Spreadsheet.MaxRows ||
            dto.Columns < 1 || dto.Columns > Spreadsheet.MaxColumns)
        {
            throw new InvalidDataException(
                $"Некоректний розмір таблиці (рядків 1–{Spreadsheet.MaxRows}, стовпців 1–{Spreadsheet.MaxColumns}).");
        }

        var sheet = new Spreadsheet(dto.Rows, dto.Columns);

        foreach (var pair in dto.Cells ?? new Dictionary<string, string>())
        {
            if (!CellAddress.TryParse(pair.Key, out CellAddress address) ||
                address.Row >= sheet.Rows || address.Column >= sheet.Columns)
            {
                throw new InvalidDataException($"Некоректна адреса клітинки: '{pair.Key}'.");
            }

            sheet.SetExpression(address.Row, address.Column, pair.Value);
        }

        return sheet;
    }
}
