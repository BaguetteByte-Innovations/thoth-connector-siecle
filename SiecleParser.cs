using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Thoth.Integration.Abstractions;

namespace Thoth.Connectors.Siecle;

/// <summary>
/// Parsing pur d'un export SIECLE-BEE (élèves) → <see cref="ThothRawIdentity"/>.
///
/// Embarqué dans l'exécutable autonome (indépendant du moteur : on ne référence que
/// <c>Thoth.Integration.Abstractions</c>). Aucune I/O réseau, aucun logging : les fonctions
/// sont pures, le caller (<see cref="ConnectorRunner"/>) décide quoi émettre.
///
/// Philosophie (comme tout connecteur) : on PARSE, on ne NETTOIE pas. Le moteur sanitize
/// et valide derrière.
///
/// Spécificités SIECLE :
/// - Source = un lot de ZIP, chacun contenant un unique XML (BEE_ELEVES, BEE_STRUCTURES…).
///   Seul <c>ExportXML_ElevesSansAdresses</c> (racine <c>BEE_ELEVES</c>) est nécessaire ;
///   on identifie le bon ZIP par sa racine XML, pas par son nom : un nom de fichier peut être
///   renommé ou tronqué, il n'est pas une identité.
/// - Un élève se recompose intra-fichier en joignant, sur <c>ELEVE_ID</c>, le bloc
///   &lt;ELEVES&gt; (identité civile) et le bloc &lt;STRUCTURES&gt; (classe + groupes de
///   l'année en cours). On n'émet QUE les inscrits (division courante <c>TYPE_STRUCTURE="D"</c>).
/// - Encodage source <c>ISO-8859-15</c> (Latin-9), décodé via <c>CodePagesEncodingProvider</c>.
/// </summary>
internal static class SiecleParser
{
    /// <summary>Racine XML du fichier élèves (ExportXML_ElevesSansAdresses).</summary>
    public const string ElevesRootName = "BEE_ELEVES";

    static SiecleParser()
    {
        // ISO-8859-15 (code page 28605) hors du set runtime par défaut. Idempotent.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        IgnoreWhitespace = true,
        DtdProcessing = DtdProcessing.Prohibit,   // pas de DTD dans SIECLE ; fail-closed sur XXE
        CloseInput = false                         // les flux sont disposés par leur `using` appelant
    };

    // ==============================================================================
    // ROUTAGE DES FICHIERS — par contenu (racine XML), jamais par nom de fichier
    // ==============================================================================

    /// <summary>Chemin du premier ZIP dont le XML interne a pour racine BEE_ELEVES, sinon null.</summary>
    public static string? FindElevesZip(IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            if (string.Equals(TryReadXmlRoot(path), ElevesRootName, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }

    /// <summary>Nom local de l'élément racine du XML contenu dans le ZIP (lecture du seul prologue).</summary>
    private static string? TryReadXmlRoot(string zipPath)
    {
        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry? entry = FindXmlEntry(archive);
            if (entry is null) return null;

            using Stream stream = entry.Open();
            using XmlReader reader = XmlReader.Create(stream, ReaderSettings);
            reader.MoveToContent();
            return reader.NodeType == XmlNodeType.Element ? reader.LocalName : null;
        }
        catch
        {
            // Fichier non-ZIP, ZIP corrompu, XML illisible… : ce n'est pas le bon, on l'ignore.
            return null;
        }
    }

    private static ZipArchiveEntry? FindXmlEntry(ZipArchive archive)
        => archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));

    // ==============================================================================
    // PARSING — conversion source → ThothRawIdentity (aucune sanitization)
    // ==============================================================================

    /// <summary>
    /// Passe 1 : indexe les inscriptions de l'année en cours (bloc &lt;STRUCTURES&gt;) et
    /// récupère le RNE de l'école (PARAMETRES/UAJ → Scope). Saute les gros blocs
    /// inutiles (&lt;ELEVES&gt;, &lt;OPTIONS&gt;…) sans les matérialiser.
    /// </summary>
    public static Dictionary<string, StructureInfo> BuildStructureIndex(string zipPath, out string? scope, CancellationToken ct = default)
    {
        Dictionary<string, StructureInfo> index = new(StringComparer.Ordinal);
        scope = null;

        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = FindXmlEntry(archive)
            ?? throw new InvalidDataException("Le ZIP élèves ne contient aucun fichier .xml.");

        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, ReaderSettings);
        reader.MoveToContent();

        while (!reader.EOF)
        {
            ct.ThrowIfCancellationRequested();

            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.Name)
            {
                case "UAJ":
                    // RNE de l'école, sous PARAMETRES. On prend la première occurrence.
                    if (scope is null) scope = reader.ReadElementContentAsString();
                    else reader.Skip();
                    continue;

                case "STRUCTURES_ELEVE":
                {
                    XElement el = (XElement)XNode.ReadFrom(reader);
                    string? eleveId = (string?)el.Attribute("ELEVE_ID");
                    if (eleveId is null) continue;

                    string? division = null;
                    List<string> groups = [];
                    foreach (XElement structure in el.Elements("STRUCTURE"))
                    {
                        string? code = (string?)structure.Element("CODE_STRUCTURE");
                        if (string.IsNullOrEmpty(code)) continue;

                        switch ((string?)structure.Element("TYPE_STRUCTURE"))
                        {
                            case "D": division ??= code; break;   // division = classe (une seule attendue)
                            case "G": groups.Add(code);   break;  // groupe pédagogique
                        }
                    }

                    index[eleveId] = new StructureInfo(division, groups);
                    continue;
                }

                // Gros blocs sans intérêt pour la passe 1 : on saute le sous-arbre entier.
                case "ELEVES":
                case "OPTIONS":
                case "DPPS":
                case "BOURSES":
                    reader.Skip();
                    continue;

                default:
                    reader.Read();
                    continue;
            }
        }

        return index;
    }

    /// <summary>
    /// Passe 2 : streame le bloc &lt;ELEVES&gt;, joint chaque élève à son inscription
    /// (<paramref name="structures"/>) et n'émet que les inscrits (division courante présente).
    /// Un seul élève est matérialisé en mémoire à la fois.
    /// </summary>
    public static IEnumerable<ThothRawIdentity> StreamStudents(
        string zipPath, IReadOnlyDictionary<string, StructureInfo> structures, string? scope)
    {
        using ZipArchive archive = ZipFile.OpenRead(zipPath);
        ZipArchiveEntry entry = FindXmlEntry(archive)
            ?? throw new InvalidDataException("Le ZIP élèves ne contient aucun fichier .xml.");

        using Stream stream = entry.Open();
        using XmlReader reader = XmlReader.Create(stream, ReaderSettings);
        reader.MoveToContent();

        while (!reader.EOF)
        {
            if (reader.NodeType != XmlNodeType.Element)
            {
                reader.Read();
                continue;
            }

            switch (reader.Name)
            {
                case "ELEVE":
                {
                    XElement el = (XElement)XNode.ReadFrom(reader);
                    string? eleveId = (string?)el.Attribute("ELEVE_ID");

                    // Filtre : pas d'inscription courante (sortant) → on n'émet pas.
                    if (eleveId is null
                        || !structures.TryGetValue(eleveId, out StructureInfo info)
                        || info.Division is null)
                        continue;

                    yield return new ThothRawIdentity(
                        Role:                     UserRole.Student,
                        SourceId:                 (string?)el.Element("ID_NATIONAL"),     // INE national, clé de réconciliation
                        LastName:                 (string?)el.Element("NOM_DE_FAMILLE"),
                        FirstName:                (string?)el.Element("PRENOM"),          // PRENOM2/3 (prénoms secondaires) volontairement ignorés
                        BirthDate:                ParseSiecleDate((string?)el.Element("DATE_NAISS")),
                        Scope:                    scope,                                  // école = RNE/UAJ
                        Class:                    info.Division,                          // classe courante (obligatoire pour un Student)
                        StudentPedagogicalGroups: info.Groups is { Count: > 0 } ? info.Groups : null);

                    continue;
                }

                // Blocs postérieurs à <ELEVES> : inutiles ici, on saute le sous-arbre.
                case "OPTIONS":
                case "STRUCTURES":
                case "DPPS":
                case "BOURSES":
                    reader.Skip();
                    continue;

                default:
                    reader.Read();
                    continue;
            }
        }
    }

    /// <summary>Dates SIECLE au format dd/MM/yyyy → DateOnly?. Échec de parse → null (le moteur tranche).</summary>
    private static DateOnly? ParseSiecleDate(string? raw)
        => DateOnly.TryParseExact(raw, "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;

    /// <summary>Inscription courante d'un élève : sa division (classe) et ses groupes pédagogiques.</summary>
    public readonly record struct StructureInfo(string? Division, List<string> Groups);
}
