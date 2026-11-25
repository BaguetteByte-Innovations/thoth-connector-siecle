using System.Globalization;
using System.Text;
using System.Text.Json;
using Thoth.Integration.Abstractions;

namespace Thoth.Connectors.Siecle;

/// <summary>
/// Point d'entrée du connecteur SIECLE, exécutable autonome lancé par le moteur.
///
/// Lit une requête JSON sur stdin (inspect | ingest), délègue le parsing à
/// <see cref="SiecleParser"/> (parsing pur, sans dépendance au moteur), et émet le
/// résultat en NDJSON sur stdout : une ligne <c>idy:</c> par élève (ingest), puis
/// exactement une ligne <c>res:</c> finale. Le moteur sanitize/valide derrière.
/// </summary>
internal static class ConnectorRunner
{
    public static async Task<int> RunAsync()
    {
        // Sans BOM dans les deux directions : pas de préfixe parasite sur les flux NDJSON.
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        // Retenu pour qu'un échec réponde à la forme du mode demandé : le moteur lit un
        // InspectionResult après un inspect, un IngestionResult sinon. Même règle que les
        // connecteurs CSV.
        string? mode = null;

        try
        {
            string? requestLine = await Console.In.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(requestLine))
            {
                EmitFailure(mode, "Aucune requête reçue sur stdin.");
                return 1;
            }

            ConnectorRequest? req = JsonSerializer.Deserialize(requestLine, ConnectorJsonContext.Default.ConnectorRequest);
            if (req is null)
            {
                EmitFailure(mode, "Requête JSON invalide.");
                return 1;
            }

            mode = req.Mode;

            return req.Mode switch
            {
                "inspect" => RunInspect(req),
                "ingest"  => RunIngest(req),
                _         => HandleUnknownMode(req.Mode)
            };
        }
        catch (Exception ex)
        {
            EmitFailure(mode, $"Crash connecteur : {ex.Message}");
            return 1;
        }
    }

    private static int HandleUnknownMode(string mode)
    {
        EmitFailure(mode, $"Mode inconnu : '{mode}'.");
        return 1;
    }

    // ==============================================================================
    // INSPECT
    // ==============================================================================

    private static int RunInspect(ConnectorRequest req)
    {
        ConnectorContext context = req.Context;
        if (context.Sources is null || context.Sources.Count == 0)
        {
            EmitInspectionResult(new InspectionResult(false, false, [], [], "Aucun fichier n'a été fourni."));
            return 0;
        }

        // Schéma fixe : SIECLE ne lit ni périmètre ni mapping sur ses sources, seulement leurs
        // chemins — il identifie ses fichiers par leur CONTENU, jamais par leur nom. Une analyse
        // porte une seule source : un fichier qui n'est pas un export élèves est refusé, nommé.
        ConnectorSource elevesSource = context.Sources[0];
        if (!SiecleParser.IsElevesZip(elevesSource.PhysicalPath))
        {
            EmitInspectionResult(new InspectionResult(false, false, [], [],
                $"« {SourceName(elevesSource)} » n'est pas l'export élèves de SIECLE-BEE (ExportXML_ElevesSansAdresses.zip) : retirer ce fichier du dépôt."));
            return 0;
        }

        string elevesZip = elevesSource.PhysicalPath;
        try
        {
            Dictionary<string, SiecleParser.StructureInfo> structures =
                SiecleParser.BuildStructureIndex(elevesZip, out string? scope, out DateOnly exportDate);

            // Schéma fixe : les clés de l'aperçu sont des identifiants de CIBLES (noms de propriétés
            // de ThothRawIdentity), jamais des intitulés d'affichage — la console les libelle dans sa
            // propre langue. Les valeurs restent des chaînes d'affichage (liste jointe par « , »).
            List<Dictionary<string, string>> sample = [];
            foreach (ThothRawIdentity student in SiecleParser.StreamStudents(elevesZip, structures, scope, exportDate))
            {
                sample.Add(new Dictionary<string, string>
                {
                    [nameof(ThothRawIdentity.SourceId)]                 = student.SourceId ?? string.Empty,   // INE
                    [nameof(ThothRawIdentity.LastName)]                 = student.LastName ?? string.Empty,
                    [nameof(ThothRawIdentity.FirstName)]                = student.FirstName ?? string.Empty,
                    [nameof(ThothRawIdentity.BirthDate)]                = student.BirthDate?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty,
                    [nameof(ThothRawIdentity.Class)]                    = student.Class ?? string.Empty,
                    [nameof(ThothRawIdentity.StudentPedagogicalGroups)] = student.StudentPedagogicalGroups is { Count: > 0 } g ? string.Join(", ", g) : string.Empty
                });

                if (sample.Count >= 5) break;
            }

            EmitInspectionResult(new InspectionResult(
                Success:               true,
                RequiresManualMapping: false,            // schéma fixe : le front saute le mapping
                SourceFields:          [],
                SampleData:            sample,
                ErrorMessage:          null,
                ExpectedTargets:       null,
                FixedRole:             UserRole.Student));
            return 0;
        }
        catch (Exception ex)
        {
            EmitInspectionResult(new InspectionResult(false, false, [], [], $"Erreur d'analyse SIECLE sur « {SourceName(elevesSource)} » : {ex.Message}"));
            return 0;
        }
    }

    // ==============================================================================
    // INGEST
    // ==============================================================================

    private static int RunIngest(ConnectorRequest req)
    {
        string syncId = req.SyncId ?? string.Empty;
        ConnectorContext context = req.Context;

        if (context.Sources is null || context.Sources.Count == 0)
        {
            EmitIngestionResult(new IngestionResult(false, syncId, 0, 0, 0, "Aucun fichier n'a été fourni."));
            return 0;
        }

        // Schéma fixe : SIECLE ne lit ni périmètre ni mapping sur ses sources, seulement leurs
        // chemins — il identifie ses fichiers par leur CONTENU, jamais par leur nom. Toutes les
        // sources sont contrôlées AVANT la première identité : la première qui n'est pas un export
        // élèves fait échouer le lancement, nommée.
        foreach (ConnectorSource source in context.Sources)
        {
            if (!SiecleParser.IsElevesZip(source.PhysicalPath))
            {
                EmitIngestionResult(new IngestionResult(false, syncId, 0, 0, 0,
                    $"« {SourceName(source)} » n'est pas l'export élèves de SIECLE-BEE (ExportXML_ElevesSansAdresses.zip) : retirer ce fichier des sources de l'import."));
                return 0;
            }
        }

        // Chaque export élèves est lu (un par école), dans l'ordre reçu.
        int processed = 0;
        foreach (ConnectorSource source in context.Sources)
        {
            try
            {
                Dictionary<string, SiecleParser.StructureInfo> structures =
                    SiecleParser.BuildStructureIndex(source.PhysicalPath, out string? scope, out DateOnly exportDate);

                foreach (ThothRawIdentity identity in SiecleParser.StreamStudents(source.PhysicalPath, structures, scope, exportDate))
                {
                    EmitIdentity(identity);
                    processed++;
                }
            }
            catch (Exception ex)
            {
                // Un export illisible fait échouer tout le lancement : le moteur abandonne le lot.
                EmitIngestionResult(new IngestionResult(false, syncId, 0, 0, 0,
                    $"Erreur d'ingestion SIECLE sur « {SourceName(source)} » : {ex.Message}"));
                return 0;
            }
        }

        EmitIngestionResult(new IngestionResult(true, syncId, processed, 0, 0));
        return 0;
    }

    /// <summary>Nom d'origine d'une source pour les messages, à défaut le nom du fichier physique.</summary>
    private static string SourceName(ConnectorSource source)
        => string.IsNullOrWhiteSpace(source.OriginalName) ? Path.GetFileName(source.PhysicalPath) : source.OriginalName;

    // ==============================================================================
    // IO PROTOCOLAIRE (préfixes idy:/res:)
    // ==============================================================================

    private static void EmitIdentity(ThothRawIdentity id)
    {
        string json = JsonSerializer.Serialize(id, ConnectorJsonContext.Default.ThothRawIdentity);
        Console.Out.Write(ConnectorLinePrefix.Identity);
        Console.Out.WriteLine(json);
    }

    private static void EmitInspectionResult(InspectionResult result)
    {
        string json = JsonSerializer.Serialize(result, ConnectorJsonContext.Default.InspectionResult);
        Console.Out.Write(ConnectorLinePrefix.Result);
        Console.Out.WriteLine(json);
    }

    private static void EmitIngestionResult(IngestionResult result)
    {
        string json = JsonSerializer.Serialize(result, ConnectorJsonContext.Default.IngestionResult);
        Console.Out.Write(ConnectorLinePrefix.Result);
        Console.Out.WriteLine(json);
    }

    /// <summary>
    /// Résultat d'échec à la forme du mode : un <see cref="InspectionResult"/> après un inspect,
    /// un <see cref="IngestionResult"/> sinon (mode inconnu, ou requête illisible).
    /// </summary>
    private static void EmitFailure(string? mode, string message)
    {
        if (mode == "inspect")
            EmitInspectionResult(new InspectionResult(false, false, [], [], message));
        else
            EmitIngestionResult(new IngestionResult(false, string.Empty, 0, 0, 0, message));
    }
}
