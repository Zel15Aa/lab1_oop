// =====================================================================
//  Цей файл містить ВСЮ логіку аналізу виразів і не залежить від UI:
//    1) ExpressionLexer     — розбиття тексту на лексеми;
//    2) ExpressionParser    — етап 1: синтаксична перевірка + побудова дерева;
//    3) ExpressionEvaluator — етап 2: обчислення значення виразу.
//
//  Формальний опис мови (БНФ), від найнижчого пріоритету до найвищого:
//
//    expr     ::= orExpr   { "eqv" orExpr }
//    orExpr   ::= andExpr  { "or"  andExpr }
//    andExpr  ::= notExpr  { "and" notExpr }
//    notExpr  ::= "not" notExpr | relation
//    relation ::= power [ ( "=" | "<" | ">" ) power ]
//    power    ::= primary [ "^" power ]            (правоасоціативна)
//    primary  ::= number | cellRef | "true" | "false" | "(" expr ")"
//    number   ::= digit { digit }                  (ціле число довільної довжини)
//    cellRef  ::= letter { letter } digit { digit } (наприклад A3, AB12)
// =====================================================================

using System.Globalization;
using System.Numerics;

namespace ExcellApp;

// ------------------------------------------------------------------
//  Помилка аналізу/обчислення виразу
// ------------------------------------------------------------------
public sealed class ExpressionException : Exception
{
    public ExpressionException(string message, int position = -1) : base(message)
    {
        Position = position;
    }

    /// Позиція (з нуля) у тексті виразу або -1, якщо невідома.
    public int Position { get; }

    public string FullMessage =>
        Position >= 0 ? $"позиція {Position + 1}: {Message}" : Message;
}

// ------------------------------------------------------------------
//  Значення виразу: ціле число або логічне значення
// ------------------------------------------------------------------
public readonly struct ExprValue
{
    private ExprValue(bool isBoolean, BigInteger number, bool boolean)
    {
        IsBoolean = isBoolean;
        NumberValue = number;
        BoolValue = boolean;
    }

    public bool IsBoolean { get; }
    public BigInteger NumberValue { get; }
    public bool BoolValue { get; }

    public static ExprValue FromNumber(BigInteger number) => new(false, number, false);
    public static ExprValue FromBoolean(bool value) => new(true, BigInteger.Zero, value);

    public string TypeName => IsBoolean ? "логічне значення" : "число";

    public override string ToString() =>
        IsBoolean ? (BoolValue ? "TRUE" : "FALSE") : NumberValue.ToString(CultureInfo.InvariantCulture);
}

// ------------------------------------------------------------------
//  Адреса клітинки (A1, B12, AA3 ...)
// ------------------------------------------------------------------
public readonly struct CellAddress
{
    public CellAddress(int row, int column)
    {
        Row = row;
        Column = column;
    }

    /// Номер рядка, починаючи з 0.
    public int Row { get; }

    /// Номер стовпця, починаючи з 0.
    public int Column { get; }

    /// Перетворює номер стовпця (з 1) на назву в стилі Excel: 1→A, 26→Z, 27→AA.
    public static string GetColumnName(int columnNumber)
    {
        int dividend = columnNumber;
        string columnName = string.Empty;

        while (dividend > 0)
        {
            int modulo = (dividend - 1) % 26;
            columnName = Convert.ToChar(65 + modulo) + columnName;
            dividend = (dividend - modulo) / 26;
        }

        return columnName;
    }

    public static bool TryParse(string text, out CellAddress address)
    {
        address = default;
        int i = 0;
        int column = 0;

        while (i < text.Length && IsAsciiLetter(text[i]))
        {
            if (i >= 3) return false; // не більше 3 літер (до 18278 стовпців)
            column = column * 26 + (char.ToUpperInvariant(text[i]) - 'A' + 1);
            i++;
        }

        if (i == 0 || i == text.Length) return false;

        for (int j = i; j < text.Length; j++)
        {
            if (text[j] < '0' || text[j] > '9') return false;
        }

        if (!int.TryParse(text.Substring(i), NumberStyles.None, CultureInfo.InvariantCulture, out int row) || row < 1)
            return false;

        address = new CellAddress(row - 1, column - 1);
        return true;
    }

    internal static bool IsAsciiLetter(char c) => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');

    public override string ToString() => $"{GetColumnName(Column + 1)}{Row + 1}";
}

// ------------------------------------------------------------------
//  Лексеми
// ------------------------------------------------------------------
public enum TokenKind
{
    Number, CellReference, True, False,
    Not, And, Or, Eqv,
    Power, Equal, Less, Greater,
    LeftParen, RightParen,
    End
}

public sealed class Token
{
    public Token(TokenKind kind, string text, int position)
    {
        Kind = kind;
        Text = text;
        Position = position;
    }

    public TokenKind Kind { get; }
    public string Text { get; }
    public int Position { get; }
    public BigInteger Number { get; init; }
    public CellAddress Address { get; init; }
}

// ------------------------------------------------------------------
//  Лексичний аналіз
// ------------------------------------------------------------------
public static class ExpressionLexer
{
    public static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        int i = 0;

        while (i < text.Length)
        {
            char c = text[i];

            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (IsDigit(c))
            {
                int start = i;
                while (i < text.Length && IsDigit(text[i])) i++;
                string digits = text.Substring(start, i - start);
                tokens.Add(new Token(TokenKind.Number, digits, start)
                {
                    Number = BigInteger.Parse(digits, CultureInfo.InvariantCulture)
                });
                continue;
            }

            if (CellAddress.IsAsciiLetter(c))
            {
                int start = i;
                while (i < text.Length && (CellAddress.IsAsciiLetter(text[i]) || IsDigit(text[i]))) i++;
                string word = text.Substring(start, i - start);
                tokens.Add(ClassifyWord(word, start));
                continue;
            }

            TokenKind? kind = c switch
            {
                '^' => TokenKind.Power,
                '=' => TokenKind.Equal,
                '<' => TokenKind.Less,
                '>' => TokenKind.Greater,
                '(' => TokenKind.LeftParen,
                ')' => TokenKind.RightParen,
                _ => null
            };

            if (kind is null)
                throw new ExpressionException($"недопустимий символ '{c}'", i);

            tokens.Add(new Token(kind.Value, c.ToString(), i));
            i++;
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
        return tokens;
    }

    private static Token ClassifyWord(string word, int position)
    {
        switch (word.ToUpperInvariant())
        {
            case "AND": return new Token(TokenKind.And, word, position);
            case "OR": return new Token(TokenKind.Or, word, position);
            case "NOT": return new Token(TokenKind.Not, word, position);
            case "EQV": return new Token(TokenKind.Eqv, word, position);
            case "TRUE": return new Token(TokenKind.True, word, position);
            case "FALSE": return new Token(TokenKind.False, word, position);
        }

        if (CellAddress.TryParse(word, out CellAddress address))
            return new Token(TokenKind.CellReference, word, position) { Address = address };

        throw new ExpressionException(
            $"невідомий ідентифікатор '{word}' (очікувалось ім'я клітинки, наприклад A1, або and/or/not/eqv/true/false)",
            position);
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';
}

// ------------------------------------------------------------------
//  Синтаксичне дерево
// ------------------------------------------------------------------
public abstract class ExprNode
{
    protected ExprNode(int position) => Position = position;
    public int Position { get; }
}

public sealed class NumberNode : ExprNode
{
    public NumberNode(BigInteger value, int position) : base(position) => Value = value;
    public BigInteger Value { get; }
}

public sealed class BooleanNode : ExprNode
{
    public BooleanNode(bool value, int position) : base(position) => Value = value;
    public bool Value { get; }
}

public sealed class CellNode : ExprNode
{
    public CellNode(CellAddress address, int position) : base(position) => Address = address;
    public CellAddress Address { get; }
}

public sealed class NotNode : ExprNode
{
    public NotNode(ExprNode operand, int position) : base(position) => Operand = operand;
    public ExprNode Operand { get; }
}

public enum BinaryOperator { Power, Equal, Less, Greater, And, Or, Eqv }

public sealed class BinaryNode : ExprNode
{
    public BinaryNode(BinaryOperator op, ExprNode left, ExprNode right, int position) : base(position)
    {
        Operator = op;
        Left = left;
        Right = right;
    }

    public BinaryOperator Operator { get; }
    public ExprNode Left { get; }
    public ExprNode Right { get; }
}

// ------------------------------------------------------------------
//  ЕТАП 1. Синтаксичний аналіз (рекурсивний спуск за БНФ вище)
// ------------------------------------------------------------------
public sealed class ExpressionParser
{
    private const int MaxDepth = 200;

    private readonly List<Token> _tokens;
    private int _index;
    private int _depth;

    private ExpressionParser(List<Token> tokens) => _tokens = tokens;

    ///Перевіряє синтаксис і будує дерево. Кидає ExpressionException при помилці.
    public static ExprNode Parse(string text)
    {
        var parser = new ExpressionParser(ExpressionLexer.Tokenize(text));
        ExprNode node = parser.ParseEqv();

        if (parser.Current.Kind != TokenKind.End)
        {
            throw new ExpressionException(
                $"неочікуваний елемент {Describe(parser.Current)}", parser.Current.Position);
        }

        return node;
    }

    private Token Current => _tokens[_index];

    private Token Advance()
    {
        Token token = _tokens[_index];
        if (token.Kind != TokenKind.End) _index++;
        return token;
    }

    private void Enter(int position)
    {
        if (++_depth > MaxDepth)
            throw new ExpressionException("занадто глибоке вкладення виразу", position);
    }

    private void Leave() => _depth--;

    // expr ::= orExpr { "eqv" orExpr }
    // початок пріорітетів
    private ExprNode ParseEqv()
    {
        ExprNode left = ParseOr();
        while (Current.Kind == TokenKind.Eqv)
        {
            Token op = Advance();
            ExprNode right = ParseOr();
            left = new BinaryNode(BinaryOperator.Eqv, left, right, op.Position);
        }
        return left;
    }

    // orExpr ::= andExpr { "or" andExpr }
    private ExprNode ParseOr()
    {
        ExprNode left = ParseAnd();
        while (Current.Kind == TokenKind.Or)
        {
            Token op = Advance();
            ExprNode right = ParseAnd();
            left = new BinaryNode(BinaryOperator.Or, left, right, op.Position);
        }
        return left;
    }

    // andExpr ::= notExpr { "and" notExpr }
    private ExprNode ParseAnd()
    {
        ExprNode left = ParseNot();
        while (Current.Kind == TokenKind.And)
        {
            Token op = Advance();
            ExprNode right = ParseNot();
            left = new BinaryNode(BinaryOperator.And, left, right, op.Position);
        }
        return left;
    }

    // notExpr ::= "not" notExpr | relation
    private ExprNode ParseNot()
    {
        if (Current.Kind == TokenKind.Not)
        {
            Token op = Advance();
            Enter(op.Position);
            ExprNode operand = ParseNot();
            Leave();
            return new NotNode(operand, op.Position);
        }

        return ParseRelation();
    }

    // relation ::= power [ ( "=" | "<" | ">" ) power ]
    private ExprNode ParseRelation()
    {
        ExprNode left = ParsePower();

        BinaryOperator? op = Current.Kind switch
        {
            TokenKind.Equal => BinaryOperator.Equal,
            TokenKind.Less => BinaryOperator.Less,
            TokenKind.Greater => BinaryOperator.Greater,
            _ => null
        };

        if (op is null) return left;

        Token token = Advance();
        ExprNode right = ParsePower();
        return new BinaryNode(op.Value, left, right, token.Position);
    }

    // power ::= primary [ "^" power ]
    private ExprNode ParsePower()
    {
        ExprNode left = ParsePrimary();

        if (Current.Kind == TokenKind.Power)
        {
            Token op = Advance();
            Enter(op.Position);
            ExprNode right = ParsePower();
            Leave();
            return new BinaryNode(BinaryOperator.Power, left, right, op.Position);
        }

        return left;
    }

    // primary ::= number | cellRef | "true" | "false" | "(" expr ")"
    private ExprNode ParsePrimary()
    {
        Token token = Current;

        switch (token.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new NumberNode(token.Number, token.Position);

            case TokenKind.CellReference:
                Advance();
                return new CellNode(token.Address, token.Position);

            case TokenKind.True:
                Advance();
                return new BooleanNode(true, token.Position);

            case TokenKind.False:
                Advance();
                return new BooleanNode(false, token.Position);

            case TokenKind.LeftParen:
            {
                Advance();
                Enter(token.Position);
                ExprNode inner = ParseEqv();
                Leave();

                if (Current.Kind != TokenKind.RightParen)
                {
                    throw new ExpressionException(
                        $"не знайдено закриваючу дужку ')' для '(' з позиції {token.Position + 1}",
                        Current.Position);
                }

                Advance();
                return inner;
            }

            default:
                throw new ExpressionException(
                    $"очікувався операнд (число, ім'я клітинки, true/false або вираз у дужках), а знайдено {Describe(token)}",
                    token.Position);
        }
    }
    //кінець пріорітетів

    private static string Describe(Token token) =>
        token.Kind == TokenKind.End ? "кінець виразу" : $"'{token.Text}'";
}

// ------------------------------------------------------------------
//  ЕТАП 2. Обчислення значення виразу
// ------------------------------------------------------------------
public static class ExpressionEvaluator
{
    private const int MaxExponent = 200_000;
    private const long MaxResultBits = 200_000;

    /// <param name="node">Дерево, отримане від ExpressionParser.
    /// <param name="resolveCell">Повертає значення клітинки за її адресою.
    public static ExprValue Evaluate(ExprNode node, Func<CellAddress, ExprValue> resolveCell)
    {
        switch (node)
        {
            case NumberNode number:
                return ExprValue.FromNumber(number.Value);

            case BooleanNode boolean:
                return ExprValue.FromBoolean(boolean.Value);

            case CellNode cell:
                try
                {
                    return resolveCell(cell.Address);
                }
                catch (ExpressionException ex) when (ex.Position < 0)
                {
                    // додаємо позицію посилання у тексті виразу
                    throw new ExpressionException(ex.Message, cell.Position);
                }

            case NotNode notNode:
            {
                ExprValue operand = Evaluate(notNode.Operand, resolveCell);
                RequireBoolean(operand, "not", notNode.Position);
                return ExprValue.FromBoolean(!operand.BoolValue);
            }

            case BinaryNode binary:
                return EvaluateBinary(binary, resolveCell);

            default:
                throw new InvalidOperationException("Невідомий вузол синтаксичного дерева.");
        }
    }

    private static ExprValue EvaluateBinary(BinaryNode node, Func<CellAddress, ExprValue> resolveCell)
    {
        // Обидва операнди обчислюються завжди (без "швидкого" обчислення),
        // щоб помилки в будь-якій частині виразу були виявлені.
        ExprValue left = Evaluate(node.Left, resolveCell);
        ExprValue right = Evaluate(node.Right, resolveCell);
        int pos = node.Position;

        switch (node.Operator)
        {
            case BinaryOperator.Power:
                RequireNumber(left, "^", pos);
                RequireNumber(right, "^", pos);
                return ExprValue.FromNumber(Power(left.NumberValue, right.NumberValue, pos));

            case BinaryOperator.Equal:
                if (left.IsBoolean != right.IsBoolean)
                {
                    throw new ExpressionException(
                        $"оператор '=' не може порівнювати різні типи ({left.TypeName} та {right.TypeName})", pos);
                }
                return ExprValue.FromBoolean(left.IsBoolean
                    ? left.BoolValue == right.BoolValue
                    : left.NumberValue == right.NumberValue);

            case BinaryOperator.Less:
                RequireNumber(left, "<", pos);
                RequireNumber(right, "<", pos);
                return ExprValue.FromBoolean(left.NumberValue < right.NumberValue);

            case BinaryOperator.Greater:
                RequireNumber(left, ">", pos);
                RequireNumber(right, ">", pos);
                return ExprValue.FromBoolean(left.NumberValue > right.NumberValue);

            case BinaryOperator.And:
                RequireBoolean(left, "and", pos);
                RequireBoolean(right, "and", pos);
                return ExprValue.FromBoolean(left.BoolValue && right.BoolValue);

            case BinaryOperator.Or:
                RequireBoolean(left, "or", pos);
                RequireBoolean(right, "or", pos);
                return ExprValue.FromBoolean(left.BoolValue || right.BoolValue);

            case BinaryOperator.Eqv:
                RequireBoolean(left, "eqv", pos);
                RequireBoolean(right, "eqv", pos);
                return ExprValue.FromBoolean(left.BoolValue == right.BoolValue);

            default:
                throw new InvalidOperationException("Невідомий оператор.");
        }
    }

    //Піднесення до степенню
    private static BigInteger Power(BigInteger value, BigInteger exponent, int position)
    {
        if (value.IsZero)
        {
            if (exponent.IsZero)
                throw new ExpressionException("вираз 0^0 не визначений", position);
            return BigInteger.Zero;
        }

        if (value.IsOne || exponent.IsZero) return BigInteger.One;

        if (exponent > MaxExponent)
            throw new ExpressionException($"показник степеня завеликий (максимум {MaxExponent})", position);

        int exp = (int)exponent;
        long bits = (long)value.GetBitLength() * exp;
        if (bits > MaxResultBits)
            throw new ExpressionException("результат піднесення до степеня завеликий для обчислення", position);

        return BigInteger.Pow(value, exp);
    }
    
    //Перевірка типу(True/False)
    private static void RequireBoolean(ExprValue value, string op, int position)
    {
        if (!value.IsBoolean)
        {
            throw new ExpressionException(
                $"оператор '{op}' потребує логічних операндів, а отримано {value.TypeName}", position);
        }
    }
    //Перевірка типу(Числа)
    private static void RequireNumber(ExprValue value, string op, int position)
    {
        if (value.IsBoolean)
        {
            throw new ExpressionException(
                $"оператор '{op}' потребує числових операндів, а отримано {value.TypeName}", position);
        }
    }
}
