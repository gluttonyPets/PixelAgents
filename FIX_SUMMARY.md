# Fix: Módulo de Texto Ahora Acepta Imágenes sin Prompt de Texto

## Problema
El módulo de texto (`TextModuleHandler`) rechazaba entradas que solo contenían imágenes sin texto, generando el error "Sin prompt de entrada".

### Payload que fallaba:
```json
{
  "input_prompt": [
    {
      "DataType": "image",
      "TextContent": null,
      "Files": [
        {
          "FileId": "1c03c7d7-6a63-41d4-a4e6-944d7157ef90",
          "FileName": "output.png",
          "ContentType": "image/png",
          "FileSize": 2350607
        }
      ],
      "SourcePortId": "output_image"
    }
  ]
}
```

## Solución
Se modificó la validación en `TextModuleHandler.cs` para que:

1. **Antes**: Validaba que hubiera texto ANTES de procesar los archivos
2. **Ahora**: Procesa los archivos primero y luego valida que haya al menos texto O archivos

### Cambios realizados:

#### Archivo modificado: `Server/Services/Ai/Handlers/TextModuleHandler.cs`

**Cambio 1: Validación movida y mejorada**
- La validación ahora ocurre después de procesar los archivos de entrada (línea 76)
- Permite que el prompt esté vacío si hay archivos adjuntos
- Solo falla si no hay ni prompt ni archivos

```csharp
// Antes (líneas 19-20):
if (string.IsNullOrWhiteSpace(prompt))
    return ModuleResult.Failed("Sin prompt de entrada");

// Ahora (líneas 75-77, después de procesar archivos):
// Validate that we have either text input or file input (or both)
if (string.IsNullOrWhiteSpace(prompt) && inputFiles.Count == 0)
    return ModuleResult.Failed("Sin prompt de entrada");
```

**Cambio 2: Formato de salida condicional**
- Las instrucciones de formato solo se agregan si hay un prompt de texto
- Evita concatenar con strings vacíos

```csharp
// Antes (líneas 22-24):
var outgoingFormats = ctx.GetOutgoingFormats();
if (outgoingFormats.Count > 0)
    prompt = OutputSchemaHelper.GetOutputFormatInstruction(outgoingFormats) + "\n\n" + prompt;

// Ahora (líneas 79-81):
var outgoingFormats = ctx.GetOutgoingFormats();
if (outgoingFormats.Count > 0 && !string.IsNullOrWhiteSpace(prompt))
    prompt = OutputSchemaHelper.GetOutputFormatInstruction(outgoingFormats) + "\n\n" + prompt;
```

## Casos de uso soportados:

✅ **Solo texto** - Funciona como antes
```json
{
  "input_prompt": [{"DataType": "text", "TextContent": "Analiza esto"}]
}
```

✅ **Texto + archivos** - Funciona como antes
```json
{
  "input_prompt": [
    {"DataType": "text", "TextContent": "Describe esta imagen"},
    {"DataType": "image", "Files": [...]}
  ]
}
```

✅ **Solo archivos (NUEVO)** - Ahora funciona correctamente
```json
{
  "input_prompt": [{"DataType": "image", "Files": [...]}]
}
```

❌ **Sin entrada** - Correctamente rechazado
```json
{
  "input_prompt": []
}
```

## Impacto
- El módulo de texto ahora puede procesar imágenes sin requerir un prompt de texto
- Los LLMs recibirán la imagen en el campo `InputFiles` del contexto de AI
- El campo `Input` puede ser un string vacío si solo hay archivos
- No afecta la funcionalidad existente de texto o texto+archivos

## Testing
Para probar el cambio:
1. Conectar un módulo de imagen directamente a un módulo de texto
2. Ejecutar el flujo sin proporcionar texto adicional
3. Verificar que el módulo de texto acepta la imagen y la envía al proveedor de AI
