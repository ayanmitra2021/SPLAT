namespace Splat.Syntax;

public enum TokenKind
{
    EndOfFile,

    // Literals and names
    Ident, Int, Float, String, Char, Temporal,

    // Keywords (spec 2.3). `Grad`/`Intg`/`Sum`/`Prod` cover both the ASCII spelling and the Unicode symbol.
    Var, Fn, Return, If, Elif, Else, Loop, Break, Continue, Pick,
    Class, From, With, Trait, Extend, Self, Super, Init, Deinit, Secret, Enum,
    Try, Catch, Rip, Arena, Drop, Import,
    Regress, Classify, Network, Train,
    Fit, On, In, While, And, Or, True, False,
    Grad, Intg, Sum, Prod,

    // Clause punctuation
    Colon,          // ':'  opens a block
    DoubleColon,    // '::' type annotation
    ColonAssign,    // ':=' immutable bind
    SliceColon,     // ':'  directly inside [...]
    Semicolon,      // ';'  closes a block
    Comma, Dot, LParen, RParen, LBracket, RBracket,

    // Operators
    Plus, Minus, Star, Slash, Percent, StarStar,
    At, Backslash, MiddleDot,
    Transpose,      // '
    ConjTranspose,  // ''
    DotStar, DotSlash, DotPlus, DotMinus,
    DotDot, DotDotEq,
    Lt, Le, Gt, Ge, EqEq, BangEq, Bang,
    Amp, Tilde,
    Pipe,           // '|' (lambda bars / norm bars; the parser decides)
    PipeGreater,    // '|>'
    GreaterGreater, // '>>'
    FatArrow,       // '=>'
    Assign, PlusAssign, MinusAssign, StarAssign, SlashAssign, AtAssign, DotStarAssign, DotSlashAssign,
    Dollar, Question,
}
