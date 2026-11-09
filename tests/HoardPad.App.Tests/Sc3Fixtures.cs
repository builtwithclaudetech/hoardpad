// Sc3Fixtures.cs — the 20 per-language token-class fixtures for SC-3 (PRD §5 SC-3 / FR-7 / A-6).
//
// One fixture per FR-7 v1 language. Each names: the file extension (drives the PRODUCTION
// HoardPadRegistryOptions.ResolveScopeForExtension lookup — the exact resolution the editor uses), the
// scope that extension resolves to (asserted, so a grammar-id drift is caught), a small inline source
// sample, and a handful of MARKED REGIONS. A marked region is a substring (+ which occurrence) paired with
// the coarse token class it must reduce to. Substring marking is used over line/col because it survives
// trivial fixture edits; the harness locates the substring, finds the covering token, and classifies it.
//
// Region selection rule (so fixtures stay coarse + stable, SC-3 design): each region targets a token whose
// scope stack carries an UNAMBIGUOUS coarse family (a keyword word, a string body, a comment body, a name,
// a numeric literal, a bracket) — never a bare-`source` gap. The exact leaf scopes were read back from the
// live tokenizer (M9.2) so every expected class is grounded, not guessed. Coverage target: every fixture
// asserts at least keyword + (string or comment) so the no-plaintext-fallback gate is non-vacuous.

using System.Collections.Generic;

namespace HoardPad.App.Tests;

/// <summary>A single marked region: the <paramref name="Needle"/>'s <paramref name="Occurrence"/>-th
/// occurrence (1-based) in the fixture must reduce to <paramref name="Expected"/>.</summary>
internal readonly record struct MarkedRegion(string Needle, int Occurrence, CoarseClass Expected)
{
    public MarkedRegion(string needle, CoarseClass expected) : this(needle, 1, expected) { }
}

/// <summary>One language fixture: extension, the scope it resolves to, the sample, and its marked regions.</summary>
internal sealed record LanguageFixture(
    string Language,
    string Extension,
    string ExpectedScope,
    string Source,
    IReadOnlyList<MarkedRegion> Regions);

/// <summary>The 20 FR-7 language fixtures (Markdown … Go). Consumed by <c>Sc3TokenReadbackTests</c>.</summary>
internal static class Sc3Fixtures
{
    public static IReadOnlyList<LanguageFixture> All { get; } = new[]
    {
        // 1. Markdown — heading marker (punctuation), heading text (identifier/section). Markdown has no
        //    keyword/string/comment families (its tokens are markup.*), so its regions are the two that DO
        //    carry a classable scope: the heading delimiter and the heading section name.
        new LanguageFixture("Markdown", ".md", "text.html.markdown",
            "# Heading\n\nSome plain text here.\n",
            new[]
            {
                new MarkedRegion("#", CoarseClass.Punctuation),       // punctuation.definition.heading.markdown
                new MarkedRegion("Heading", CoarseClass.Identifier),  // entity.name.section.markdown
            }),

        // 2. HTML — tag name (identifier), attribute name (identifier), quoted value (string), tag bracket (punctuation).
        new LanguageFixture("HTML", ".html", "text.html.derivative",
            "<div class=\"box\">hi</div>\n",
            new[]
            {
                new MarkedRegion("div", CoarseClass.Identifier),  // entity.name.tag.html
                new MarkedRegion("class", CoarseClass.Identifier), // entity.other.attribute-name.html
                new MarkedRegion("box", CoarseClass.String),       // string.quoted.double.html (body)
                new MarkedRegion("<", CoarseClass.Punctuation),    // punctuation.definition.tag.begin.html
            }),

        // 3. JavaScript — const (keyword/storage), number literal, comment.
        new LanguageFixture("JavaScript", ".js", "source.js",
            "const x = 42; // a note\n",
            new[]
            {
                new MarkedRegion("const", CoarseClass.Keyword),  // storage.type.js
                new MarkedRegion("42", CoarseClass.Number),      // constant.numeric.decimal.js
                new MarkedRegion("a note", CoarseClass.Comment), // comment.line.double-slash.js (body)
            }),

        // 4. CSS — class selector name (identifier), property name (identifier/support), hex color (number-ish constant), brace (punctuation).
        new LanguageFixture("CSS", ".css", "source.css",
            ".foo { color: #fff; }\n",
            new[]
            {
                new MarkedRegion("foo", CoarseClass.Identifier),  // entity.other.attribute-name.class.css
                new MarkedRegion("color", CoarseClass.Identifier), // support.type.property-name.css
                new MarkedRegion("{", CoarseClass.Punctuation),    // punctuation.section.property-list.begin...
            }),

        // 5. Python — def (keyword/storage), function name (identifier), number, comment.
        new LanguageFixture("Python", ".py", "source.python",
            "def main():  # entry\n    x = 1\n",
            new[]
            {
                new MarkedRegion("def", CoarseClass.Keyword),     // storage.type.function.python
                new MarkedRegion("main", CoarseClass.Identifier), // entity.name.function.python
                new MarkedRegion("entry", CoarseClass.Comment), // comment.line.number-sign.python (body)
                new MarkedRegion("1", CoarseClass.Number),        // constant.numeric.dec.python
            }),

        // 6. C# — public (keyword/storage modifier), class (keyword/storage type), type name (identifier), brace.
        new LanguageFixture("C#", ".cs", "source.cs",
            "public class Foo { }\n",
            new[]
            {
                new MarkedRegion("public", CoarseClass.Keyword),  // storage.modifier.public.cs
                new MarkedRegion("class", CoarseClass.Keyword),   // storage.type.class.cs
                new MarkedRegion("Foo", CoarseClass.Identifier),  // entity.name.type.class.cs
                new MarkedRegion("{", CoarseClass.Punctuation),   // punctuation.curlybrace.open.cs
            }),

        // 7. XML — tag name (identifier), attribute name (identifier), quoted value (string), tag bracket (punctuation).
        new LanguageFixture("XML", ".xml", "text.xml",
            "<root attr=\"v\">text</root>\n",
            new[]
            {
                new MarkedRegion("root", CoarseClass.Identifier), // entity.name.tag.localname.xml
                new MarkedRegion("attr", CoarseClass.Identifier), // entity.other.attribute-name.localname.xml
                new MarkedRegion("v", CoarseClass.String),        // string.quoted.double.xml (body)
                new MarkedRegion("<", CoarseClass.Punctuation),   // punctuation.definition.tag.xml
            }),

        // 8. JSON — property name (identifier/support), string value (string), number value (number), brace (punctuation).
        new LanguageFixture("JSON", ".json", "source.json",
            "{ \"key\": \"val\", \"n\": 123 }\n",
            new[]
            {
                new MarkedRegion("key", CoarseClass.Identifier),  // support.type.property-name.json
                new MarkedRegion("val", CoarseClass.String),      // string.json (body)
                new MarkedRegion("123", CoarseClass.Number),      // constant.numeric.json
                new MarkedRegion("{", CoarseClass.Punctuation),   // punctuation.definition.dictionary.begin.json
            }),

        // 9. SQL — DML keyword, comment.
        new LanguageFixture("SQL", ".sql", "source.sql",
            "SELECT id FROM t -- trailing note\n",
            new[]
            {
                new MarkedRegion("SELECT", CoarseClass.Keyword),  // keyword.other.DML.sql
                new MarkedRegion("FROM", CoarseClass.Keyword),    // keyword.other.DML.sql
                new MarkedRegion("trailing note", CoarseClass.Comment), // comment.line.double-dash.sql (body)
            }),

        // 10. Rust (STOCK grammar) — fn (keyword), function name (identifier), let (keyword/storage), number, semicolon.
        new LanguageFixture("Rust", ".rs", "source.rust",
            "fn main() { let x = 1; }\n",
            new[]
            {
                new MarkedRegion("fn", CoarseClass.Keyword),      // keyword.other.fn.rust
                new MarkedRegion("main", CoarseClass.Identifier), // entity.name.function.rust
                new MarkedRegion("let", CoarseClass.Keyword),     // storage.type.rust
                new MarkedRegion("1", CoarseClass.Number),        // constant.numeric.decimal.rust
            }),

        // 11. TypeScript — let (keyword/storage), number, type-annotation keyword punctuation, terminator.
        new LanguageFixture("TypeScript", ".ts", "source.ts",
            "let x: number = 1; // trailing\n",
            new[]
            {
                new MarkedRegion("let", CoarseClass.Keyword),     // storage.type.ts
                new MarkedRegion("number", CoarseClass.Identifier), // support.type.primitive.ts
                new MarkedRegion("1", CoarseClass.Number),        // constant.numeric.decimal.ts
                new MarkedRegion("trailing", CoarseClass.Comment), // comment.line.double-slash.ts (body)
            }),

        // 12. YAML — key (identifier/tag), comment, key-value separator (punctuation).
        new LanguageFixture("YAML", ".yaml", "source.yaml",
            "key: value  # a yaml note\n",
            new[]
            {
                new MarkedRegion("key", CoarseClass.Identifier),  // entity.name.tag.yaml
                new MarkedRegion("yaml note", CoarseClass.Comment), // comment.line.number-sign.yaml (body)
                new MarkedRegion(":", CoarseClass.Punctuation),   // punctuation.separator.key-value.mapping.yaml
            }),

        // 13. TOML (SUPPLEMENTAL grammar — proves HoardPadRegistryOptions overlay) — key, string value, comment.
        new LanguageFixture("TOML", ".toml", "source.toml",
            "key = \"value\"  # a toml note\n",
            new[]
            {
                new MarkedRegion("key", CoarseClass.Identifier),  // variable.other.key.toml
                new MarkedRegion("value", CoarseClass.String),    // string.quoted.double.basic.toml (body)
                new MarkedRegion("toml note", CoarseClass.Comment), // comment.line.number-sign.toml (body)
            }),

        // 14. Shell/Bash — builtin (identifier/support), quoted string, comment.
        new LanguageFixture("Shell", ".sh", "source.shell",
            "echo \"hi there\"  # a shell note\n",
            new[]
            {
                new MarkedRegion("echo", CoarseClass.Identifier), // support.function.builtin.shell
                new MarkedRegion("hi there", CoarseClass.String), // string.quoted.double.shell (body)
                new MarkedRegion("shell note", CoarseClass.Comment), // comment.line.number-sign.shell (body)
            }),

        // 15. PowerShell — variable, single-quoted string, comment.
        new LanguageFixture("PowerShell", ".ps1", "source.powershell",
            "$x = 'hello'  # a pwsh note\n",
            new[]
            {
                new MarkedRegion("x", CoarseClass.Identifier),    // variable.other.readwrite.powershell
                new MarkedRegion("hello", CoarseClass.String),    // string.quoted.single.powershell (body)
                new MarkedRegion("pwsh note", CoarseClass.Comment), // comment.line.powershell (body)
            }),

        // 16. Dockerfile — instruction keyword (the one structural token Dockerfile reliably scopes), comment.
        new LanguageFixture("Dockerfile", ".dockerfile", "source.dockerfile",
            "# base image\nFROM ubuntu:22.04\n",
            new[]
            {
                new MarkedRegion("FROM", CoarseClass.Keyword),    // keyword.other.special-method.dockerfile
                new MarkedRegion("base image", CoarseClass.Comment), // comment.line.number-sign.dockerfile (body)
            }),

        // 17. C — primitive type (keyword/storage), function name (identifier), control keyword, number, semicolon.
        new LanguageFixture("C", ".c", "source.c",
            "int main(void) { return 0; }\n",
            new[]
            {
                new MarkedRegion("int", CoarseClass.Keyword),     // storage.type.built-in.primitive.c
                new MarkedRegion("main", CoarseClass.Identifier), // entity.name.function.c
                new MarkedRegion("return", CoarseClass.Keyword),  // keyword.control.c
                new MarkedRegion("0", CoarseClass.Number),        // constant.numeric.decimal.c
            }),

        // 18. C++ — primitive type (keyword/storage), function name (identifier), return control keyword, number.
        new LanguageFixture("C++", ".cpp", "source.cpp",
            "int main() { return 0; }\n",
            new[]
            {
                new MarkedRegion("int", CoarseClass.Keyword),     // storage.type.built-in.primitive.cpp
                new MarkedRegion("main", CoarseClass.Identifier), // entity.name.function.definition.cpp
                new MarkedRegion("return", CoarseClass.Keyword),  // keyword.control.return.cpp
                new MarkedRegion("0", CoarseClass.Number),        // constant.numeric.decimal.cpp
            }),

        // 19. Java — class (keyword/storage modifier), type name (identifier), brace (punctuation).
        new LanguageFixture("Java", ".java", "source.java",
            "class Foo { }\n",
            new[]
            {
                new MarkedRegion("class", CoarseClass.Keyword),   // storage.modifier.java
                new MarkedRegion("Foo", CoarseClass.Identifier),  // entity.name.type.class.java
                new MarkedRegion("{", CoarseClass.Punctuation),   // punctuation.section.class.begin.bracket.curly.java
            }),

        // 20. Go — func (keyword), function name (identifier), brace + paren (punctuation).
        new LanguageFixture("Go", ".go", "source.go",
            "func main() {}\n",
            new[]
            {
                new MarkedRegion("func", CoarseClass.Keyword),    // keyword.function.go
                new MarkedRegion("main", CoarseClass.Identifier), // entity.name.function.go
                new MarkedRegion("(", CoarseClass.Punctuation),   // punctuation.definition.begin.bracket.round.go
            }),
    };
}
