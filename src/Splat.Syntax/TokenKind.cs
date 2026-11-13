namespace Splat.Syntax;

public enum TokenKind
{
    EndOfFile,

    // Literals and names
    Ident, Int, Float, String, Char,

    // Keywords (spec 2.3). `Grad`/`Intg`/`Sum`/`Prod` cover both the ASCII spelling and the Unicode symbol.
    Wag, Cook, Yeet, Hmm, Meh, Nah, Spin, Bail, Skip, Pick,
    Mold, From, With, Quirk, Teach, Me, Elder, Birth, Funeral, Secret, Mood,
    Yolo, Oops, Rip, Zone, Nuke, Yoink,
    Guess, Judge, Brain, Grind,
    Fit, On, In, While, And, Or, Yep, Nop,
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
