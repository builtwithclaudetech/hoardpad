// SupplementalGrammars.cs — reads the FR-7 supplemental .tmLanguage.json grammars bundled as
// embedded resources (TD §5.6: all grammars bundled offline, A-7/NFR-7). Only TOML is supplemental;
// the other 19 v1 languages (incl. Rust) come from the stock TextMateSharp.Grammars set.

using System.IO;
using System.Reflection;
using TextMateSharp.Internal.Grammars.Reader;
using TextMateSharp.Internal.Types;

namespace HoardPad.App.Editor;

/// <summary>Loads the bundled supplemental TextMate grammars (embedded resources).</summary>
internal static class SupplementalGrammars
{
    // The .tmLanguage.json is embedded via <EmbeddedResource> in the .csproj; the resource name is
    // the default-namespace + path with separators as dots.
    private const string TomlResourceName = "HoardPad.App.Assets.grammars.toml.tmLanguage.json";

    /// <summary>Parse the embedded TOML grammar into an <see cref="IRawGrammar"/> via TextMateSharp's reader.</summary>
    public static IRawGrammar LoadToml()
    {
        Assembly assembly = typeof(SupplementalGrammars).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(TomlResourceName)
            ?? throw new FileNotFoundException(
                $"Embedded supplemental grammar resource '{TomlResourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return GrammarReader.ReadGrammarSync(reader);
    }
}
