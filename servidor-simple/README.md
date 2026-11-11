# Servidor web simple (C# / .NET 8)

Proyecto final de Programación sobre Redes. Todo el servidor está en `Program.cs` y no usa paquetes externos.

## Ejecutar

```
dotnet run
```

Abrir http://localhost:8080

## Configuración (`config.json`)

```json
{
  "Puerto": 8080,
  "CarpetaWeb": "www"
}
```

## Dónde está cada requisito

| Requisito | En el código |
|---|---|
| 1. Concurrencia | `while (true)` + `Task.Run(() => AtenderCliente(cliente))` |
| 2. index.html por defecto | `BuscarArchivo`: si la ruta termina en `/` se agrega `index.html` |
| 3 y 4. Carpeta y puerto | Se leen de `config.json` con `JsonSerializer` |
| 5. 404 personalizado | `BuscarArchivo`: devuelve `www/404.html` con código 404 |
| 6. GET y POST | GET sirve archivos; POST responde "Datos recibidos." y loguea el cuerpo |
| 7. Parámetros de consulta | Se separa la URL en el `?` y los parámetros se loguean |
| 8. Compresión | `Comprimir` con `GZipStream` si el navegador envía `Accept-Encoding: gzip` |
| 9. Log diario con IP | `Loguear`: un archivo `logs/AAAA-MM-DD.log` por día, con `lock` |

Los logs quedan en `bin/Debug/net8.0/logs/`.
