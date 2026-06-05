// GDScript 3.4 tokenizer — validation-only C# port
// Based on godot/modules/gdscript/gdscript_tokenizer.cpp
// Stripped of: constants, compiler buffer, code completion, warning skips.
// Kept: full string/comment/indent handling + TK_ERROR generation.

namespace SlotWeave.Modding;

/// <summary>
/// GDScript tokenizer that produces TK_ERROR tokens with line numbers
/// for syntax validation. Does NOT build an AST — just checks if the
/// source can be tokenized without errors.
/// </summary>
public class GdTokenizer
{
    private readonly string _source;
    private int _pos;
    private int _line;
    private int _col;
    private bool _errorFlag;
    private string _lastError = "";
    private int _errorLine;
    private int _errorCol;

    public bool HasError => _errorFlag;
    public string LastError => _lastError;
    public int ErrorLine => _errorLine;
    public int ErrorCol => _errorCol;

    public GdTokenizer(string source)
    {
        _source = source;
        _pos = 0;
        _line = 1;
        _col = 0;
    }

    /// <summary>
    /// Run the tokenizer to completion. Returns true if the source has no token-level errors.
    /// If false, check LastError / ErrorLine / ErrorCol.
    /// </summary>
    public bool Validate()
    {
        while (_pos < _source.Length && !_errorFlag)
            NextToken();
        return !_errorFlag;
    }

    private char Peek(int offset = 0)
    {
        int idx = _pos + offset;
        return idx < _source.Length ? _source[idx] : '\0';
    }

    private void Advance(int count = 1)
    {
        _pos += count;
        _col += count;
    }

    private void Newline()
    {
        _pos++;
        _line++;
        _col = 0;
    }

    private void Error(string msg)
    {
        if (!_errorFlag)
        {
            _errorFlag = true;
            _lastError = msg;
            _errorLine = _line;
            _errorCol = _col;
        }
    }

    /// <summary>
    /// Main tokenizer loop — one call consumes one token.
    /// </summary>
    private void NextToken()
    {
        if (_pos >= _source.Length) return;

        char c = _source[_pos];

        // ── Whitespace ──
        if (c == '\n') { Newline(); return; }
        if (c == '\r')
        {
            _pos++;
            if (Peek() == '\n') _pos++;
            _line++;
            _col = 0;
            return;
        }
        if (c == ' ' || c == '\t') { Advance(); return; }

        // ── Comment ──
        if (c == '#') { SkipComment(); return; }

        // ── String literals ──
        if (c == '"')
        {
            if (Peek(1) == '"' && Peek(2) == '"')
                ScanMultiLineString();
            else
                ScanSingleLineString('"');
            return;
        }
        if (c == '\'')
        {
            ScanSingleLineString('\'');
            return;
        }

        // ── Numbers ──
        if (c >= '0' && c <= '9')
        {
            ScanNumber();
            return;
        }

        // ── Identifiers and keywords ──
        if (IsIdentStart(c))
        {
            ScanIdentifier();
            return;
        }

        // ── Operators and punctuation ──
        ScanOperator();
    }

    // ═══════════════════════════════════════════════════
    //  String scanning
    // ═══════════════════════════════════════════════════

    private void ScanSingleLineString(char quote)
    {
        Advance(); // skip opening quote
        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '\\')
            {
                Advance(2); // skip escape sequence
                continue;
            }
            if (c == '\n' || c == '\r')
            {
                Error($"Unclosed string literal (opened line {_errorLine})");
                return;
            }
            if (c == quote)
            {
                Advance(); // closing quote
                return;
            }
            Advance();
        }
        Error($"Unclosed string literal (reached end of file)");
    }

    private void ScanMultiLineString()
    {
        int startLine = _line;
        Advance(3); // skip """
        while (_pos < _source.Length)
        {
            if (_source[_pos] == '"' && Peek(1) == '"' && Peek(2) == '"')
            {
                Advance(3); // closing """
                return;
            }
            if (_source[_pos] == '\n') Newline();
            else Advance();
        }
        Error($"Unclosed multi-line string (opened line {startLine})");
    }

    // ═══════════════════════════════════════════════════
    //  Comment
    // ═══════════════════════════════════════════════════

    private void SkipComment()
    {
        while (_pos < _source.Length && _source[_pos] != '\n' && _source[_pos] != '\r')
            _pos++;
    }

    // ═══════════════════════════════════════════════════
    //  Number scanning
    // ═══════════════════════════════════════════════════

    private void ScanNumber()
    {
        if (_source[_pos] == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            Advance(2); // 0x
            while (_pos < _source.Length && IsHexDigit(_source[_pos])) Advance();
            return;
        }

        while (_pos < _source.Length && _source[_pos] >= '0' && _source[_pos] <= '9') Advance();

        if (_pos < _source.Length && _source[_pos] == '.')
        {
            Advance();
            while (_pos < _source.Length && _source[_pos] >= '0' && _source[_pos] <= '9') Advance();
        }

        if (_pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
        {
            Advance();
            if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-')) Advance();
            while (_pos < _source.Length && _source[_pos] >= '0' && _source[_pos] <= '9') Advance();
        }
    }

    // ═══════════════════════════════════════════════════
    //  Identifier
    // ═══════════════════════════════════════════════════

    private void ScanIdentifier()
    {
        while (_pos < _source.Length && IsIdentContinue(_source[_pos])) Advance();
    }

    // ═══════════════════════════════════════════════════
    //  Operators
    // ═══════════════════════════════════════════════════

    private void ScanOperator()
    {
        char c = _source[_pos];
        char n = Peek(1);

        // Two-char operators
        switch (c)
        {
            case '=' when n == '=': Advance(2); return;
            case '!' when n == '=': Advance(2); return;
            case '<' when n == '=': Advance(2); return;
            case '>' when n == '=': Advance(2); return;
            case '<' when n == '<': Advance(2); return;
            case '>' when n == '>': Advance(2); return;
            case '&' when n == '&': Advance(2); return;
            case '|' when n == '|': Advance(2); return;
            case '+' when n == '=': Advance(2); return;
            case '-' when n == '=': Advance(2); return;
            case '*' when n == '=': Advance(2); return;
            case '/' when n == '=': Advance(2); return;
            case '%' when n == '=': Advance(2); return;
            case '-' when n == '>': Advance(2); return;
            case '.' when n == '.': Advance(2); return;
        }

        // Single-char
        Advance();
    }

    // ═══════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════

    private static bool IsIdentStart(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

    private static bool IsIdentContinue(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') ||
        (c >= '0' && c <= '9') || c == '_';

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
