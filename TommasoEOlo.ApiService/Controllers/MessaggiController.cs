using Microsoft.AspNetCore.Mvc;
using System.IO;

namespace TommasoEOlo_ApiService.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class MessaggiController : ControllerBase
    {
        public class MessaggioDTO
        {
            public string Messaggio { get; set; }
        }

        // Endpoint POST che riceve un messaggio e risponde
        [HttpPost]
        public IActionResult Post([FromBody] MessaggioDTO messaggio)
        {
            return Ok(new { Risposta = $"Hai scritto: {messaggio.Messaggio}" });
        }

        // Nuovo endpoint GET per leggere dati.txt
        [HttpGet("dati")]
        public IActionResult GetDati()
        {
            var path = "dati.txt"; // metti qui il percorso corretto del file dati.txt

            if (!System.IO.File.Exists(path))
                return NotFound("File dati.txt non trovato.");

            var contenuto = System.IO.File.ReadAllText(path);

            return Ok(new { Dati = contenuto });
        }
    }
}