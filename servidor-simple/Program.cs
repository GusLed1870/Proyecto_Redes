using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// ============================================================
//  Servidor web simple - Programación sobre Redes
// ============================================================

// --- Requisitos 3 y 4: puerto y carpeta desde config.json ---
string baseDir = AppContext.BaseDirectory;
var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(Path.Combine(baseDir, "config.json")))!;
string carpetaWeb = Path.GetFullPath(Path.Combine(baseDir, config.CarpetaWeb));
string carpetaLogs = Path.Combine(baseDir, "logs");
Directory.CreateDirectory(carpetaLogs);

object candadoLog = new object(); // para que dos hilos no escriban el log a la vez

TcpListener servidor = new TcpListener(IPAddress.Any, config.Puerto);
servidor.Start();
Console.WriteLine($"Servidor escuchando en http://localhost:{config.Puerto}");
Console.WriteLine($"Sirviendo archivos desde: {carpetaWeb}");

// --- Requisito 1: concurrencia ---
// Cada cliente se atiende en su propia tarea; el bucle vuelve enseguida a aceptar otro.
while (true)
{
    TcpClient cliente = await servidor.AcceptTcpClientAsync();
    _ = Task.Run(() => AtenderCliente(cliente));
}

// ============================================================
//  Atención de un cliente
// ============================================================
void AtenderCliente(TcpClient cliente)
{
    using (cliente)
    {
        string ip = ((IPEndPoint)cliente.Client.RemoteEndPoint!).Address.ToString();
        try
        {
            NetworkStream red = cliente.GetStream();
            red.ReadTimeout = 10000; // si el cliente no manda nada en 10 s, se corta
            var entrada = new BufferedStream(red);

            // 1) Línea de solicitud: "GET /index.html?x=1 HTTP/1.1"
            string? primeraLinea = LeerLinea(entrada);
            if (string.IsNullOrEmpty(primeraLinea)) return;
            string[] partes = primeraLinea.Split(' ');
            if (partes.Length < 3) return;
            string metodo = partes[0].ToUpper();
            string url = partes[1];

            // 2) Encabezados, hasta la línea vacía
            var encabezados = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? linea;
            while (!string.IsNullOrEmpty(linea = LeerLinea(entrada)))
            {
                int dosPuntos = linea.IndexOf(':');
                if (dosPuntos > 0)
                    encabezados[linea[..dosPuntos].Trim()] = linea[(dosPuntos + 1)..].Trim();
            }

            // 3) Cuerpo (solo si hay Content-Length; se lee en BYTES)
            string cuerpo = "";
            if (encabezados.TryGetValue("Content-Length", out string? largoTexto)
                && int.TryParse(largoTexto, out int largo) && largo > 0)
            {
                byte[] buffer = new byte[largo];
                int leidos = 0;
                while (leidos < largo) // puede llegar en varios pedazos
                {
                    int n = entrada.Read(buffer, leidos, largo - leidos);
                    if (n == 0) break;
                    leidos += n;
                }
                cuerpo = Encoding.UTF8.GetString(buffer, 0, leidos);
            }

            // --- Requisito 7: separar ruta y parámetros de consulta ---
            string ruta = url, parametros = "";
            int signo = url.IndexOf('?');
            if (signo >= 0)
            {
                ruta = url[..signo];
                parametros = Uri.UnescapeDataString(url[(signo + 1)..]);
            }
            ruta = Uri.UnescapeDataString(ruta); // "mi%20archivo.html" -> "mi archivo.html"

            // 4) Armar la respuesta según el método
            int codigo;
            string tipo;
            byte[] contenido;

            if (metodo == "GET")
            {
                (codigo, tipo, contenido) = BuscarArchivo(ruta);
            }
            else if (metodo == "POST")
            {
                // --- Requisito 6: en POST solo se loguean los datos ---
                (codigo, tipo, contenido) = (200, "text/plain", Encoding.UTF8.GetBytes("Datos recibidos."));
            }
            else
            {
                (codigo, tipo, contenido) = (405, "text/plain", Encoding.UTF8.GetBytes("Método no permitido."));
            }

            // --- Requisito 8: compresión GZip si el navegador la acepta ---
            bool usarGzip = encabezados.TryGetValue("Accept-Encoding", out string? enc) && enc.Contains("gzip");
            if (usarGzip) contenido = Comprimir(contenido);

            EnviarRespuesta(red, codigo, tipo, contenido, usarGzip);

            // --- Requisito 9: log diario con IP ---
            string registro = $"{DateTime.Now:HH:mm:ss} | {ip} | {metodo} {ruta} | {codigo}";
            if (parametros != "") registro += $" | parámetros: {parametros}";
            if (metodo == "POST") registro += $" | datos: {cuerpo}";
            Loguear(registro);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{ip}] Error: {ex.Message}");
        }
    }
}

// ============================================================
//  Funciones auxiliares
// ============================================================

// Requisitos 2 y 5: archivo por defecto y error 404
(int, string, byte[]) BuscarArchivo(string ruta)
{
    // Requisito 2: si la URL no nombra un archivo, se sirve index.html
    if (ruta.EndsWith("/")) ruta += "index.html";

    string archivo = Path.GetFullPath(Path.Combine(carpetaWeb, ruta.TrimStart('/')));

    // Seguridad: no permitir salir de la carpeta web con "../"
    if (!archivo.StartsWith(carpetaWeb + Path.DirectorySeparatorChar))
        return (403, "text/plain", Encoding.UTF8.GetBytes("Acceso denegado."));

    if (File.Exists(archivo))
        return (200, TipoDeContenido(archivo), File.ReadAllBytes(archivo));

    // Requisito 5: 404 con documento personalizado
    string pagina404 = Path.Combine(carpetaWeb, "404.html");
    byte[] cuerpo404 = File.Exists(pagina404)
        ? File.ReadAllBytes(pagina404)
        : Encoding.UTF8.GetBytes("<h1>404 - Archivo no encontrado</h1>");
    return (404, "text/html", cuerpo404);
}

void EnviarRespuesta(NetworkStream red, int codigo, string tipo, byte[] contenido, bool gzip)
{
    string estado = codigo switch
    {
        200 => "OK", 403 => "Forbidden", 404 => "Not Found", 405 => "Method Not Allowed", _ => "Error"
    };

    // HTTP exige "\r\n" al final de cada línea y una línea vacía antes del cuerpo
    string cabecera = $"HTTP/1.1 {codigo} {estado}\r\n" +
                      $"Content-Type: {tipo}\r\n" +
                      $"Content-Length: {contenido.Length}\r\n" +
                      (gzip ? "Content-Encoding: gzip\r\n" : "") +
                      "Connection: close\r\n\r\n";

    red.Write(Encoding.ASCII.GetBytes(cabecera));
    red.Write(contenido);
}

byte[] Comprimir(byte[] datos)
{
    using var salida = new MemoryStream();
    using (var gzip = new GZipStream(salida, CompressionMode.Compress))
        gzip.Write(datos);
    return salida.ToArray();
}

void Loguear(string texto)
{
    texto = texto.Replace("\r", " ").Replace("\n", " "); // una solicitud = una línea
    Console.WriteLine(texto);
    string archivoDelDia = Path.Combine(carpetaLogs, $"{DateTime.Now:yyyy-MM-dd}.log");
    lock (candadoLog)
    {
        File.AppendAllText(archivoDelDia, texto + Environment.NewLine);
    }
}

// Lee una línea terminada en "\r\n" byte a byte (así no se mezcla con el cuerpo)
static string? LeerLinea(Stream entrada)
{
    var bytes = new List<byte>();
    int b;
    while ((b = entrada.ReadByte()) != -1)
    {
        if (b == '\n') break;
        if (b != '\r') bytes.Add((byte)b);
    }
    if (b == -1 && bytes.Count == 0) return null;
    return Encoding.UTF8.GetString(bytes.ToArray());
}

static string TipoDeContenido(string archivo) => Path.GetExtension(archivo).ToLower() switch
{
    ".html" => "text/html; charset=utf-8",
    ".css" => "text/css; charset=utf-8",
    ".js" => "text/javascript; charset=utf-8",
    ".png" => "image/png",
    ".jpg" or ".jpeg" => "image/jpeg",
    _ => "application/octet-stream"
};

class Config
{
    public int Puerto { get; set; } = 8080;
    public string CarpetaWeb { get; set; } = "www";
}
