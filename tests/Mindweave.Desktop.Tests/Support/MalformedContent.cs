using System.Text;

namespace Mindweave.Desktop.Tests;

/// <summary>
/// Formulas and Markdown a model can write wrongly (D2 §2.5, dependencies-4): a fixed list of known-hard cases and a seeded
/// mix of their pieces, a few hundred in all. The same seed gives the same list on every run and machine. Extreme lengths
/// are left out: they are slow rather than broken (a 200,000-character line without a space took 47 s to lay out in a debug
/// build, 2026-11-27), and a test of the normal run has seconds.
/// </summary>
internal static class MalformedContent
{
    private static readonly string[] KnownFormulas =
    [
        "", " ", "{", "}", "{{{", "}}}", "{}{}{}", "\\", "\\\\", "\\\\\\", "^", "_", "^^", "__", "x^", "x_", "x^^2", "x__2", "x^_", "^{", "_{",
        "\\frac", "\\frac{", "\\frac{1}", "\\frac{1}{", "\\frac{}{}", "\\frac12", "\\frac{1}{2", "\\sqrt", "\\sqrt[", "\\sqrt[3", "\\sqrt[]{}",
        "\\left(", "\\right)", "\\left( x", "x \\right)", "\\left. \\right.", "\\left\\{ \\right", "\\left", "\\right", "\\middle|",
        "\\begin", "\\begin{", "\\begin{matrix}", "\\end{matrix}", "\\begin{matrix} a & b", "\\begin{array}", "\\begin{array}{", "\\begin{array}{zz} a \\end{array}",
        "\\begin{cases} x & y \\\\ \\end{cases}", "\\begin{pmatrix} & & & \\\\ \\\\ \\\\ \\end{pmatrix}", "\\begin{align} a &= b \\end{matrix}",
        "\\begin{nonexistent} x \\end{nonexistent}", "\\end{x}", "& & &", "\\\\ \\\\ \\\\",
        "\\unknowncommand", "\\@", "\\#", "\\%", "\\&", "\\~", "\\'", "\\`", "\\^", "\\_{", "\\text", "\\text{", "\\text{unclosed", "\\text{$x$}",
        "\\mathrm{", "\\mathbb{}", "\\mathcal{\\mathcal{\\mathcal{x}}}", "\\operatorname{", "\\color", "\\color{", "\\color{#zzzzzz}{x}", "\\color{}{x}",
        "\\textcolor{red}", "\\colorbox{blue}{", "\\fcolorbox{a}{b}{c}", "\\rule{1e9em}{1e9em}", "\\rule{-5em}{-5em}", "\\rule{}{}",
        "\\kern-1e308em", "\\kern1e308em x", "\\hspace{99999999cm}", "\\hspace{-99999999cm}", "\\mkern-999999mu", "\\raisebox{1e9pt}{x}",
        "\\phantom{", "\\vphantom", "\\overbrace{x}^{", "\\underbrace{", "\\overset{", "\\stackrel{a}", "\\xrightarrow[", "\\xleftarrow",
        "\\def\\a{\\a}\\a", "\\def\\a{\\a\\a}\\a", "\\newcommand{\\x}{\\x}\\x", "\\newcommand{\\x}[1]{#1#1}\\x{\\x{\\x{\\x{y}}}}", "\\def", "\\def\\", "#1", "##",
        "\\gdef\\b{\\b b}\\b", "\\let\\a\\a\\a", "\\href{javascript:alert(1)}{x}", "\\url{", "\\includegraphics{x.png}", "\\htmlClass{x}{y}",
        "\\char", "\\char\"FFFFFF", "\\char-1", "\\char\"110000", "\\char0", "\\verb|x", "\\verb", "\\tag{", "\\tag{1}\\tag{2}", "\\label{",
        "\\big", "\\Bigg(", "\\bigl", "\\not", "\\not\\not\\not", "\\limits", "x\\limits", "\\sum\\limits\\limits", "\\int\\nolimits\\limits",
        "\\overline{\\overline{\\overline{\\overline{\\overline{x}}}}}", "\\displaystyle\\textstyle\\scriptstyle\\scriptscriptstyle",
        "\\binom", "\\binom{", "\\dbinom{}{}", "\\boxed{", "\\cancel", "\\ce{H2O}", "\\pu{1 m}", "\\mathchoice{a}{b}", "\\over\\over", "a \\over b \\over c",
        "\\atop", "\\choose\\choose", "\\hline", "\\cline{", "\\multicolumn{2}{c}{x}", "\\substack{", "\\smash[", "\\lap", "\\llap{", "\\mathllap",
        "\0", "\u0001\u0002\u0003", "\u200b\u200c\u200d", "\u202e x \u202c", "\ufeff", "\ud800", "\udfff", "\ud83d", "🚂^{🚃}_{🚋}", "火车^{轨道}",
        "\\text{火车\\frac{轨}{道}}", "\\mathrm{مرحبا}", "\\text{\u0300\u0301\u0302\u0303}", "x\r\ny", "x\ty", "\\\r\n\\", "$", "$$", "\\(", "\\[", "\\]",
    ];

    private static readonly string[] Pieces =
    [
        "x", "\\frac{", "}", "{", "^", "_", "\\left(", "\\right)", "\\sqrt[", "]", "\\begin{matrix}", "\\end{matrix}", "&", "\\\\", "\\text{", "\\color{red}",
        "\\alpha", "\\unknown", "\\over", "\\kern-9em", "\\rule{9em}{9em}", "\\def\\q{\\q}", "\\q", "#", "%", "$", "\\", "火", "🚂", "\u0000", "\\char\"FFFF",
        "\\mathbb{", "\\overbrace{", "}^{", "\\not", "\\limits", "\\tag{", "\\big(", "\\middle|", "\\hspace{1e6em}",
    ];

    /// <summary>LaTeX bodies, without delimiters: unclosed and unbalanced groups, unknown commands, huge or negative sizes, deep nesting, macros that expand forever, odd characters.</summary>
    public static IReadOnlyList<string> Formulas { get; } = BuildFormulas(); // After the arrays it is built from.

    /// <summary>Whole Markdown texts: broken blocks and delimiters, deep nesting, long lines, odd characters.</summary>
    public static IReadOnlyList<string> Markdown { get; } = BuildMarkdown();

    private static List<string> BuildFormulas()
    {
        var list = new List<string>(KnownFormulas)
        {
            // Deep nesting and long inputs.
            new string('{', 300) + "x" + new string('}', 300),
            new string('{', 2000),
            new string('}', 2000),
            string.Concat(Enumerable.Repeat("\\frac{1}{", 200)) + "x" + new string('}', 200),
            string.Concat(Enumerable.Repeat("\\sqrt{", 300)) + "x" + new string('}', 300),
            string.Concat(Enumerable.Repeat("x^{", 300)) + "y" + new string('}', 300),
            string.Concat(Enumerable.Repeat("\\left(", 500)) + "x" + string.Concat(Enumerable.Repeat("\\right)", 500)),
            string.Concat(Enumerable.Repeat("\\left(", 500)),
            "\\begin{matrix}" + string.Concat(Enumerable.Repeat("a & ", 2000)) + "\\end{matrix}",
            "\\begin{matrix}" + string.Concat(Enumerable.Repeat("a \\\\ ", 2000)) + "\\end{matrix}",
            string.Concat(Enumerable.Repeat("x + ", 2_000)),
            new string('x', 5_000),
            string.Concat(Enumerable.Repeat("\\alpha", 2_000)),
            string.Concat(Enumerable.Repeat("\\def\\a{\\a\\a}", 50)) + "\\a",
        };

        var random = new Random(20261127);
        for (var i = 0; i < 160; i++)
        {
            var text = new StringBuilder();
            var count = random.Next(1, 14);
            for (var j = 0; j < count; j++)
            {
                text.Append(Pieces[random.Next(Pieces.Length)]);
            }

            list.Add(text.ToString());
        }

        return list;
    }

    private static List<string> BuildMarkdown()
    {
        var list = new List<string>
        {
            "", " ", "\n\n\n", "#", "######## too deep", "```", "```\nunclosed fence", "~~~~", "````python\n```\n", "    indented\n\tcode",
            "| a | b |\n|---|\n| 1 | 2 | 3 | 4 |", "| a |\n| --- | --- | --- |", "|", "||||", "| $x$ | $$y$$ |\n|---|---|\n| \\( | \\) |",
            "[link](", "[link](javascript:alert(1))", "[a]: <>", "![image](http://example.com/x.png)", "![", "<script>alert(1)</script>", "<div>", "</div>",
            "<!--", "&#0;", "&#x110000;", "&nbsp;&nbsp;", "***", "**unclosed bold", "*a **b* c**", "~~", "> > > >", "- - - -", "1. 2. 3.", "-\n-\n-",
            "$", "$$", "$$\nunclosed display", "\\[\nunclosed", "\\(", "$x", "$$x$", "$x$$", "\\[x\\)", "\\(x\\]", "$$ $$ $$ $$", "$ $", "$\\$",
            "价格 $5 和 $10 与 $$", "`$x$`", "```math\n\\frac{\n```", "<math>", "\\begin{equation} x \\end{equation}",
            new string('>', 150) + " deep quote", string.Concat(Enumerable.Repeat("- ", 200)) + "deep list", string.Concat(Enumerable.Repeat("  ", 300)) + "- x",
            string.Concat(Enumerable.Range(0, 300).Select(i => new string(' ', i % 60) + "- item\n")),
            string.Concat(Enumerable.Repeat("[", 5000)), string.Concat(Enumerable.Repeat("*", 5000)), string.Concat(Enumerable.Repeat("_a", 5000)),
            string.Concat(Enumerable.Repeat("`", 3000)), string.Concat(Enumerable.Repeat("$", 3000)), string.Concat(Enumerable.Repeat("\\", 3000)),
            new string('x', 5_000), string.Concat(Enumerable.Repeat("火", 5_000)), string.Concat(Enumerable.Repeat("🚂", 2_000)),
            "\0\0\0", "\u0001\u0002", "\u202e right to left \u202c", "\ud800 lone surrogate", "x\r\ny\rz\n", "\u2028\u2029",
            string.Concat(Enumerable.Repeat("| a ", 100)) + "|\n" + string.Concat(Enumerable.Repeat("|---", 100)) + "|",
            string.Concat(Enumerable.Repeat("| x |\n", 300)),
            "[^1]\n\n[^1]: ", "[^]", "* [ ] task\n* [x] done\n* [", "Term\n: definition\n:", "<https://", "www.", "http://" + new string('a', 5000),
        };

        return list;
    }
}
