using Markdig;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;
using System.Web;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using Tesseract;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;


var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPerFrontend", policy =>
    {
        policy.WithOrigins("http://localhost:5156")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddHttpClient();

builder.Services.AddControllers();
var app = builder.Build();

app.UseCors("CorsPerFrontend");

app.MapPost("/api/intento", async (IntentoRequest input) =>
{
    Console.WriteLine("Sto avviando api/intento");

    if (string.IsNullOrWhiteSpace(input.MessaggioUtente) || string.IsNullOrWhiteSpace(input.UltimaRispostaAI))
        return Results.BadRequest("Messaggi insufficienti.");

    string prompt = $"""
    Di seguito è riportato uno scambio tra un utente e un assistente AI.

    Risposta precedente dell'AI:
    "{input.UltimaRispostaAI}"

    Nuovo messaggio dell'utente:
    "{input.MessaggioUtente}"

    Analizza la coerenza tra i due messaggi.

    L'utente sta cercando di:
    - approfondire o chiarire un'informazione contenuta nella risposta dell'AI
    - oppure sta cambiando completamente argomento?

    Valuta anche i riferimenti impliciti (es. pronomi, soggetti sottintesi) e se è logico pensare che si riferisca al tema della risposta precedente.

    Rispondi solo con:
    - "si" se c'è continuità logica (approfondimento o chiarimento)
    - "no" se è un cambio di argomento
    """;

    var risultatoGrezzo = await OpenRouterHelper.ChiamaOpenRouterAsync(prompt, input.Modello);

    // Rimuove tag HTML e normalizza
    string risultatoPulito = Regex.Replace(risultatoGrezzo, "<.*?>", "").Trim().ToLowerInvariant();

    Console.WriteLine("Risultato AI pulito: " + risultatoPulito);

    return Results.Ok(new { intent = risultatoPulito });
}).RequireCors("CorsPerFrontend");

app.MapPost("/api/verificaContesto", async (HttpRequest req) =>
{
    Console.WriteLine("Sto avviando api/verificaContesto");

    using var reader = new StreamReader(req.Body);
    var body = await reader.ReadToEndAsync();
    var json = JsonSerializer.Deserialize<JsonElement>(body);

    var messaggio = json.GetProperty("messaggio").GetString() ?? "";
    var modello = json.GetProperty("modello").GetString() ?? "gpt-4o";

    Console.WriteLine($"Modello usato per verifica contesto: {modello}");
    Console.WriteLine($"Messaggio da analizzare: {messaggio}");

    // Escape virgolette nel messaggio per evitare problemi nel prompt
    var messaggioSafe = messaggio.Replace("\"", "\\\"");

    var prompt = $"""
    Sei un sistema che deve rispondere **esclusivamente** con "sì" o "no" (in minuscolo, senza virgolette, senza altri caratteri o spazi).

    La domanda è: "{messaggioSafe}"

    - Se la domanda è un semplice saluto, ringraziamento o domanda personale generica (es. "ciao", "come stai?", "grazie", "tutto bene?"), **devi rispondere con "no"** perché NON serve alcun contesto esterno per rispondere correttamente.

    - Solo se la domanda contiene riferimenti ambigui, termini che richiedono contesto esterno, o è una domanda tecnica/scientifica che necessita informazioni aggiuntive, rispondi con "sì".

    **Non ragionare come un chatbot con memoria, ma come un sistema logico che deve capire se il contesto è davvero necessario per rispondere**.

    Rispondi solo con "sì" o "no".
    """;

    var risultato = await OpenRouterHelper.ChiamaOpenRouterAsync(modello, prompt);

    Console.WriteLine($"Risposta AI RAW: '{risultato}'");

    var rispostaPulita = risultato.Trim().ToLower();

    // Controllo più tollerante per "sì"
    bool serveContesto = rispostaPulita.Contains("sì") || rispostaPulita.StartsWith("si");

    Console.WriteLine($"Serve contesto (bool): {serveContesto}");

    // Fallback con parole chiave (opzionale)
    if (!serveContesto)
    {
        var paroleChiaveContesto = new[] { "riassunto", "approfondito", "vita", "storia", "dettagli", "spiega" };
        if (paroleChiaveContesto.Any(k => messaggio.ToLower().Contains(k)))
        {
            serveContesto = true;
            Console.WriteLine("Fallback: serve contesto perché la domanda contiene parole chiave.");
        }
    }

    return Results.Json(new { serveContesto });
});

app.MapPost("/api/chat", async (HttpContext http, IHttpClientFactory httpClientFactory) =>
{
    var swTotale = Stopwatch.StartNew();
    Console.WriteLine("➡️ Avvio api/chat");

    using var reader = new StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync();

    ChatInput? dati;
    try
    {
        dati = JsonSerializer.Deserialize<ChatInput>(body);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Errore JSON: " + ex.Message);
        return Results.BadRequest(new { errore = "JSON malformato" });
    }

    if (dati is null || string.IsNullOrWhiteSpace(dati.Messaggio))
        return Results.BadRequest(new { errore = "Messaggio vuoto o malformato" });

    if (dati.Messaggio.Trim().ToLower() == "/utilizzi")
    {
        var utilizzi = await OpenRouterHelper.VerificaUtilizziAsync();
        return Results.Json(new { testo = utilizzi });
    }

    string testo = dati.Messaggio;
    string risposta;

    if (dati.UsaContesto)
    {
        Console.WriteLine("➡️ Richiesto uso del contesto");
        var client = httpClientFactory.CreateClient();

        // Avvia richiesta contesto
        var contestoTask = client.PostAsync("http://localhost:5309/api/contesto", new StringContent(
            JsonSerializer.Serialize(new { testo }),
            Encoding.UTF8,
            "application/json"
        ));

        // Nessun await qui!

        Console.WriteLine("➡️ In attesa risposta da /api/contesto");

        var contestoResponseHttp = await contestoTask;
        if (!contestoResponseHttp.IsSuccessStatusCode)
        {
            Console.WriteLine("❌ Errore /api/contesto: " + contestoResponseHttp.StatusCode);
            return Results.Problem("Errore nella richiesta al contesto.");
        }

        var responseContent = await contestoResponseHttp.Content.ReadAsStringAsync();
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

        ContestoResponse? contestoResponse;
        try
        {
            contestoResponse = JsonSerializer.Deserialize<ContestoResponse>(responseContent, options);
        }
        catch (Exception ex)
        {
            Console.WriteLine("❌ Parsing contesto fallito: " + ex.Message);
            return Results.Problem("Errore nel parsing del contesto.");
        }

        // Prompt costruito da contesto + messaggio
        var prompt = string.Join("\n", contestoResponse.Contesto) + "\nUtente: " + testo;

        Console.WriteLine("🧠 Invio prompt con contesto a OpenRouter...");
        risposta = await OpenRouterHelper.ChiamaOpenRouterAsync(prompt, dati.Modello);
    }
    else
    {
        Console.WriteLine("➡️ Uso diretto senza contesto");
        risposta = await OpenRouterHelper.ChiamaOpenRouterAsync(testo, dati.Modello);
    }

    Console.WriteLine($"✅ Tempo totale risposta: {swTotale.ElapsedMilliseconds}ms");

    return Results.Json(new
    {
        testo = risposta,
        embeddingUsato = dati.UsaContesto
    });
})
.RequireCors("CorsPerFrontend");

app.MapPost("/api/salva", async (HttpContext http) =>
{
    Console.WriteLine("Sto avviando api/salva");

    using var reader = new StreamReader(http.Request.Body);
    var body = await reader.ReadToEndAsync();

    SalvataggioInput? dati;
    try
    {
        dati = JsonSerializer.Deserialize<SalvataggioInput>(body);
    }
    catch (Exception ex)
    {
        Console.WriteLine("Errore deserializzazione JSON in /api/salva: " + ex.Message);
        return Results.BadRequest(new { errore = "JSON malformato" });
    }

    if (dati is null || string.IsNullOrWhiteSpace(dati.Domanda) || string.IsNullOrWhiteSpace(dati.Risposta))
        return Results.BadRequest(new { errore = "Domanda o risposta mancanti" });

    string testoCompleto = dati.Domanda + " " + dati.Risposta;

    // CHIAMATA HTTP al servizio embedding (FastAPI locale)
    using var client = new HttpClient();
    HttpResponseMessage embeddingResponse;
    try
    {
        embeddingResponse = await client.PostAsJsonAsync("http://localhost:8000/embedding", new { testo = testoCompleto });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Errore nella chiamata al server Python: " + ex.Message);
        return Results.Problem("Errore nella comunicazione con il server Python.");
    }

    if (!embeddingResponse.IsSuccessStatusCode)
    {
        var errore = await embeddingResponse.Content.ReadAsStringAsync();
        Console.WriteLine("Errore dal server Python: " + errore);
        return Results.Problem("Errore dal server Python.");
    }

    var json = await embeddingResponse.Content.ReadAsStringAsync();
    List<float>? embeddingCompleto;
    try
    {
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("embedding", out var embeddingElement))
            return Results.Problem("Campo 'embedding' mancante nella risposta.");

        embeddingCompleto = JsonSerializer.Deserialize<List<float>>(embeddingElement.GetRawText());
    }
    catch (Exception ex)
    {
        Console.WriteLine("Errore parsing embedding JSON: " + ex.Message);
        return Results.Problem("Errore parsing JSON.");
    }

    if (embeddingCompleto is null || embeddingCompleto.Count == 0)
        return Results.Problem("Embedding non valido.");

    // Salvataggio in Qdrant
    await QdrantHelper.InserisciInQdrantAsync(embeddingCompleto, testoCompleto);
    return Results.Ok(new { successo = true });
})
.RequireCors("CorsPerFrontend");

app.MapPost("/api/dati", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    try
    {
        var dati = JsonSerializer.Deserialize<Contatto>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        if (dati == null || string.IsNullOrWhiteSpace(dati.Email))
            return Results.BadRequest(new { errore = "Dati mancanti o malformati" });

        var pathFile = @"C:\\Users\\tommaso.rometti\\OneDrive - EOLO SpA\\Desktop\\TommasoEOlo\\TommasoEOlo.AppHost\\dati.txt";
        var directory = Path.GetDirectoryName(pathFile);
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory);

        var testoDaSalvare = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - Telefono: {dati.Telefono}, Nome: {dati.Nome}, Cognome: {dati.Cognome}, Email: {dati.Email}{Environment.NewLine}";
        await File.AppendAllTextAsync(pathFile, testoDaSalvare);

        return Results.Ok(new { messaggio = "Dati salvati correttamente" });
    }
    catch (Exception ex)
    {
        Console.WriteLine("Errore scrittura file: " + ex);
        return Results.BadRequest(new { errore = ex.Message });
    }
}).RequireCors("CorsPerFrontend");

app.MapPost("/api/immagine", async (PromptRequest richiesta) =>
{
    Console.WriteLine("Sto avviando api/immagine");
    // 1. Traduci in inglese il prompt italiano
    string promptInInglese = await OpenRouterHelper.TraduciInInglese(richiesta.Prompt, richiesta.Modello);

    // 2. Genera immagine usando il prompt tradotto
    string imageUrl = await OpenRouterHelper.GeneraImmagineConStabilityAI(promptInInglese, "stable-diffusion-v1-6");

    // 3. Ritorna la URL base64
    return Results.Ok(new { imageUrl });
})
.RequireCors("CorsPerFrontend");

app.MapPost("/api/invia", async (HttpRequest request) =>
{
    Console.WriteLine("Sto avviando api/invia");

    var form = await request.ReadFormAsync();
    var file = form.Files["file"];

    if (file == null || file.Length == 0)
        return Results.BadRequest("File non presente");

    var extension = Path.GetExtension(file.FileName).ToLower();
    string testoEstratto = null;

    try
    {
        using var stream = file.OpenReadStream();
        testoEstratto = extension switch
        {
            ".txt" => await new StreamReader(stream).ReadToEndAsync(),
            ".pdf" => InserimentoFIle.EstraiTestoDaPdf(stream),
            ".docx" => InserimentoFIle.EstraiTestoDaWord(stream),
            ".jpg" or ".jpeg" or ".png" => InserimentoFIle.EstraiTestoDaImmagine(stream),
            _ => null
        };
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Errore durante l'estrazione: {ex.Message}");
        return Results.Problem("Errore durante l'estrazione del testo dal file.");
    }

    if (testoEstratto == null)
        return Results.BadRequest("Tipo di file non supportato o vuoto");

    // Pulisce e tronca
    testoEstratto = Regex.Replace(testoEstratto, "<.*?>", "");
    testoEstratto = testoEstratto.Replace("\\n", "\n").Trim();
    if (testoEstratto.Length > 10000)
        testoEstratto = testoEstratto.Substring(0, 10000);

    return Results.Ok(new { testo = testoEstratto });
});

app.MapPost("/api/file", async (HttpContext ctx) =>
{
    Console.WriteLine("Sto avviando api/file");
    var body = await ctx.Request.ReadFromJsonAsync<DomandaConTesto>();
    if (body is null || string.IsNullOrWhiteSpace(body.Testo) || string.IsNullOrWhiteSpace(body.Domanda))
        return Results.BadRequest("Testo o domanda mancanti");

    var prompt = $"Rispondi alla seguente domanda in base al testo fornito.\n\n" +
                 $"Testo:\n\"{body.Testo}\"\n\n" +
                 $"Domanda:\n\"{body.Domanda}\"\n\n" +
                 $"Rispondi in modo chiaro e conciso, basandoti solo sul testo.";

    var risposta = await OpenRouterHelper.ChiamaOpenRouterAsync(prompt, body.Modello);

    return Results.Ok(new { risposta });

}).RequireCors("CorsPerFrontend");

app.MapPost("/api/embedding", async (HttpRequest request) =>
{
    Console.WriteLine("Sto avviando api/embedding");

    using var reader = new StreamReader(request.Body);
    var body = await reader.ReadToEndAsync();

    if (string.IsNullOrWhiteSpace(body))
        return Results.BadRequest("Body vuoto");

    // Estrai "testo" dal JSON ricevuto
    string testo;
    try
    {
        var jsonDoc = JsonDocument.Parse(body);
        if (!jsonDoc.RootElement.TryGetProperty("testo", out var testoElement))
            return Results.BadRequest("Campo 'testo' mancante nel JSON");

        testo = testoElement.GetString() ?? "";
    }
    catch
    {
        return Results.BadRequest("JSON malformato");
    }

    if (string.IsNullOrWhiteSpace(testo))
        return Results.BadRequest("Testo vuoto");

    // CHIAMATA HTTP al servizio embedding FastAPI (locale)
    using var client = new HttpClient();
    var embeddingResponse = await client.PostAsJsonAsync("http://localhost:8000/embedding", new { testo });

    if (!embeddingResponse.IsSuccessStatusCode)
    {
        var errore = await embeddingResponse.Content.ReadAsStringAsync();
        Console.Error.WriteLine($"Errore da server embedding: {errore}");
        return Results.Problem("Errore dal server embedding Python.");
    }

    var json = await embeddingResponse.Content.ReadAsStringAsync();
    List<float>? embedding;
    try
    {
        var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("embedding", out var embeddingElement))
            return Results.Problem("Campo 'embedding' mancante nella risposta.");

        embedding = JsonSerializer.Deserialize<List<float>>(embeddingElement.GetRawText());
    }
    catch (Exception ex)
    {
        return Results.Problem("Errore parsing JSON embedding: " + ex.Message);
    }

    if (embedding is null || embedding.Count == 0)
        return Results.Problem("Embedding vuoto o non valido.");

    return Results.Ok(new
    {
        embedding = embedding,
        messaggio = "Embedding calcolato con FastAPI (più veloce)."
    });
});

app.MapPost("/api/contesto", async (ContestoRequest request, ILogger<Program> logger) =>
{
    var swTotale = Stopwatch.StartNew();
    Console.WriteLine("🚀 Inizio /api/contesto");

    if (string.IsNullOrWhiteSpace(request.Testo))
        return Results.BadRequest("Il campo 'Testo' è obbligatorio.");

    var testoRichiesta = request.Testo.Trim().ToLowerInvariant();
    var queryPerEmbedding = IntentoRequest.GeneraQueryPerEmbedding(request.Testo);

    var client = new HttpClient();

    // Embedding - avviato subito
    var embeddingTask = client.PostAsJsonAsync("http://localhost:5309/api/embedding", new { testo = queryPerEmbedding });

    // Attendi risposta embedding
    var embeddingResponse = await embeddingTask;

    if (!embeddingResponse.IsSuccessStatusCode)
    {
        var errore = await embeddingResponse.Content.ReadAsStringAsync();
        return Results.Problem("Errore da /api/embedding: " + errore);
    }

    var json = await embeddingResponse.Content.ReadAsStringAsync();
    var doc = JsonDocument.Parse(json);

    if (!doc.RootElement.TryGetProperty("embedding", out var embeddingElement))
        return Results.Problem("Campo 'embedding' mancante nella risposta da /api/embedding");

    var embedding = JsonSerializer.Deserialize<List<float>>(embeddingElement.GetRawText());

    if (embedding == null || embedding.Count == 0)
        return Results.Problem("Embedding vuoto o non valido da /api/embedding");

    // Ora che hai l'embedding, avvia recupero contesto
    var risultati = await QdrantHelper.RecuperaContestoDaSerpAPIAsync(embedding, request.Testo);

    // Filtra duplicati
    var distinti = risultati
        .GroupBy(r => r.testo.Trim().ToLowerInvariant())
        .Select(g => g.First().testo)
        .Where(t => t.Trim().ToLowerInvariant() != testoRichiesta)
        .ToList();

    swTotale.Stop();
    Console.WriteLine($"✅ Fine /api/contesto in {swTotale.ElapsedMilliseconds} ms");

    return Results.Ok(new
    {
        embedding,
        contesto = distinti,
        messaggio = "Embedding calcolato, salvato e ricercato con successo."
    });
})
.RequireCors("CorsPerFrontend");

app.MapControllers();
app.Run();

record ChatInput(string Messaggio, string Modello, bool UsaContesto);
record Contatto(string Telefono, string Nome, string Cognome, string Email);
record PromptRequest(string Prompt, string Modello);
record DomandaConTesto(string Testo, string Domanda, string Modello);

public class IntentoRequest
{
    public string MessaggioUtente { get; set; } = string.Empty;
    public string UltimaRispostaAI { get; set; } = string.Empty;
    public string? Modello { get; set; }

    public static string GeneraQueryPerEmbedding(string testo, int maxLunghezza = 800)
    {
        // Pulisci da caratteri problematici
        testo = testo.Replace("\n", " ").Replace("\r", " ").Replace("\"", "'");

        // Qui puoi aggiungere un prefisso se vuoi
        string prefix = "Approfondisci e approfondisci: ";

        // Costruisci il prompt completo
        string promptCompleto = prefix + testo;

        if (promptCompleto.Length > maxLunghezza)
        {
            promptCompleto = promptCompleto.Substring(0, maxLunghezza - 3) + "...";
        }

        return promptCompleto.Trim();
    }
}

public class SalvataggioInput
{
    public string Domanda { get; set; } = string.Empty;
    public string Risposta { get; set; } = string.Empty;
}

public class ContestoResponse
{
    public List<float> Embedding { get; set; }
    public List<string> Contesto { get; set; }
    public string Messaggio { get; set; }
}

public class ContestoRequest
{
    public string Testo { get; set; } = string.Empty;
}

public static class QdrantHelper
{
    public static async Task<List<(string testo, List<float> embedding)>> RecuperaContestoDaSerpAPIAsync(List<float> embedding, string testoQuery)
    {
        Console.WriteLine("Sto avviando RecuperaContestoDaSerpAPIAsync");

        string scriptPath = @"C:\Users\tommaso.rometti\OneDrive - EOLO SpA\Desktop\TommasoEOlo\TommasoEOlo.AppHost\Embedding\SerpAPI.py";
        var embeddingJson = JsonSerializer.Serialize(embedding);
        var testoEscapato = testoQuery.Replace("\"", "\\\"");

        var psi = new ProcessStartInfo
        {
            FileName = @"C:\Users\tommaso.rometti\AppData\Local\Programs\Python\Python313\python.exe",
            Arguments = $"\"{scriptPath}\" \"search\" \"{embeddingJson.Replace("\"", "\\\"")}\" \"{testoEscapato}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new Exception("Impossibile avviare lo script SerpAPI");

        Console.WriteLine("Processo avviato, PID=" + process.Id);

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        var waitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(4));
        var completedTask = await Task.WhenAny(waitTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            try
            {
                process.Kill(true);
                Console.WriteLine("Processo Python terminato forzatamente per timeout.");
            }
            catch { }

            Console.WriteLine("Timeout SerpAPI: avvio fallback con Qdrant.");
            return await RecuperaContestoDaQdrantAsync(embedding, testoQuery);
        }

        var output = await outputTask;
        var error = await errorTask;

        Console.WriteLine("Processo Python terminato normalmente.");
        Console.WriteLine("Output da SerpAPI: " + output);
        if (!string.IsNullOrWhiteSpace(error))
            Console.WriteLine("stderr da SerpAPI: " + error);

        try
        {
            var risultati = JsonSerializer.Deserialize<List<QdrantRisultato>>(output);

            if (risultati == null)
            {
                Console.WriteLine("Output nullo: fallback Qdrant.");
                return await RecuperaContestoDaQdrantAsync(embedding, testoQuery);
            }

            var validi = risultati
                .Where(r => !string.IsNullOrWhiteSpace(r.testo) && r.embedding != null && r.embedding.Count > 0)
                .Select(r => (r.testo!, r.embedding!))
                .ToList();

            if (validi.Count == 0)
            {
                Console.WriteLine("Nessun risultato valido: fallback Qdrant.");
                return await RecuperaContestoDaQdrantAsync(embedding, testoQuery);
            }

            return validi;
        }
        catch (JsonException jex)
        {
            Console.WriteLine("Errore JSON SerpAPI: " + jex.Message + " - fallback Qdrant.");
            return await RecuperaContestoDaQdrantAsync(embedding, testoQuery);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Errore generico SerpAPI: " + ex.Message + " - fallback Qdrant.");
            return await RecuperaContestoDaQdrantAsync(embedding, testoQuery);
        }
    }

    public static async Task<List<(string testo, List<float> embedding)>> RecuperaContestoDaQdrantAsync(List<float> embedding, string testoQuery)
    {
        Console.WriteLine("Sto avviando RecuperaContestoDaQdrantAsync");
        string scriptPath = @"C:\Users\tommaso.rometti\OneDrive - EOLO SpA\Desktop\TommasoEOlo\TommasoEOlo.AppHost\Embedding\qdrant_service.py";
        var embeddingJson = JsonSerializer.Serialize(embedding);
        var testoEscapato = testoQuery.Replace("\"", "\\\""); // Escape virgolette per la shell

        var psi = new ProcessStartInfo
        {
            FileName = @"C:\Users\tommaso.rometti\AppData\Local\Programs\Python\Python313\python.exe",
            Arguments = $"\"{scriptPath}\" \"search\" \"{embeddingJson.Replace("\"", "\\\"")}\" \"{testoEscapato}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new Exception("Impossibile avviare lo script Qdrant");

        Console.WriteLine("Processo avviato, PID=" + process.Id);

        // Leggi output e error async subito
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();

        // Aspetta completamento o timeout (4 minuti)
        var waitTask = process.WaitForExitAsync();
        var timeoutTask = Task.Delay(TimeSpan.FromMinutes(4));

        var completedTask = await Task.WhenAny(waitTask, timeoutTask);

        if (completedTask == timeoutTask)
        {
            try
            {
                process.Kill(true);
                Console.WriteLine("Processo Python terminato forzatamente per timeout.");
            }
            catch { }

            throw new TimeoutException("Timeout esecuzione script Qdrant superato.");
        }

        // Ora il processo è terminato, attendi output/errori completi
        var output = await outputTask;
        var error = await errorTask;

        Console.WriteLine("Processo Python terminato normalmente.");
        Console.WriteLine("Output da Qdrant (Questo è il contesto che è stato recuperato): " + output);
        if (!string.IsNullOrWhiteSpace(error))
            Console.WriteLine("stderr da Qdrant (RecuperaContesto): " + error);

        try
        {
            var risultati = JsonSerializer.Deserialize<List<QdrantRisultato>>(output);

            if (risultati == null)
            {
                Console.WriteLine("Attenzione: risultato deserializzato è null.");
                return new List<(string, List<float>)>();
            }

            var validi = risultati
                .Where(r => !string.IsNullOrWhiteSpace(r.testo) && r.embedding != null && r.embedding.Count > 0)
                .Select(r => (r.testo!, r.embedding!))
                .ToList();
            return validi;
        }
        catch (JsonException jex)
        {
            throw new Exception("Errore deserializzazione JSON output Qdrant: " + jex.Message + " Output: " + output);
        }
        catch (Exception ex)
        {
            throw new Exception("Errore imprevisto deserializzazione output Qdrant: " + ex.Message + " Output: " + output);
        }
    }

    private class QdrantRisultato
    {
        public string? testo { get; set; }
        public List<float>? embedding { get; set; }
    }

    public static async Task InserisciInQdrantAsync(List<float> embedding, string testo)
    {
        Console.WriteLine("Sto avviando InserisciInQdrantAsync");
        string scriptPath = @"C:\Users\tommaso.rometti\OneDrive - EOLO SpA\Desktop\TommasoEOlo\TommasoEOlo.AppHost\Embedding\qdrant_service.py";

        var embeddingJson = JsonSerializer.Serialize(embedding);
        var payloadJson = JsonSerializer.Serialize(new { text = testo });

        // Escape doppie virgolette per passaggio come argomento shell
        string embeddingArg = embeddingJson.Replace("\"", "\\\"");
        string payloadArg = payloadJson.Replace("\"", "\\\"");

        var psi = new ProcessStartInfo
        {
            FileName = @"C:\Users\tommaso.rometti\AppData\Local\Programs\Python\Python313\python.exe",
            Arguments = $"\"{scriptPath}\" insert \"{embeddingArg}\" \"{payloadArg}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null)
            throw new Exception("Impossibile avviare lo script Qdrant");

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Console.WriteLine("Qdrant output: " + output + " (InserisciInQdrantAsync) proviene dal backend");
        if (!string.IsNullOrWhiteSpace(error))
            Console.WriteLine("Qdrant error: " + error);

        if (process.ExitCode != 0)
        {
            if (error.Contains("esiste già"))
            {
                Console.WriteLine("Collezione già esistente, non è un errore critico.");
            }
            else
            {
                throw new Exception("Errore script Qdrant: " + error);
            }
        }
    }
}

static class OpenRouterHelper
{
    private static readonly string apiKey = " ";
    private static readonly string apiKeyBackup = " ";
    private static readonly string apiUrl = "https://openrouter.ai/api/v1/chat/completions";

    // HttpClient condiviso per riuso connessioni e miglior performance
    private static readonly HttpClient sharedClient = new()
    {
        Timeout = TimeSpan.FromMinutes(5) // Timeout ragionevole per evitare attese lunghe
    };

    private static int utilizziPrincipale = 0;
    private static int utilizziBackup = 0;
    private static string ultimaChiaveUsata = "nessuna";

    private static readonly Dictionary<string, (int ContextMax, int OutputMax)> ModelliToken = new()
    {
        { "meta-llama/llama-3.3-8b-instruct:free", (128_000, 4_000) },
        { "deepseek/deepseek-chat-v3-0324:free", (164_000, 164_000) },
        { "meta-llama/llama-4-maverick:free", (128_000, 128_000) },
        { "deepseek/deepseek-r1-0528:free", (164_000, 164_000) }
    };

    public static async Task<string> VerificaUtilizziAsync()
    {
        async Task<(bool Success, int? RemainingQuota, string ChiaveUsata)> ControllaQuota(string key, string label)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
                var response = await client.GetAsync("https://openrouter.ai/api/v1/auth/key");
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                    return (false, null, label);

                var json = JsonDocument.Parse(content);
                var remaining = json.RootElement.GetProperty("rate_limit").GetProperty("remaining").GetInt32();
                return (true, remaining, label);
            }
            catch
            {
                return (false, null, label);
            }
        }

        var quotaPrincipale = await ControllaQuota(apiKey, "principale");
        var quotaBackup = await ControllaQuota(apiKeyBackup, "backup");

        var risposta = "**Stato utilizzi:**\n";

        risposta += quotaPrincipale.Success ? $"- Chiave principale: {quotaPrincipale.RemainingQuota} richieste rimanenti\n" : "- Chiave principale: errore nel recupero quota\n";
        risposta += quotaBackup.Success ? $"- Chiave di backup: {quotaBackup.RemainingQuota} richieste rimanenti\n" : "- Chiave di backup: errore nel recupero quota\n";

        string chiaveInUso = quotaPrincipale.Success && quotaPrincipale.RemainingQuota > 0 ? "principale" :
                             quotaBackup.Success && quotaBackup.RemainingQuota > 0 ? "backup" :
                             "nessuna chiave disponibile";

        risposta += $"\n**Ultima chiave usata:** {chiaveInUso}";
        return risposta;
    }

    public static async Task<string> ChiamaOpenRouterAsync(string messaggioOriginale, string modello)
    {
        if (!ModelliToken.ContainsKey(modello))
            return $"Modello non supportato: {modello}";

        var (contextMax, outputMax) = ModelliToken[modello];

        async Task<(bool Success, string Result, HttpStatusCode? StatusCode)> CallApiWithKey(string key, string messaggio, string tipoChiave)
        {
            // Pulizia header personalizzati per evitare duplicati
            sharedClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            if (sharedClient.DefaultRequestHeaders.Contains("HTTP-Referer"))
                sharedClient.DefaultRequestHeaders.Remove("HTTP-Referer");
            if (sharedClient.DefaultRequestHeaders.Contains("X-Title"))
                sharedClient.DefaultRequestHeaders.Remove("X-Title");

            sharedClient.DefaultRequestHeaders.Add("HTTP-Referer", "http://localhost:5156");
            sharedClient.DefaultRequestHeaders.Add("X-Title", "test-chat");

            var oggi = DateTime.Now.ToString("dddd dd MMMM yyyy", new CultureInfo("it-IT"));
            var promptSystem = $"La data attuale è {oggi}.";

            int margineSicurezza = 2000;
            int tokenInput = StimaToken(promptSystem) + StimaToken(messaggio);
            int maxOutputTokens = Math.Min(outputMax, contextMax - tokenInput - margineSicurezza);
            maxOutputTokens = Math.Max(maxOutputTokens, 256); // valore minimo più basso per velocizzare

            if (tokenInput + maxOutputTokens > contextMax)
            {
                int maxInputChars = (contextMax - maxOutputTokens - margineSicurezza) * 4;
                messaggio = messaggio.Substring(0, Math.Min(messaggio.Length, Math.Max(0, maxInputChars)));
                tokenInput = StimaToken(promptSystem) + StimaToken(messaggio);
                maxOutputTokens = Math.Min(outputMax, contextMax - tokenInput - margineSicurezza);
                maxOutputTokens = Math.Max(maxOutputTokens, 256);
            }

            var messaggi = new[]
            {
                new { role = "system", content = promptSystem },
                new { role = "user", content = messaggio }
            };

            var jsonString = JsonSerializer.Serialize(new
            {
                model = modello,
                messages = messaggi,
                temperature = 0.3,  // temperatura abbassata per output più conservativo e veloce
                max_tokens = maxOutputTokens
            });

            var content = new StringContent(jsonString, Encoding.UTF8, "application/json");
            var response = await sharedClient.PostAsync(apiUrl, content);
            var jsonResponse = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return (false, jsonResponse, response.StatusCode);

            try
            {
                var root = JsonDocument.Parse(jsonResponse).RootElement;
                var testoMarkdown = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "(nessuna risposta)";
                var testoHtml = ConvertiMarkdownInHtml(testoMarkdown);

                if (tipoChiave == "principale") utilizziPrincipale++;
                else utilizziBackup++;

                ultimaChiaveUsata = tipoChiave;
                return (true, testoHtml, response.StatusCode);
            }
            catch (Exception ex)
            {
                return (false, $"Errore parsing risposta: {ex.Message}", response.StatusCode);
            }
        }

        var primoTentativo = await CallApiWithKey(apiKey, messaggioOriginale, "principale");
        if (primoTentativo.Success)
            return primoTentativo.Result;

        if (primoTentativo.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var secondoTentativo = await CallApiWithKey(apiKeyBackup, messaggioOriginale, "backup");
            if (secondoTentativo.Success)
                return secondoTentativo.Result;

            if (secondoTentativo.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var retryTime = EstraiRetryTime(secondoTentativo.Result);
                if (retryTime != null)
                    return $"LIMITATA: Hai superato il numero massimo di richieste anche con la chiave di backup. Potrai riprovare il {retryTime:dddd dd MMMM yyyy 'alle' HH:mm:ss}.";

                return "LIMITATA: Hai superato il numero massimo di richieste anche con la chiave di backup. Riprova più tardi.";
            }

            return $"Errore chiamata API con chiave backup: {secondoTentativo.StatusCode}, dettaglio: {secondoTentativo.Result}";
        }

        return $"Errore chiamata API: {primoTentativo.StatusCode}, dettaglio: {primoTentativo.Result}";
    }

    public static async Task<string> GeneraImmagineConStabilityAI(string prompt, string modello)
    {
        Console.WriteLine("Sto avviando GeneraImmagineConStabilityAI");

        var apiKeys = new[]
        {
            "sk-uXtF862dRhyEpMA0TFhOJMXYCQmDAgM3scjHotRmExrJjvmi", // Chiave principale
            "sk-G0NlYqc9JksMv8yB5RmQlrnd29QKZ0qCn7BHfpWOBOTvpuU9"  // Chiave di riserva
        };

        foreach (var apiKey in apiKeys)
        {
            using var client = new HttpClient();
            client.BaseAddress = new Uri("https://api.stability.ai");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            var payload = new
            {
                text_prompts = new[] { new { text = prompt, weight = 1 } },
                cfg_scale = 7,
                height = 512,
                width = 512,
                samples = 1,
                steps = 30
            };

            string endpoint = $"/v1/generation/{modello}/text-to-image";

            try
            {
                var response = await client.PostAsJsonAsync(endpoint, payload);
                var json = await response.Content.ReadAsStringAsync();

                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(json);
                    var base64 = doc.RootElement.GetProperty("artifacts")[0].GetProperty("base64").GetString();

                    return $"data:image/png;base64,{base64}";
                }
                else if ((int)response.StatusCode == 401 || (int)response.StatusCode == 429)
                {
                    Console.WriteLine($"Chiave API fallita ({apiKey[..10]}...): {response.StatusCode}");
                    continue;
                }
                else
                {
                    throw new Exception($"Errore API Stability AI: {response.StatusCode}, {json}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Errore durante la richiesta con la chiave {apiKey[..10]}...: {ex.Message}");
            }
        }

        throw new Exception("Tutte le chiavi API hanno fallito.");
    }

    public static async Task<string> TraduciInInglese(string promptItaliano, string modello)
    {
        var messaggio = $"Traduci in inglese il seguente prompt per l'uso con un generatore di immagini: \"{promptItaliano}\". Rispondi solo con il testo tradotto, senza virgolette o spiegazioni.";

        var traduzione = await ChiamaOpenRouterAsync(messaggio, modello);

        return traduzione.Trim();
    }

    private static DateTime? EstraiRetryTime(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("reset", out var resetElement))
            {
                if (resetElement.ValueKind == JsonValueKind.Number && resetElement.TryGetInt64(out long timestamp))
                    return DateTimeOffset.FromUnixTimeSeconds(timestamp).ToLocalTime().DateTime;

                if (resetElement.ValueKind == JsonValueKind.String && DateTime.TryParse(resetElement.GetString(), out var parsedDate))
                    return parsedDate.ToLocalTime();
            }

            if (doc.RootElement.TryGetProperty("retry_after", out var retryAfterElement) ||
                doc.RootElement.TryGetProperty("retryAfter", out retryAfterElement))
            {
                if (retryAfterElement.ValueKind == JsonValueKind.Number && retryAfterElement.TryGetInt32(out int seconds))
                    return DateTime.Now.AddSeconds(seconds);
            }
        }
        catch { }

        return null;
    }

    private static string ConvertiMarkdownInHtml(string testoMarkdown)
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        return Markdown.ToHtml(testoMarkdown, pipeline);
    }

    private static int StimaToken(string testo) => (int)Math.Ceiling((double)testo.Length / 5);
}

public static class InserimentoFIle
{
    public static string EstraiTestoDaPdf(Stream stream)
    {
        using var pdf = PdfDocument.Open(stream);
        var testo = new StringBuilder();

        foreach (var pagina in pdf.GetPages())
        {
            testo.AppendLine(pagina.Text);
        }

        return testo.ToString();
    }

    public static string EstraiTestoDaWord(Stream stream)
    {
        using var mem = new MemoryStream();
        stream.CopyTo(mem);
        using var doc = WordprocessingDocument.Open(mem, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        return body?.InnerText ?? string.Empty;
    }

    public static string EstraiTestoDaImmagine(Stream stream)
    {
        var pathTessdata = Path.Combine(Directory.GetCurrentDirectory(), "tessdata");
        using var engine = new TesseractEngine(pathTessdata, "ita", EngineMode.Default);
        using var img = Pix.LoadFromMemory(ReadAllBytes(stream));
        using var page = engine.Process(img);
        return page.GetText();
    }

    public static byte[] ReadAllBytes(Stream input)
    {
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return ms.ToArray();
    }
}